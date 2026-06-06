using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Arda.World.Player;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Capture.Diagnostics;
using Mithril.MapCalibration.Detection;
using Mithril.Shared.Diagnostics.Telemetry;
using Mithril.Shared.Game;
using Mithril.Shared.MapCalibration;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// One auto-calibration attempt for the current area (spec §4): resolve area +
/// window + framed bbox, capture under the blanked overlay, refine the map
/// sub-rect, resolve the area's base texture + references, run the detect→solve→
/// gate engine, and persist the solved transform via
/// <see cref="IMapCalibrationService.SaveUserRefinement"/> (stamped
/// <see cref="CalibrationSource.AutoCapture"/>) ONLY when the gate accepts.
///
/// <para><b>Fail-soft everywhere</b> (spec §11): no current area / no bbox / PG
/// not foreground / bad capture / null base texture / low-confidence solve → keep
/// the prior calibration, return a reason for status surfacing, NEVER persist a
/// wrong transform.</para>
///
/// <para><b>Base-texture policy (Task 21 / Decision D8).</b> The base texture is
/// resolved from <see cref="IBaseTextureProvider"/> (the #931 seam over the
/// out-of-process sidecar cache — this layer writes NO provider code). On a
/// cache-miss (null) and when an <see cref="IAssetExtractor"/> is wired, invoke
/// the sidecar once to populate the cache, then retry the provider. If still
/// null, fail-soft with a "preparing map assets…" reason (no texture → no
/// detections → gate rejects → safe-degrade).</para>
///
/// <para><b>Icon-template policy (#949).</b> Icon templates resolve from
/// <see cref="IIconTemplateProvider"/> <i>per attempt</i> (it re-reads the cache
/// each call — the icon analogue of the base-texture provider). On an empty set and
/// when an <see cref="IAssetExtractor"/> is wired, the sidecar's <c>--icons</c> mode
/// is demand-triggered once to populate the cache, then the set is re-resolved — so
/// first-session calibration succeeds on a fresh icon cache without a restart. A
/// still-empty set fails soft: no typed detections → the gate rejects.</para>
///
/// <para><b>Diagnostic bundle.</b> Each public attempt is wrapped in a
/// <c>try { … } finally { sink.Write(attempt); }</c> so the sink receives the
/// partial context on every exit path — success, gate-reject, exception, or
/// cancellation. The active sink is resolved per attempt from
/// <see cref="CalibrationAttemptBundleSinkSelector"/> so the toggle in Settings
/// takes effect without an app restart.</para>
/// </summary>
public sealed class AutoCalibrationEngine : IAutoCalibrationRunner
{
    // Proven Phase-1 detection recipe (§0): the gate-study sweet-spot for real
    // assets. RenderSizePx 16 is the empirical icon render size.
    private const int RenderSizePx = 16;
    private const double LowNcc = 0.5;
    private const double TypeFloor = 0.80;
    private static readonly BlobOptions BlobOpts = new(
        MinArea: 12, MaxIconArea: 900, MinSolidity: 0.35, MaxAspect: 2.5, MinPeak: 0.7);

    // mithril#1046 §6.3: drift-check thresholds.
    private const double DriftToleranceFactor = 3.0;
    private const double DriftMatchGatePx = 20.0;
    private const int DriftMinMatchedReferences = 3;

    private readonly IMapState _mapState;
    private readonly ISceneAssetCache _sceneCache;
    private readonly IGameWindowLocator _windowLocator;
    private readonly IMapCaptureRegionProvider _region;
    private readonly ICaptureService _capture;
    private readonly IMapRegionRefiner _refiner;
    private readonly IBaseTextureProvider _baseTextures;
    private readonly IAreaReferenceProvider _references;
    private readonly IMapCalibrationSolver _solver;
    private readonly IIconTemplateProvider _iconTemplates;
    private readonly IMapCalibrationService _calibrationService;
    private readonly CalibrationAttemptBundleSinkSelector _sinkSelector;
    private readonly ILogger? _logger;

    // Optional Task-21 sidecar policy (null in unit branch tests + when no
    // extractor is wired): on a base-texture cache miss, populate the cache then
    // retry. GameConfig supplies the PG install root for the extract request.
    private readonly IAssetExtractor? _assetExtractor;
    private readonly GameConfig? _gameConfig;
    private readonly string? _assetCacheDir;
    private readonly string? _pgVersion;

    public AutoCalibrationEngine(
        IMapState mapState,
        ISceneAssetCache sceneCache,
        IGameWindowLocator windowLocator,
        IMapCaptureRegionProvider region,
        ICaptureService capture,
        IMapRegionRefiner refiner,
        IBaseTextureProvider baseTextures,
        IAreaReferenceProvider references,
        IMapCalibrationSolver solver,
        IIconTemplateProvider iconTemplates,
        IMapCalibrationService calibrationService,
        ILogger? logger,
        CalibrationAttemptBundleSinkSelector? sinkSelector = null,
        IAssetExtractor? assetExtractor = null,
        GameConfig? gameConfig = null,
        string? assetCacheDir = null,
        string? pgVersion = null)
    {
        _mapState = mapState;
        _sceneCache = sceneCache;
        _windowLocator = windowLocator;
        _region = region;
        _capture = capture;
        _refiner = refiner;
        _baseTextures = baseTextures;
        _references = references;
        _solver = solver;
        _iconTemplates = iconTemplates;
        _calibrationService = calibrationService;
        _sinkSelector = sinkSelector ?? new CalibrationAttemptBundleSinkSelector(
            new CaptureDiagnosticsOptions(), NullCalibrationAttemptBundleSink.Instance,
            NullCalibrationAttemptBundleSink.Instance);
        _logger = logger;
        _assetExtractor = assetExtractor;
        _gameConfig = gameConfig;
        _assetCacheDir = assetCacheDir;
        _pgVersion = pgVersion;
    }

    /// <summary>
    /// The downloaded <c>classdata.tpk</c> path inside the asset cache, or null when
    /// it isn't present yet (#960). Threaded into <see cref="ExtractRequest.TpkPath"/>
    /// so the sidecar can decode icons; when null the sidecar falls back to its old
    /// resolution and fail-softs exactly as before.
    /// </summary>
    private string? ResolveTpkPath()
    {
        if (string.IsNullOrWhiteSpace(_assetCacheDir)) return null;
        var tpk = Path.Combine(_assetCacheDir, ClassDataTpkProvisioner.TpkFileName);
        return File.Exists(tpk) ? tpk : null;
    }

    /// <inheritdoc/>
    public async Task<DriftCheckOutcome> CheckDriftAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var span = MithrilActivitySources.MapCalibration.StartActivity("calibration.drift_check");

        // Step 1: resolve scene.
        var resolvedScene = SceneResolution.ResolveCurrentScene(_mapState, _sceneCache);
        if (resolvedScene is null)
        {
            span?.SetTag("outcome", "NoStoredCalibration");
            return new DriftCheckOutcome.NoStoredCalibration();
        }
        var sceneRef = resolvedScene.Value;
        span?.SetTag("map.area", sceneRef.MapAssetKey);

        // Step 2: resolve stored calibration.
        var stored = _calibrationService.GetCalibration(sceneRef);
        if (stored is null)
        {
            span?.SetTag("outcome", "NoStoredCalibration");
            return new DriftCheckOutcome.NoStoredCalibration();
        }

        // mithril#1076 frame-aware refusal (spec §2.4 / §13 P.1b): the drift
        // check projects through a texture-frame calibration then converts to
        // crop frame for comparison against the detector's anchors. If the
        // active stored record is overlay-frame (a Legolas-wizard fit per
        // spec §7.2) there is no texture-frame source to project through —
        // running the locate→detect→compare pipeline would silently produce
        // 0/N matches on whatever record happens to be stored. Refuse honestly
        // before any capture / refine / detect work runs. Post-#1082 the
        // coordinator no longer reaches this branch on a record-less scene
        // (it gates on GetTextureCalibration before calling CheckDriftAsync);
        // the outcome remains as defense-in-depth and is handled as a
        // race-fallback in ManualCalibrationCoordinator.
        if (_calibrationService.GetTextureCalibration(sceneRef) is null)
        {
            _logger?.LogInformation(
                "Drift check {MapAssetKey}: no texture-frame calibration record — refusing to run; coordinator treats this as race-fallback (mithril#1082).",
                sceneRef.MapAssetKey);
            span?.SetTag("outcome", "NoTextureFrameRecord");
            return new DriftCheckOutcome.NoTextureFrameRecord();
        }

        // Step 3a: bbox gate.
        var bbox = _region.Current;
        if (bbox is null)
        {
            var capReason = "no map bbox set — use the draw-map-bbox hotkey first";
            _logger?.LogInformation(
                "Drift check {MapAssetKey}: {Failure} ({Reason}). No arming; chip shows actionable reason.",
                sceneRef.MapAssetKey, "capture-failed", capReason);
            span?.SetTag("outcome", "CaptureFailed");
            return new DriftCheckOutcome.CaptureFailed(capReason);
        }

        // Step 3b: PG-foreground gate.
        if (_windowLocator.Locate() is null)
        {
            var capReason = "Project Gorgon is not the foreground window";
            _logger?.LogInformation(
                "Drift check {MapAssetKey}: {Failure} ({Reason}). No arming; chip shows actionable reason.",
                sceneRef.MapAssetKey, "capture-failed", capReason);
            span?.SetTag("outcome", "CaptureFailed");
            return new DriftCheckOutcome.CaptureFailed(capReason);
        }

        // Step 5: resolve references (logged with starting message below).
        var references = _references.ForArea(sceneRef);
        _logger?.LogInformation(
            "Drift check starting for {MapAssetKey}: {Refs} references, tolerance factor {Factor}× of stored {Residual:0.00}px.",
            sceneRef.MapAssetKey, references.Count, DriftToleranceFactor, stored.ResidualPixels);

        // Step 3c: capture gate.
        var captureResult = await _capture.CaptureMapAsync(bbox.Value, ct).ConfigureAwait(false);
        if (captureResult.Gray is null)
        {
            var capReason = "map capture failed (black/wrong-size frame)";
            _logger?.LogInformation(
                "Drift check {MapAssetKey}: {Failure} ({Reason}). No arming; chip shows actionable reason.",
                sceneRef.MapAssetKey, "capture-failed", capReason);
            span?.SetTag("outcome", "CaptureFailed");
            return new DriftCheckOutcome.CaptureFailed(capReason);
        }
        var gray = captureResult.Gray;

        // Base-texture gate.
        var baseTexture = await ResolveBaseTextureAsync(sceneRef.MapAssetKey, ct).ConfigureAwait(false);
        if (baseTexture is null)
        {
            var texReason = "base texture unavailable";
            _logger?.LogInformation(
                "Drift check {MapAssetKey}: {Failure} ({Reason}). No arming; chip shows actionable reason.",
                sceneRef.MapAssetKey, "map-not-located", texReason);
            span?.SetTag("outcome", "MapNotLocated");
            return new DriftCheckOutcome.MapNotLocated(texReason);
        }

        // Step 4: run locator/refiner.
        // mithril#1061: IAreaContextualRefiner replaces the concrete-type cast so
        // CompositeMapRegionRefiner can transparently forward to its inner FM
        // refiner (which is what populates the per-area ORB-descriptor cache key).
        if (_refiner is IAreaContextualRefiner driftCtx)
            driftCtx.SetAreaKey(sceneRef.ParentAreaKey);
        var refineResult = _refiner.Refine(gray, baseTexture);
        if (refineResult.AcceptedRect is null || refineResult.Metrics is null)
        {
            var locReason = refineResult.Metrics is { } failM
                ? $"locator inliers={failM.InlierCount}/{failM.CandidateCount} scale={failM.Scale:0.00}"
                : "no fit";
            _logger?.LogInformation(
                "Drift check {MapAssetKey}: {Failure} ({Reason}). No arming; chip shows actionable reason.",
                sceneRef.MapAssetKey, "map-not-located", locReason);
            span?.SetTag("outcome", "MapNotLocated");
            return new DriftCheckOutcome.MapNotLocated(locReason);
        }
        var loc = refineResult.Metrics;
        _logger?.LogInformation(
            "Drift check {MapAssetKey}: locator scale={Scale:0.000}, rotation={Rot:0.00}°, inliers={Inliers}/{Cand}, locator residual={LocResid:0.00}px.",
            sceneRef.MapAssetKey, loc.Scale, loc.RotationDegrees, loc.InlierCount, loc.CandidateCount, loc.ResidualPixels);

        // Build aligned detection inputs (mirrors RunAttemptCoreAsync §978).
        var templates = await EnsureIconTemplatesAsync(ct).ConfigureAwait(false);
        var clamped = ClampToFrame(refineResult.AcceptedRect, gray.Width, gray.Height);
        if (clamped is null)
        {
            var clampReason = "the located map rect fell outside the captured frame";
            _logger?.LogInformation(
                "Drift check {MapAssetKey}: {Failure} ({Reason}). No arming; chip shows actionable reason.",
                sceneRef.MapAssetKey, "map-not-located", clampReason);
            span?.SetTag("outcome", "MapNotLocated");
            return new DriftCheckOutcome.MapNotLocated(clampReason);
        }
        var crop = ImageOps.Crop(gray, clamped.OriginX, clamped.OriginY, clamped.Width, clamped.Height);
        var alignedTexture = ImageOps.Resize(baseTexture, clamped.Width, clamped.Height);
        var alignedRect = new MapRect(0, 0, clamped.Width, clamped.Height, clamped.TextureWidth, clamped.TextureHeight);
        var detectionRequest = new DetectionRequest(
            Screenshot: crop,
            BaseTexture: alignedTexture,
            MapRect: alignedRect,
            Templates: templates,
            RimMask: RimMaskMode.DeviationFlood,
            LowNcc: LowNcc,
            TypeFloor: TypeFloor,
            BlobOptions: BlobOpts)
        {
            RenderSizePx = RenderSizePx,
        };

        // Step 6: run typed icon detector only (no geometric solve).
        var detections = _solver.DetectOnly(detectionRequest);
        if (detections.Count == 0)
        {
            span?.SetTag("outcome", "NoIconDetections");
            return new DriftCheckOutcome.NoIconDetections();
        }

        // Step 7: pair each reference against nearest detection within the gate.
        // Each detection may claim at most one reference (greedy nearest-first
        // prevents a single detection from boosting the matched count artificially
        // when references are close together).
        //
        // mithril#1076 fix: compare predictions and detections in the SAME frame
        // (CroppedFramePixel — the frame the detector emits anchors in). The
        // pre-fix code projected predictions into TEXTURE space and then added
        // (loc.Tx, loc.Ty) to land in CAPTURED-FRAME space — but the detector
        // emits anchors in CROP-FRAME space (the screenshot it consumed is the
        // cropped sub-rect). The mismatch was exactly (loc.Tx, loc.Ty); on the
        // catalyst Map_KhyruleksCrypt 2026-06-04 attempt that was (320.1, 57.6),
        // pushing every reference outside DriftMatchGatePx=20 → 0/N matched.
        //
        // Wrap the stored AreaCalibration (texture-frame, see InferFrameFromSource
        // in MapCalibrationService) into the typed projection struct, project to
        // TEXTURE space, then map texture→crop via alignedRect — landing in the
        // same frame as `d.Anchor`. The type system now forbids the old shape:
        // TexturePixel.DistanceTo(CroppedFramePixel) doesn't compile.
        var storedTexCal = new WorldToTextureCalibration(
            stored.OriginX, stored.OriginY, stored.Scale, stored.RotationRadians,
            stored.MirrorNorth);

        var usedDetectionIndices = new HashSet<int>(detections.Count);
        var residuals = new List<double>(references.Count);
        foreach (var r in references)
        {
            // Predict in TEXTURE space (where the stored calibration solves):
            TexturePixel predTex = storedTexCal.ToTexture(r.World);

            // Convert to CROP space — same frame as TypedDetection.Anchor.
            CroppedFramePixel predCrop = alignedRect.TextureToCropped(predTex);

            double? best = null;
            int bestIdx = -1;
            for (int di = 0; di < detections.Count; di++)
            {
                if (usedDetectionIndices.Contains(di)) continue;
                var dist = predCrop.DistanceTo(detections[di].Anchor);  // type-safe, same frame
                if (dist < (best ?? double.MaxValue))
                {
                    best = dist;
                    bestIdx = di;
                }
            }
            if (best is null || best.Value > DriftMatchGatePx) continue;
            usedDetectionIndices.Add(bestIdx);
            residuals.Add(best.Value);
            _logger?.LogTrace(
                "Drift check {MapAssetKey}: ref '{Name}' predicted=({Px:0.0},{Py:0.0}), nearest detection=({Dx:0.0},{Dy:0.0}) at {Dist:0.00}px.",
                sceneRef.MapAssetKey, r.Name,
                predCrop.X, predCrop.Y,
                detections[bestIdx].Anchor.X, detections[bestIdx].Anchor.Y,
                best.Value);
        }

        span?.SetTag("refs.matched", residuals.Count);

        // Step 8: aggregate.
        if (residuals.Count < DriftMinMatchedReferences)
        {
            _logger?.LogInformation(
                "Drift check {MapAssetKey}: inconclusive — {Reason} ({Matched} refs matched, need ≥{Min}). No arming.",
                sceneRef.MapAssetKey, "too few visible landmarks", residuals.Count, DriftMinMatchedReferences);
            span?.SetTag("outcome", "Inconclusive");
            return new DriftCheckOutcome.Inconclusive("too few visible landmarks", residuals.Count);
        }

        var maxResidual = residuals.Max();
        var threshold = DriftToleranceFactor * stored.ResidualPixels;
        span?.SetTag("max_residual_px", maxResidual);
        span?.SetTag("threshold_px", threshold);

        if (maxResidual > threshold)
        {
            _logger?.LogWarning(
                "Drift check {MapAssetKey}: DRIFT detected ({Matched} refs matched, max residual {MaxResid:0.00}px exceeds threshold {Threshold:0.00}px). Hotkey armed for {Arm}s — re-press to recalibrate.",
                sceneRef.MapAssetKey, residuals.Count, maxResidual, threshold, ManualCalibrationCoordinator.ArmingSeconds);
            span?.SetTag("outcome", "Drift");
            return new DriftCheckOutcome.Drift(maxResidual, residuals.Count, threshold);
        }

        _logger?.LogInformation(
            "Drift check {MapAssetKey}: OK ({Matched} refs matched, max residual {MaxResid:0.00}px, threshold {Threshold:0.00}px). No recalibration needed.",
            sceneRef.MapAssetKey, residuals.Count, maxResidual, threshold);
        span?.SetTag("outcome", "Ok");
        return new DriftCheckOutcome.Ok(maxResidual, residuals.Count);
    }

    /// <summary>
    /// Thin public entry point. Wraps <see cref="RunAttemptCoreAsync"/> in a
    /// <c>try/finally</c> so <see cref="ICalibrationAttemptBundleSink.Write"/> is
    /// called on every exit path: success, gate-reject, exception, or cancellation.
    /// The sink is resolved per attempt so a live Settings toggle takes effect
    /// without an app restart.
    /// </summary>
    public async Task<AutoCalibrationOutcome> TryCalibrateCurrentAreaAsync(CancellationToken ct)
    {
        // Resolution cascade (mithril#1041 D3): live IMapState.CurrentMapScene is
        // preferred; on null, the SceneAssetCache supplies the seeded/learned
        // fallback for known areas; on a still-null result the strict gate
        // refuses outright. Fires BEFORE any side effect (no bundle write, no
        // attempt context, no texture/solver invocation).
        var resolvedScene = SceneResolution.ResolveCurrentScene(_mapState, _sceneCache);
        if (resolvedScene is null)
        {
            _logger?.LogInformation(
                "Auto-calibration refused: per-scene map asset not yet known (Area={Area}); change zones once or restart while in this scene.",
                _mapState.CurrentArea ?? "<none>");
            return new AutoCalibrationOutcome(
                Persisted: false,
                AreaKey: _mapState.CurrentArea ?? string.Empty,
                RejectReason: OutcomeVocabulary.MapAssetNotYetKnown,
                OutcomeCategory: OutcomeVocabulary.MapAssetNotYetKnown);
        }

        // From here on, sceneRef.MapAssetKey is the authoritative per-scene key
        // (#1021): texture lookup, sidecar requests, attempt-bundle naming.
        var sceneRef = resolvedScene.Value;
        var assetKey = sceneRef.MapAssetKey;
        var attempt = new CalibrationAttemptContext(assetKey, DateTimeOffset.UtcNow);
        var sink = _sinkSelector.Resolve();
        try
        {
            return await RunAttemptCoreAsync(attempt, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            attempt.Outcome = OutcomeVocabulary.Error;
            attempt.ExceptionInfo = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            attempt.Outcome = OutcomeVocabulary.Error;
            attempt.ExceptionInfo = $"{ex.GetType().Name}: {ex.Message}";
            throw;
        }
        finally
        {
            sink.Write(attempt); // fail-soft inside Write — never throws into the engine
        }
    }

    /// <summary>
    /// The full pipeline body. Property assignments feed the
    /// <see cref="CalibrationAttemptContext"/> passed in from
    /// <see cref="TryCalibrateCurrentAreaAsync"/> so the finally-block sink receives
    /// whatever was accumulated before the return/throw.
    /// </summary>
    private async Task<AutoCalibrationOutcome> RunAttemptCoreAsync(
        CalibrationAttemptContext attempt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Per-attempt trace span (#914). Null when no listener is attached (no OTLP
        // export / no perf-recording), so this is zero-overhead when off. Child
        // capture/refine/solve spans nest under it → a Seq waterfall showing which
        // step is slow.
        using var actSpan = MithrilActivitySources.MapCalibration.StartActivity("calibration.attempt");

        // #1041: the resolved scene composite carries every key the pipeline needs.
        // The bundle/log "Area" tag in attempt.Area is the per-scene asset key (the
        // bundle subfolder + 01-attempt.json read it); ParentAreaKey scopes the
        // landmark/NPC filter for aggregator scenes (e.g. AreaCave1 → Hogan's
        // Basement). Resolved upstream by TryCalibrateCurrentAreaAsync via
        // SceneResolution.ResolveCurrentScene.
        var assetKey = attempt.Area;
        var resolvedScene = SceneResolution.ResolveCurrentScene(_mapState, _sceneCache);
        if (resolvedScene is not { } sceneRef || string.IsNullOrWhiteSpace(sceneRef.ParentAreaKey))
        {
            attempt.Outcome = OutcomeVocabulary.RejectedNoArea;
            return Fail("", "not in-world — open Project Gorgon and enter an area first", OutcomeVocabulary.RejectedNoArea);
        }
        var area = sceneRef.ParentAreaKey;

        // PG-foreground gate: capture must read the game's framebuffer, not
        // another app's. (The hotkey already focus-gates; the auto path + manual
        // path both re-check here so neither can capture the wrong window.)
        if (_windowLocator.Locate() is null)
        {
            attempt.Outcome = OutcomeVocabulary.RejectedPgNotForeground;
            return Fail(area, "Project Gorgon is not the foreground window", OutcomeVocabulary.RejectedPgNotForeground);
        }

        var bbox = _region.Current;
        if (bbox is null)
        {
            attempt.Outcome = OutcomeVocabulary.RejectedNoBbox;
            return Fail(area, "no map bbox set — use the draw-map-bbox hotkey first", OutcomeVocabulary.RejectedNoBbox);
        }

        actSpan?.SetTag("map.area", area);
        _logger?.LogInformation(
            "Auto-calibration {Area}: capturing map region {Width}x{Height} at ({X},{Y})…",
            area, bbox.Value.Width, bbox.Value.Height, bbox.Value.X, bbox.Value.Y);

        CaptureMapResult captureResult;
        using (var captureAct = MithrilActivitySources.MapCalibration.StartActivity("calibration.capture"))
        {
            captureAct?.SetTag("bbox.width", bbox.Value.Width);
            captureAct?.SetTag("bbox.height", bbox.Value.Height);
            captureResult = await _capture.CaptureMapAsync(bbox.Value, ct).ConfigureAwait(false);
            captureAct?.SetTag("capture.ok", captureResult.Gray is not null);
        }

        attempt.RawCapture = captureResult.Color;
        attempt.GrayCapture = captureResult.Gray;

        if (captureResult.Gray is null)
        {
            attempt.Outcome = OutcomeVocabulary.RejectedCaptureFailed;
            return Fail(area, "map capture failed or was rejected (black / wrong-size frame)", OutcomeVocabulary.RejectedCaptureFailed);
        }

        var gray = captureResult.Gray;

        _logger?.LogInformation(
            "Auto-calibration {Area} ({MapAsset}): captured {Width}x{Height} frame; resolving base texture…",
            area, assetKey, gray.Width, gray.Height);
        var baseTexture = await ResolveBaseTextureAsync(assetKey, ct).ConfigureAwait(false);
        if (baseTexture is null)
        {
            attempt.Outcome = OutcomeVocabulary.RejectedNoBaseTexture;
            return Fail(area, "preparing map assets… (base texture unavailable — no detections possible)", OutcomeVocabulary.RejectedNoBaseTexture);
        }

        // Locate the map within the captured frame. Under the production refiner
        // (FeatureMatchingRefiner, PR-4 Task 17) this is ORB + RANSAC; under the
        // legacy NCC refiner it's an NCC scale ladder. Both are synchronous and
        // can take a noticeable moment on a cold call — bracket with before/after
        // + timing so a slow or stalled refine is visible (the attempt previously
        // went dark after "Loaded base texture …").
        _logger?.LogInformation(
            "Auto-calibration {Area}: locating the map within the captured frame…", area);
        var refineStart = Stopwatch.GetTimestamp();
        MapRegionRefineResult refineResult;
        // PR-4 Task 17: the FM refiner reads/writes a per-area ORB descriptor
        // cache keyed on the area name. SetAreaKey isn't on IMapRegionRefiner
        // (the interface stays narrow — no cache-key arg on every call); it's a
        // runtime cast because there's exactly one production refiner type, and
        // any other IMapRegionRefiner (test fakes, legacy NCC) safely skips the
        // cache pre-warm.
        // mithril#1061: IAreaContextualRefiner replaces the concrete-type cast so
        // CompositeMapRegionRefiner can transparently forward to its inner FM
        // refiner (which is what populates the per-area ORB-descriptor cache key).
        if (_refiner is IAreaContextualRefiner refinerCtx)
        {
            refinerCtx.SetAreaKey(area);
        }
        using (var refineAct = MithrilActivitySources.MapCalibration.StartActivity("calibration.refine"))
        {
            refineResult = _refiner.Refine(gray, baseTexture);
            refineAct?.SetTag("map.located", refineResult.AcceptedRect is not null);
        }
        // Surface the raw fit rect + metrics on EITHER branch — the diagnostic bundle
        // reads these so a rejected-map-not-located is self-triaging. The production
        // FeatureMatchingRefiner populates Metrics on both accept + reject paths
        // (PR-4 Task 17 cutover). Metrics may still be null under a non-FM refiner
        // (test fakes / direct unit-test wiring).
        attempt.LocatorRawFit = refineResult.RawFitRect;
        attempt.LocatorMetrics = refineResult.Metrics;
        var mapRect = refineResult.AcceptedRect;
        if (mapRect is null)
        {
            // mithril#1061: distinguish low-confidence fallback rejects (input
            // pathology — try a different zoom / explore more) from ORB primary's
            // "no fit at all" (framing problem — zoom out / re-draw the box).
            // A low-confidence reject still has Metrics with Confidence populated;
            // a no-fit reject from ORB has Metrics null or InlierCount-driven.
            bool lowConfidenceFallback =
                refineResult.Metrics is { Provenance: LocateProvenance.SobelPaddedPyramid, Confidence: not null };

            if (refineResult.Metrics is { } m)
            {
                if (m.Provenance == LocateProvenance.SobelPaddedPyramid)
                {
                    _logger?.LogInformation(
                        "Auto-calibration {Area}: locate rejected — fallback NCC={Ncc:0.000} < floor, scale={Scale:0.000}, tx={Tx:0.0}, ty={Ty:0.0}.",
                        area, m.Confidence ?? 0, m.Scale, m.Tx, m.Ty);
                }
                else
                {
                    _logger?.LogInformation(
                        "Auto-calibration {Area}: locate rejected — inliers={Inliers}/{Cand} ratio={Ratio:0.000}, scale={Scale:0.000}, rotation={Rot:0.000}°.",
                        area, m.InlierCount, m.CandidateCount, m.InlierRatio, m.Scale, m.RotationDegrees);
                }
            }
            else if (refineResult.RawFitRect is { } best)
            {
                _logger?.LogInformation(
                    "Auto-calibration {Area}: locate rejected — raw fit rect at origin = ({X}, {Y}), size = {W}x{H}.",
                    area, best.OriginX, best.OriginY, best.Width, best.Height);
            }

            if (lowConfidenceFallback)
            {
                attempt.Outcome = OutcomeVocabulary.RejectedMapLowConfidence;
                return Fail(area,
                    "couldn't locate the map confidently — try a different zoom or explore more of the area first",
                    OutcomeVocabulary.RejectedMapLowConfidence);
            }

            attempt.Outcome = OutcomeVocabulary.RejectedMapNotLocated;
            return Fail(area, "couldn't locate the map in the captured frame — zoom the in-game map all the way out and draw the capture box tightly around the map", OutcomeVocabulary.RejectedMapNotLocated);
        }
        attempt.MapRect = mapRect;
        _logger?.LogInformation(
            "Auto-calibration {Area}: map sub-rect located ({MapRect}) in {ElapsedMs:0} ms.",
            area, mapRect, Stopwatch.GetElapsedTime(refineStart).TotalMilliseconds);

        // #1021: scene-aware reference lookup. ParentAreaKey scopes landmarks
        // (landmarks.json has no sub-zone field) and the NPC parent filter;
        // SceneFriendlyName further narrows the NPC filter for aggregator
        // scenes (e.g. AreaCave1 → Hogan's Basement) so the solver doesn't
        // pair the captured Texture2D against every NPC under the aggregator.
        var references = _references.ForArea(sceneRef);
        attempt.References = references;
        _logger?.LogInformation(
            "Auto-calibration {Area} ({MapAsset}, scene={Scene}): {ReferenceCount} landmark reference(s).",
            area, assetKey, sceneRef.SceneFriendlyName ?? "<none>", references.Count);

        // Resolve icon templates per attempt (#949). On a fresh icon cache the
        // provider returns Empty; if a sidecar is wired, demand-trigger its --icons
        // mode ONCE to populate the cache, then re-resolve — so first-session
        // calibration works without a restart. Fail-soft: still-Empty → no typed
        // detections → the gate rejects → safe-degrade.
        var templates = await EnsureIconTemplatesAsync(ct).ConfigureAwait(false);

        // #978 ALIGNED detection inputs. The ECC-refined rect is sub-pixel-accurate,
        // so cropping the captured frame to it and resampling the base texture to the
        // same size makes the two pixel-register — terrain cancels in the deviation
        // map and only the added icons survive (the coarse rect floods with terrain
        // false-positives). The rect handed to the request is crop-anchored
        // ((0,0)+crop size) but carries the FULL texture dims, so the solver's
        // ScreenshotToTexture still maps crop pixels into full-texture world space.
        //
        // Guard the crop against an ECC rect that ran slightly past the frame edge:
        // clamp origin ≥0 and origin+size ≤ frame dims (fail-soft, never throw). A
        // degenerate (empty) clamped rect → reject this attempt with a reason.
        var clamped = ClampToFrame(mapRect, gray.Width, gray.Height);
        if (clamped is null)
        {
            attempt.Outcome = OutcomeVocabulary.RejectedClampDegenerate;
            return Fail(area, "the located map rect fell outside the captured frame — redraw the capture box tightly around the in-game map", OutcomeVocabulary.RejectedClampDegenerate);
        }

        // #989: the bundle sink reads attempt.MapRect to write 04-maprect.json.
        // The detect→solve pipeline below operates on the CLAMPED rect (crop,
        // alignedTexture, alignedRect all derive from `clamped`), so the bundle
        // JSON must describe the same dims the deviation/aligned/base-texture
        // images carry — not the pre-clamp ECC value that overshoots the frame.
        attempt.MapRect = clamped;

        var crop = ImageOps.Crop(gray, clamped.OriginX, clamped.OriginY, clamped.Width, clamped.Height);
        var alignedTexture = ImageOps.Resize(baseTexture, clamped.Width, clamped.Height);
        var alignedRect = new MapRect(0, 0, clamped.Width, clamped.Height, clamped.TextureWidth, clamped.TextureHeight);

        attempt.AlignedCrop = crop;
        attempt.AlignedTexture = alignedTexture;
        attempt.BaseTextureResampled = alignedTexture; // same data, distinct semantic slot per spec

        var request = new DetectionRequest(
            Screenshot: crop,
            BaseTexture: alignedTexture,
            MapRect: alignedRect,
            Templates: templates,
            RimMask: RimMaskMode.DeviationFlood,
            LowNcc: LowNcc,
            TypeFloor: TypeFloor,
            BlobOptions: BlobOpts)
        {
            RenderSizePx = RenderSizePx,
        };

        _logger?.LogInformation(
            "Auto-calibration {Area}: running detect→solve ({TemplateCount} icon template(s), {ReferenceCount} reference(s))…",
            area, templates.Templates.Count, references.Count);
        var solveStart = Stopwatch.GetTimestamp();
        CalibrationSolveResult result;
        using (var solveAct = MithrilActivitySources.MapCalibration.StartActivity("calibration.solve"))
        {
            solveAct?.SetTag("templates", templates.Templates.Count);
            solveAct?.SetTag("references", references.Count);
            result = _solver.Solve(request, references);
            solveAct?.SetTag("solve.inliers", result.InlierCount);
            solveAct?.SetTag("solve.calibrated", result.Calibration is not null);
            if (result.Calibration is not null)
            {
                solveAct?.SetTag("solve.residual_px", result.Calibration.ResidualPixels);
            }
        }
        _logger?.LogInformation(
            "Auto-calibration {Area}: solve finished in {ElapsedMs:0} ms (calibration {HasCalibration}, {Inliers} inlier(s)).",
            area, Stopwatch.GetElapsedTime(solveStart).TotalMilliseconds, result.Calibration is not null, result.InlierCount);

        attempt.Detections = result.Detections;
        attempt.Result = result;

        if (result.Calibration is null)
        {
            var reason = result.RejectReason ?? "no geometrically-consistent fit";
            var category = OutcomeVocabulary.RejectSolveSubcategory(result.RejectReason);
            attempt.Outcome = category;
            _logger?.LogInformation("Auto-calibration rejected for {Area}: {Reason}. Prior calibration kept.", area, reason);
            return new AutoCalibrationOutcome(Persisted: false, AreaKey: area, RejectReason: reason, OutcomeCategory: category);
        }

        // Gate-accept: persist through the user store stamped AutoCapture, which
        // inherits user-store precedence by construction (Task 20).
        //
        // mithril#1078: explicit Frame=Texture. The auto-capture RANSAC solves over
        // the aligned base texture (`alignedTexture` + `alignedRect = (0,0,…)`); the
        // resulting OriginX/Scale/etc. are in texture-pixel units. AreaCalibration.Frame
        // defaults to Texture so this stamp is documentation today, but making it
        // explicit defends against the default flipping in a future cleanup and
        // keeps the save sites symmetric with Legolas-wizard's Frame=Overlay stamp.
        // mithril#1081: stamp the base texture's SHA-256 so the Legolas overlay
        // can look up dims via IMapTextureDimensions when composing the record
        // onto the overlay surface. Same digest the sidecar's MapTextureManifest
        // carries; we re-hash from baseTexture.Pixels (~1 MB at 1024², sub-ms)
        // rather than threading it through IBaseTextureProvider.
        var stamped = result.Calibration with
        {
            Source = CalibrationSource.AutoCapture,
            Frame = CalibrationFrame.Texture,
            PixelSha256 = Convert.ToHexStringLower(SHA256.HashData(baseTexture.Pixels)),
        };

        attempt.Outcome = OutcomeVocabulary.Accepted;
        _calibrationService.SaveUserRefinement(sceneRef, stamped);
        _logger?.LogInformation(
            "Auto-calibration persisted for {Area} (residual {Residual:0.00} px, {Inliers} inliers).",
            area, stamped.ResidualPixels, result.InlierCount);
        return new AutoCalibrationOutcome(
            Persisted: true,
            AreaKey: area,
            RejectReason: null,
            OutcomeCategory: OutcomeVocabulary.Accepted);
    }

    /// <summary>
    /// Task-21 policy. Resolve the base texture from the #931 provider; on a
    /// cache-miss, optionally trigger the sidecar once to populate the cache,
    /// then retry. Fail-soft to null on any path.
    ///
    /// <para>Keyed on the per-scene <paramref name="assetKey"/> (mithril#1021):
    /// the literal Unity Texture2D name observed in the Player.log
    /// <c>Downloading Map … runtime key …[Map_&lt;X&gt;]</c> line.</para>
    /// </summary>
    private async Task<GrayImage?> ResolveBaseTextureAsync(string assetKey, CancellationToken ct)
    {
        var tex = _baseTextures.TryGetBaseTexture(assetKey);
        if (tex is not null) return tex;

        if (_assetExtractor is null || _gameConfig is null
            || string.IsNullOrWhiteSpace(_gameConfig.InstallRoot) || string.IsNullOrWhiteSpace(_assetCacheDir))
        {
            return null; // no extractor wired → safe-degrade (caller surfaces "preparing map assets…")
        }

        _logger?.LogInformation("Base texture cache-miss for {MapAsset}; invoking asset-extractor sidecar.", assetKey);
        try
        {
            var request = new ExtractRequest(
                InstallRoot: _gameConfig.InstallRoot,
                OutDir: _assetCacheDir!,
                Kind: ExtractKind.Texture,
                MapAssetName: assetKey,
                ExpectPgVersion: _pgVersion,
                TpkPath: ResolveTpkPath());
            var extract = await _assetExtractor.ExtractAsync(request, ct).ConfigureAwait(false);
            if (!extract.Ok)
            {
                _logger?.LogWarning(
                    "Asset-extractor sidecar failed for {MapAsset} (exit {Exit}): {Error}. Safe-degrade.",
                    assetKey, extract.ExitCode, extract.Error);
                return null;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Asset-extractor sidecar threw for {MapAsset}. Safe-degrade.", assetKey);
            return null;
        }

        var retried = _baseTextures.TryGetBaseTexture(assetKey); // retry after populate
        if (retried is null)
        {
            // The extractor reported success but the provider still has no usable
            // texture for this asset. Distinguish this from a plain transient
            // cache-miss: it usually means an asset-shape change or a
            // canonical-hash-gate mismatch (the extracted bytes don't match the
            // gated hash), which a future PG patch can introduce silently.
            // Behaviour is unchanged (still fail-soft); this just makes the
            // gate/shape mismatch visible instead of looking like a cache hiccup.
            _logger?.LogWarning(
                "Asset-extractor reported success for {MapAsset} but no usable base texture is available after retry "
                + "(possible asset-shape change or canonical-hash-gate mismatch, not a transient cache-miss). Safe-degrade.",
                assetKey);
        }
        return retried;
    }

    /// <summary>
    /// #949 policy (icon analogue of <see cref="ResolveBaseTextureAsync"/>). Resolve
    /// the icon-template set from the per-attempt <see cref="IIconTemplateProvider"/>;
    /// on an empty set, optionally demand-trigger the sidecar's <c>--icons</c> mode
    /// once to populate the cache, then re-resolve. Fail-soft to whatever the
    /// provider returns (Empty included) on any path — never throws into the engine.
    /// </summary>
    private async Task<IconTemplateSet> EnsureIconTemplatesAsync(CancellationToken ct)
    {
        var templates = _iconTemplates.GetTemplates();
        if (templates.Templates.Count > 0) return templates;

        if (_assetExtractor is null || _gameConfig is null
            || string.IsNullOrWhiteSpace(_gameConfig.InstallRoot) || string.IsNullOrWhiteSpace(_assetCacheDir))
        {
            return templates; // no extractor wired → safe-degrade (Empty → gate rejects)
        }

        _logger?.LogInformation("Icon-template cache empty; invoking asset-extractor sidecar (--icons) on demand.");
        try
        {
            var request = new ExtractRequest(
                InstallRoot: _gameConfig.InstallRoot,
                OutDir: _assetCacheDir!,
                Kind: ExtractKind.Icons,
                MapAssetName: null,
                ExpectPgVersion: _pgVersion,
                TpkPath: ResolveTpkPath());
            var extract = await _assetExtractor.ExtractAsync(request, ct).ConfigureAwait(false);
            if (!extract.Ok)
            {
                _logger?.LogWarning(
                    "Asset-extractor sidecar (--icons) failed (exit {Exit}): {Error}. Safe-degrade (no icon detections).",
                    extract.ExitCode, extract.Error);
                return templates;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Asset-extractor sidecar (--icons) threw. Safe-degrade (no icon detections).");
            return templates;
        }

        return _iconTemplates.GetTemplates(); // re-resolve after populate
    }

    /// <summary>
    /// Clamp a refined <see cref="MapRect"/> to the captured-frame bounds (#978). The
    /// ECC refine can place the rect's far edge a pixel or two past the frame when the
    /// in-game map is captured right up to the edge; clamping the origin to ≥0 and the
    /// extent to the frame keeps <see cref="ImageOps.Crop"/> in-bounds (it throws on an
    /// out-of-range region) without crashing the attempt. Returns null when the clamp
    /// leaves nothing to crop (origin already at/past the far edge) so the caller can
    /// fail-soft with a reason instead of cropping an empty region. Texture dims pass
    /// through unchanged — they're metadata, not pixels.
    /// </summary>
    private static MapRect? ClampToFrame(MapRect rect, int frameWidth, int frameHeight)
    {
        int x = Math.Clamp(rect.OriginX, 0, frameWidth);
        int y = Math.Clamp(rect.OriginY, 0, frameHeight);
        int w = Math.Min(rect.Width, frameWidth - x);
        int h = Math.Min(rect.Height, frameHeight - y);
        if (w <= 0 || h <= 0)
        {
            return null;
        }
        if (x == rect.OriginX && y == rect.OriginY && w == rect.Width && h == rect.Height)
        {
            return rect; // already in-bounds — no allocation
        }
        return rect with { OriginX = x, OriginY = y, Width = w, Height = h };
    }

    private AutoCalibrationOutcome Fail(string area, string reason, string outcomeCategory)
    {
        _logger?.LogInformation("Auto-calibration not attempted for {Area}: {Reason}.", string.IsNullOrEmpty(area) ? "<none>" : area, reason);
        return new AutoCalibrationOutcome(Persisted: false, AreaKey: area, RejectReason: reason, OutcomeCategory: outcomeCategory);
    }

}

/// <summary>
/// The outcome of one auto-calibration attempt: whether a transform was
/// persisted, the area it was for, a user-facing reason when not persisted
/// (<see cref="CalibrationStatusFormatter"/>), and the structured outcome
/// category (one of the constants on <see cref="Diagnostics.OutcomeVocabulary"/>).
///
/// <para><see cref="OutcomeCategory"/> is nullable; <see cref="CalibrationStatusFormatter.ForOutcome"/>
/// routes structurally when it is set and falls back to substring-matching
/// the <see cref="RejectReason"/> when null. New engine return sites MUST
/// populate it.</para>
/// </summary>
public sealed record AutoCalibrationOutcome(
    bool Persisted,
    string AreaKey,
    string? RejectReason,
    string? OutcomeCategory = null);
