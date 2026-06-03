using System;
using System.Diagnostics;
using System.IO;
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

    // #988 monotonicity gate: when a stored calibration exists for the area,
    // a new fit must not regress quality by more than these tolerances.
    // Tuned from the Eltibule 03:11:05 (0.79 px / 10 inliers, GOOD) vs
    // 03:11:30 (4.03 px / 4 inliers, WRONG) pair surfaced by PR #986: ratio
    // 2.0× catches the 5× residual blow-up; delta 2 catches the 6-inlier
    // drop. Both gates conservative on the cold-start floor (4 inliers /
    // residual already <12 px) so a marginal-but-correct re-fit still wins.
    private const double MonotonicResidualRatio = 2.0;
    private const int MonotonicInlierDelta = 2;

    private readonly IAreaState _areaState;
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
        IAreaState areaState,
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
        _areaState = areaState;
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

    /// <summary>
    /// Thin public entry point. Wraps <see cref="RunAttemptCoreAsync"/> in a
    /// <c>try/finally</c> so <see cref="ICalibrationAttemptBundleSink.Write"/> is
    /// called on every exit path: success, gate-reject, exception, or cancellation.
    /// The sink is resolved per attempt so a live Settings toggle takes effect
    /// without an app restart.
    /// </summary>
    public async Task<AutoCalibrationOutcome> TryCalibrateCurrentAreaAsync(CancellationToken ct)
    {
        var area = _areaState.CurrentArea ?? string.Empty;
        var attempt = new CalibrationAttemptContext(area, DateTimeOffset.UtcNow);
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

        var area = attempt.Area;
        if (string.IsNullOrWhiteSpace(area))
        {
            attempt.Outcome = OutcomeVocabulary.RejectedNoArea;
            return Fail("", "not in-world — open Project Gorgon and enter an area first");
        }

        // PG-foreground gate: capture must read the game's framebuffer, not
        // another app's. (The hotkey already focus-gates; the auto path + manual
        // path both re-check here so neither can capture the wrong window.)
        if (_windowLocator.Locate() is null)
        {
            attempt.Outcome = OutcomeVocabulary.RejectedPgNotForeground;
            return Fail(area, "Project Gorgon is not the foreground window");
        }

        var bbox = _region.Current;
        if (bbox is null)
        {
            attempt.Outcome = OutcomeVocabulary.RejectedNoBbox;
            return Fail(area, "no map bbox set — use the draw-map-bbox hotkey first");
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
            return Fail(area, "map capture failed or was rejected (black / wrong-size frame)");
        }

        var gray = captureResult.Gray;

        _logger?.LogInformation(
            "Auto-calibration {Area}: captured {Width}x{Height} frame; resolving base texture…",
            area, gray.Width, gray.Height);
        var baseTexture = await ResolveBaseTextureAsync(area, ct).ConfigureAwait(false);
        if (baseTexture is null)
        {
            attempt.Outcome = OutcomeVocabulary.RejectedNoBaseTexture;
            return Fail(area, "preparing map assets… (base texture unavailable — no detections possible)");
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
        if (_refiner is FeatureMatchingRefiner fmRefiner)
        {
            fmRefiner.SetAreaKey(area);
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
            attempt.Outcome = OutcomeVocabulary.RejectedMapNotLocated;
            if (refineResult.Metrics is { } m)
            {
                _logger?.LogInformation(
                    "Auto-calibration {Area}: locate rejected — inliers={Inliers}/{Cand} ratio={Ratio:0.000}, scale={Scale:0.000}, rotation={Rot:0.000}°.",
                    area, m.InlierCount, m.CandidateCount, m.InlierRatio, m.Scale, m.RotationDegrees);
            }
            else if (refineResult.RawFitRect is { } best)
            {
                _logger?.LogInformation(
                    "Auto-calibration {Area}: locate rejected — raw fit rect at origin = ({X}, {Y}), size = {W}x{H}.",
                    area, best.OriginX, best.OriginY, best.Width, best.Height);
            }
            return Fail(area, "couldn't locate the map in the captured frame — zoom the in-game map all the way out and draw the capture box tightly around the map");
        }
        attempt.MapRect = mapRect;
        _logger?.LogInformation(
            "Auto-calibration {Area}: map sub-rect located ({MapRect}) in {ElapsedMs:0} ms.",
            area, mapRect, Stopwatch.GetElapsedTime(refineStart).TotalMilliseconds);

        var references = _references.ForArea(area);
        attempt.References = references;
        _logger?.LogInformation(
            "Auto-calibration {Area}: {ReferenceCount} landmark reference(s) for this area.", area, references.Count);

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
            return Fail(area, "the located map rect fell outside the captured frame — redraw the capture box tightly around the in-game map");
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
            attempt.Outcome = OutcomeVocabulary.RejectSolveSubcategory(result.RejectReason);
            _logger?.LogInformation("Auto-calibration rejected for {Area}: {Reason}. Prior calibration kept.", area, reason);
            return new AutoCalibrationOutcome(Persisted: false, AreaKey: area, RejectReason: reason);
        }

        // Gate-accept: persist through the user store stamped AutoCapture, which
        // inherits user-store precedence by construction (Task 20).
        var stamped = result.Calibration with { Source = CalibrationSource.AutoCapture };

        // #988 monotonicity gate. When a stored calibration already exists for
        // this area, the new fit must not regress residual/inlier quality (a
        // wrong-fit second attempt that clears the cold-start gate would
        // otherwise replace a good first attempt — see the Eltibule 03:11:05
        // vs 03:11:30 pair in the originating issue). Cold start (no existing)
        // takes the same accept path it always did.
        var existing = _calibrationService.GetCalibration(area);
        if (existing is not null)
        {
            var monotonicReason = CheckMonotonicAccept(existing, stamped, result.InlierCount);
            if (monotonicReason is not null)
            {
                attempt.Outcome = OutcomeVocabulary.RejectedNotMonotonic;
                _logger?.LogInformation(
                    "Auto-calibration rejected for {Area}: monotonicity gate — {Reason}. Prior calibration kept (residual {PriorResidual:0.00}px, refs {PriorRefs}).",
                    area, monotonicReason, existing.ResidualPixels, existing.ReferenceCount);
                return new AutoCalibrationOutcome(Persisted: false, AreaKey: area, RejectReason: monotonicReason);
            }
        }

        attempt.Outcome = OutcomeVocabulary.Accepted;
        _calibrationService.SaveUserRefinement(area, stamped);
        _logger?.LogInformation(
            "Auto-calibration persisted for {Area} (residual {Residual:0.00} px, {Inliers} inliers).",
            area, stamped.ResidualPixels, result.InlierCount);
        return new AutoCalibrationOutcome(Persisted: true, AreaKey: area, RejectReason: null);
    }

    /// <summary>
    /// Task-21 policy. Resolve the base texture from the #931 provider; on a
    /// cache-miss, optionally trigger the sidecar once to populate the cache,
    /// then retry. Fail-soft to null on any path.
    /// </summary>
    private async Task<GrayImage?> ResolveBaseTextureAsync(string area, CancellationToken ct)
    {
        var tex = _baseTextures.TryGetBaseTexture(area);
        if (tex is not null) return tex;

        if (_assetExtractor is null || _gameConfig is null
            || string.IsNullOrWhiteSpace(_gameConfig.InstallRoot) || string.IsNullOrWhiteSpace(_assetCacheDir))
        {
            return null; // no extractor wired → safe-degrade (caller surfaces "preparing map assets…")
        }

        _logger?.LogInformation("Base texture cache-miss for {Area}; invoking asset-extractor sidecar.", area);
        try
        {
            var request = new ExtractRequest(
                InstallRoot: _gameConfig.InstallRoot,
                OutDir: _assetCacheDir!,
                Kind: ExtractKind.Texture,
                AreaKey: area,
                ExpectPgVersion: _pgVersion,
                TpkPath: ResolveTpkPath());
            var extract = await _assetExtractor.ExtractAsync(request, ct).ConfigureAwait(false);
            if (!extract.Ok)
            {
                _logger?.LogWarning(
                    "Asset-extractor sidecar failed for {Area} (exit {Exit}): {Error}. Safe-degrade.",
                    area, extract.ExitCode, extract.Error);
                return null;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Asset-extractor sidecar threw for {Area}. Safe-degrade.", area);
            return null;
        }

        var retried = _baseTextures.TryGetBaseTexture(area); // retry after populate
        if (retried is null)
        {
            // The extractor reported success but the provider still has no usable
            // texture for this area. Distinguish this from a plain transient
            // cache-miss: it usually means an asset-shape change or a
            // canonical-hash-gate mismatch (the extracted bytes don't match the
            // gated hash), which a future PG patch can introduce silently.
            // Behaviour is unchanged (still fail-soft); this just makes the
            // gate/shape mismatch visible instead of looking like a cache hiccup.
            _logger?.LogWarning(
                "Asset-extractor reported success for {Area} but no usable base texture is available after retry "
                + "(possible asset-shape change or canonical-hash-gate mismatch, not a transient cache-miss). Safe-degrade.",
                area);
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
                AreaKey: null,
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

    private AutoCalibrationOutcome Fail(string area, string reason)
    {
        _logger?.LogInformation("Auto-calibration not attempted for {Area}: {Reason}.", string.IsNullOrEmpty(area) ? "<none>" : area, reason);
        return new AutoCalibrationOutcome(Persisted: false, AreaKey: area, RejectReason: reason);
    }

    /// <summary>
    /// #988 monotonicity gate. A new fit may REPLACE an existing stored
    /// calibration only if it isn't meaningfully worse. Rejects when the new
    /// residual exceeds the existing by <see cref="MonotonicResidualRatio"/>×
    /// OR the new inlier count is below the existing by more than
    /// <see cref="MonotonicInlierDelta"/>. Returns null on accept, or a
    /// human-readable reason on reject.
    ///
    /// <para>Cold start (no <paramref name="existing"/>) is the caller's
    /// problem — this helper is consulted only after the engine looks up a
    /// prior calibration and finds one. The cold-start accept path is
    /// unchanged per the issue's out-of-scope list.</para>
    /// </summary>
    internal static string? CheckMonotonicAccept(AreaCalibration existing, AreaCalibration candidate, int candidateInlierCount)
    {
        if (existing.ResidualPixels > 0
            && candidate.ResidualPixels > existing.ResidualPixels * MonotonicResidualRatio)
        {
            return $"new residual {candidate.ResidualPixels:0.00}px exceeds existing {existing.ResidualPixels:0.00}px × {MonotonicResidualRatio:0.0}";
        }
        if (candidateInlierCount < existing.ReferenceCount - MonotonicInlierDelta)
        {
            return $"new inlier count {candidateInlierCount} below existing {existing.ReferenceCount} − {MonotonicInlierDelta}";
        }
        return null;
    }
}

/// <summary>
/// The outcome of one auto-calibration attempt: whether a transform was
/// persisted, the area it was for, and (when not persisted) a user-facing reason
/// for status surfacing (<see cref="CalibrationStatusFormatter"/>).
/// </summary>
public sealed record AutoCalibrationOutcome(bool Persisted, string AreaKey, string? RejectReason);
