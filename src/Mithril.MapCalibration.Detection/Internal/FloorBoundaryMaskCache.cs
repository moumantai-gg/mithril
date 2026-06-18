using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Per-area in-memory cache of the dilated floor-boundary mask derived from the
/// base texture's alpha channel (mithril#1116 Task 2, spec §D5; rewritten in
/// mithril#1183 review pass).
///
/// <para>The PG base texture's alpha channel is 0 where the area's floor doesn't
/// exist (off-map / out-of-bounds) and 255 where it does. The boundary between
/// the two — once dilated by a few pixels to absorb sub-pixel renderer / anti-
/// alias noise — is exactly the chrome the deviation detector must NOT trust as
/// "this differs from baseline", because legitimate floor-edge softening will
/// always produce mask hits there. We pre-compute the dilated boundary band
/// once per area, then the deviation detector ANDs it out of the deviation
/// mask before NCC.</para>
///
/// <para><b>Algorithm.</b> 4-connected edge detection on a binary alpha
/// threshold (≥ 128) → separable horizontal-then-vertical 1D max-dilation by
/// the caller-provided dilation radius. The dilation source-of-truth is
/// <see cref="Mithril.MapCalibration.Detection.SceneCalibrationProfile.BoundaryDilationPx"/>
/// (per-scene-class override, mithril#1174) with fallback to
/// <see cref="MapCalibrationDetectorOptions.BoundaryDilationPx"/> (global
/// setting). Resolution lives at the call site
/// (<see cref="Mithril.MapCalibration.Capture.AutoCalibrationEngine"/>); the
/// cache just stores the result.
/// The result is a <see cref="GrayImage"/> with 255 on the dilated boundary
/// band and 0 elsewhere. <b>Fail-soft:</b> when the provider can't furnish
/// alpha, or the alpha buffer is degenerate (all 0 / all 255 — no meaningful
/// boundary exists), we log a warning and return null. The deviation detector
/// handles null by skipping the boundary-exclusion step and lets the unmasked
/// deviation drive matching (safe-degrade).</para>
///
/// <para><b>Caching + lock discipline.</b> Three dictionaries — <c>_cache</c>
/// (mask per (key, dilation)), <c>_sceneClassCache</c> (scene class per key),
/// <c>_opaqueFractionCache</c> (alpha-opaque fraction per key) — all guarded
/// by <c>_gate</c>. <b>The provider call runs OUTSIDE the lock</b> via a
/// double-checked pattern: take the lock, check the cache; if miss, release
/// the lock, do provider IO + classify + (optionally) build the mask, then
/// reacquire to commit. This keeps alpha-load serialization off the hot path
/// for concurrent area resolutions and avoids holding <c>_gate</c> across
/// chained-provider re-entry (mithril#1183 review C1, C2). Provider throws
/// propagate to the caller with NO state poisoning — a transient sidecar IO
/// failure is retryable on the next call (mithril#1183 review C2). Alpha is
/// loaded fresh per cache miss; we don't cache the raw alpha buffer
/// (mithril#1183 review C3 — pre-fix the buffer was retained for the lifetime
/// of the DI singleton). On contention two threads may both load alpha for the
/// same key — the second writer is a no-op via <c>TryAdd</c>; the cost is one
/// extra alpha load under cold-start race, no correctness issue.</para>
/// </summary>
internal sealed class FloorBoundaryMaskCache
{
    private readonly IBaseTextureProvider _provider;
    private readonly MapCalibrationDetectorOptions _options;
    private readonly ILogger<FloorBoundaryMaskCache>? _logger;

    private readonly object _gate = new();
    // mithril#1174: the boundary mask is keyed on (mapAssetKey, dilationPx)
    // because the dilation now flows from the per-scene-class
    // SceneCalibrationProfile, not the global options field. In production each
    // area has ONE SceneClass → ONE dilation, so the composed key collapses to
    // one mask per area. The composition exists for the tests' dilation-sweep
    // theory and for any future settings-flip flow (mithril#1183 review C19 —
    // the settings-flip flow isn't wired yet; revisit when it is).
    private readonly Dictionary<(string MapAssetKey, int DilationPx), GrayImage?> _cache = new();

    // mithril#1163 spec §5.2: cache the alpha-coverage-derived SceneClass per
    // key. SceneClass derives from alpha which is per-area, not per-dilation.
    private readonly Dictionary<string, SceneClass> _sceneClassCache = new();

    // mithril#1163 spec §5.6: cache the opaque fraction alongside the scene
    // class — diagnostic field surfaced by TryGetOpaqueFraction for the engine's
    // bundle sink.
    private readonly Dictionary<string, double> _opaqueFractionCache = new();

    public FloorBoundaryMaskCache(
        IBaseTextureProvider provider,
        MapCalibrationDetectorOptions options,
        ILogger<FloorBoundaryMaskCache>? logger = null)
    {
        _provider = provider;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Returns the dilated floor-boundary mask for <paramref name="mapAssetKey"/>
    /// at <paramref name="dilationPx"/>, or <c>null</c> when alpha is unavailable
    /// or degenerate. Cached per (key, dilationPx) — repeat calls for the same
    /// pair hit the cache (including cached nulls).
    ///
    /// <para><b>mithril#1174.</b> Caller-provided <paramref name="dilationPx"/>
    /// is resolved by
    /// <see cref="Mithril.MapCalibration.Capture.AutoCalibrationEngine"/> from
    /// <see cref="Mithril.MapCalibration.Detection.SceneCalibrationProfile.BoundaryDilationPx"/>
    /// (per-profile override) or the global
    /// <see cref="MapCalibrationDetectorOptions.BoundaryDilationPx"/> fallback.</para>
    ///
    /// <para><b>mithril#1183 review:</b> Provider IO runs OUTSIDE the lock. Two
    /// concurrent callers for the same (key, dilation) miss both pay the provider
    /// call once — the second writer is a no-op via TryAdd. Provider throws
    /// propagate; no state is poisoned, retry is possible.</para>
    /// </summary>
    public GrayImage? GetOrCompute(string mapAssetKey, int dilationPx)
    {
        var compositeKey = (mapAssetKey, dilationPx);

        // Phase 1: lock, read cache. Fast path.
        lock (_gate)
        {
            if (_cache.TryGetValue(compositeKey, out var cached))
            {
                return cached;
            }
        }

        // Phase 2: outside the lock, do provider IO + classification. Throws
        // here propagate to the caller without poisoning any cache state — a
        // transient failure is retryable on the next call.
        var (alpha, sceneClass, opaqueFraction) = LoadAndClassify(mapAssetKey);

        // Phase 3: build the mask if we have a non-degenerate alpha. Mask
        // compute is CPU-only (no IO), but it's still cheap to do outside the
        // lock so other threads on other areas don't block.
        GrayImage? mask = null;
        if (alpha is not null)
        {
            mask = ComputeBoundaryMask(alpha, dilationPx);
        }

        // Phase 4: reacquire to commit. TryAdd lets a concurrent winner stand;
        // the loser's result is discarded with no correctness penalty (mask is
        // pure function of alpha + dilation; both threads compute the same).
        lock (_gate)
        {
            // _cache uses indexer (overwrites duplicate writes idempotently —
            // same (alpha, dilation) → same mask bytes by construction).
            _cache[compositeKey] = mask;
            if (sceneClass is { } sc) _sceneClassCache.TryAdd(mapAssetKey, sc);
            if (opaqueFraction is { } frac) _opaqueFractionCache.TryAdd(mapAssetKey, frac);
        }

        // Log AFTER the commit so the LogInformation reflects the cache state
        // any subsequent reader will see. Success path emits Information per
        // CLAUDE.md's instrumentation contract (mithril#1183 review C12 — the
        // pre-review refactor had silently dropped the success-path log).
        if (mask is not null)
        {
            _logger?.LogInformation(
                "Computed floor-boundary mask for {MapAsset} ({W}x{H}, dilation {DilationPx}px); scene class {SceneClass}.",
                mapAssetKey, mask.Width, mask.Height, dilationPx,
                sceneClass ?? SceneClass.Outdoor);
        }
        return mask;
    }

    /// <summary>
    /// Returns the alpha-coverage-derived <see cref="SceneClass"/> for
    /// <paramref name="mapAssetKey"/> (mithril#1163 spec §5.2). Cached per key;
    /// first call may pay the provider's alpha load + classification scan,
    /// subsequent calls hit the cache. Fail-soft: returns
    /// <see cref="SceneClass.Outdoor"/> when alpha is unavailable.
    ///
    /// <para><b>mithril#1183 review:</b> The provider call runs OUTSIDE the
    /// lock; concurrent first-touch on the same key may both load alpha (second
    /// writer is a no-op via TryAdd), no lock-during-IO.</para>
    /// </summary>
    public SceneClass GetSceneClass(string mapAssetKey)
    {
        lock (_gate)
        {
            if (_sceneClassCache.TryGetValue(mapAssetKey, out var cached))
            {
                return cached;
            }
        }

        var (_, sceneClass, opaqueFraction) = LoadAndClassify(mapAssetKey);
        var resolved = sceneClass ?? SceneClass.Outdoor;

        lock (_gate)
        {
            _sceneClassCache.TryAdd(mapAssetKey, resolved);
            if (opaqueFraction is { } frac) _opaqueFractionCache.TryAdd(mapAssetKey, frac);
        }
        return resolved;
    }

    /// <summary>
    /// Returns the cached opaque-fraction (alpha ≥ 128 / total) for
    /// <paramref name="mapAssetKey"/> if it has been classified, else <c>null</c>.
    /// Diagnostic-only — surfaced by the engine's bundle sink for the
    /// <c>sceneClassOpaqueFraction</c> JSON field per spec §5.6.
    /// </summary>
    public double? TryGetOpaqueFraction(string mapAssetKey)
    {
        lock (_gate)
        {
            return _opaqueFractionCache.TryGetValue(mapAssetKey, out var frac) ? frac : null;
        }
    }

    /// <summary>
    /// Loads alpha from the provider, classifies the scene class, checks the
    /// degeneracy gate. Runs OUTSIDE the cache lock. Returns a triple — alpha
    /// is non-null only when both the provider returned a buffer AND the
    /// buffer is non-degenerate (so the caller can compute a meaningful
    /// boundary mask). Scene class + opaque fraction are populated whenever
    /// alpha was loadable, even for degenerate alphas (the scene class label
    /// is still well-defined when all pixels are transparent).
    ///
    /// <para>Provider throws propagate to the caller — no try/catch here. The
    /// caller (lock-free at this point) sees the exception and the cache stays
    /// unwritten, so retry is possible on the next call.</para>
    /// </summary>
    private (GrayImage? Alpha, SceneClass? SceneClass, double? OpaqueFraction) LoadAndClassify(
        string mapAssetKey)
    {
        var alpha = _provider.TryGetTextureAlpha(mapAssetKey);
        if (alpha is null)
        {
            _logger?.LogWarning(
                "Floor-boundary mask for {MapAsset} unavailable — provider returned no alpha (safe-degrade).",
                mapAssetKey);
            // mithril#1163: alpha unavailable → can't classify → Outdoor by
            // fail-soft default. Surface that so the caller can stamp the
            // scene class cache.
            return (null, SceneClass.Outdoor, null);
        }

        // Fused single-pass over the alpha buffer — computes opaque count for
        // both the scene-class verdict AND the degeneracy check (mithril#1183
        // review C20: pre-review code walked the buffer twice).
        long opaqueCount = 0;
        int n = alpha.Pixels.Length;
        for (int i = 0; i < n; i++)
        {
            if (alpha.Pixels[i] >= 128) opaqueCount++;
        }
        double fraction = n == 0 ? 1.0 : opaqueCount / (double)n;
        var sceneClass = fraction >= _options.SceneClassOpaqueFractionThreshold
            ? SceneClass.Outdoor
            : SceneClass.Indoor;
        bool degenerate = n == 0 || opaqueCount == 0 || opaqueCount == n;

        if (degenerate)
        {
            _logger?.LogWarning(
                "Floor-boundary mask for {MapAsset} skipped — alpha is degenerate (all-opaque or all-transparent) (safe-degrade); scene class {SceneClass}.",
                mapAssetKey, sceneClass);
            // Scene class + fraction are still well-defined on a degenerate
            // texture; only the mask is null.
            return (null, sceneClass, fraction);
        }

        return (alpha, sceneClass, fraction);
    }

    private static GrayImage ComputeBoundaryMask(GrayImage alpha, int dilationPx)
    {
        int w = alpha.Width;
        int h = alpha.Height;
        var src = alpha.Pixels;

        // 4-connected edge mask: a pixel is on the boundary iff at least one
        // of its 4 neighbors lies on the opposite side of the 128-threshold.
        // We compare opacity states (bool) rather than raw bytes so soft alpha
        // ramps land cleanly on one side or the other.
        var edge = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = rowBase + x;
                bool here = src[i] >= 128;
                bool isEdge = false;
                if (x > 0     && ((src[i - 1] >= 128) != here)) isEdge = true;
                else if (x < w - 1 && ((src[i + 1] >= 128) != here)) isEdge = true;
                else if (y > 0     && ((src[i - w] >= 128) != here)) isEdge = true;
                else if (y < h - 1 && ((src[i + w] >= 128) != here)) isEdge = true;
                edge[i] = isEdge;
            }
        }

        // Separable 1D max-dilation: horizontal pass then vertical pass.
        // Each "true" expands by `dilationPx` in each direction along the axis.
        // Cost is O(w*h*r) — fine for a once-per-area hot path.
        var dilated = dilationPx <= 0 ? edge : DilateSeparable(edge, w, h, dilationPx);

        // Pack to byte[] (255 = masked / on the dilated boundary, 0 = floor
        // interior or exterior).
        var bytes = new byte[w * h];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (dilated[i]) bytes[i] = 255;
        }
        return new GrayImage(w, h, bytes);
    }

    private static bool[] DilateSeparable(bool[] src, int w, int h, int radius)
    {
        var tmp = new bool[w * h];

        // Horizontal pass.
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                bool any = false;
                int xStart = x - radius; if (xStart < 0) xStart = 0;
                int xEnd = x + radius;   if (xEnd > w - 1) xEnd = w - 1;
                for (int xi = xStart; xi <= xEnd; xi++)
                {
                    if (src[rowBase + xi]) { any = true; break; }
                }
                tmp[rowBase + x] = any;
            }
        }

        // Vertical pass.
        var dst = new bool[w * h];
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
            {
                bool any = false;
                int yStart = y - radius; if (yStart < 0) yStart = 0;
                int yEnd = y + radius;   if (yEnd > h - 1) yEnd = h - 1;
                for (int yi = yStart; yi <= yEnd; yi++)
                {
                    if (tmp[yi * w + x]) { any = true; break; }
                }
                dst[y * w + x] = any;
            }
        }

        return dst;
    }
}
