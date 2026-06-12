using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Per-area in-memory cache of the dilated floor-boundary mask derived from the
/// base texture's alpha channel (mithril#1116 Task 2, spec §D5).
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
/// <see cref="MapCalibrationDetectorOptions.BoundaryDilationPx"/>. The result
/// is a <see cref="GrayImage"/> with 255 on the dilated boundary band and 0
/// elsewhere. <b>Fail-soft:</b> when the provider can't furnish alpha, or the
/// alpha buffer is degenerate (all 0 / all 255 — no meaningful boundary
/// exists), we log a warning and return null. The deviation detector handles
/// null by skipping the boundary-exclusion step and lets the unmasked
/// deviation drive matching (safe-degrade).</para>
///
/// <para><b>Caching.</b> Computation cost is O(w · h · r). The hot path is once
/// per area on first detection — subsequent detections within the same area
/// (and across area revisits — alpha doesn't change at runtime) hit the cache.
/// A null result is also cached so a once-degenerate texture doesn't pay the
/// provider IO + degeneracy scan twice.</para>
/// </summary>
internal sealed class FloorBoundaryMaskCache
{
    private readonly IBaseTextureProvider _provider;
    private readonly MapCalibrationDetectorOptions _options;
    private readonly ILogger<FloorBoundaryMaskCache>? _logger;

    private readonly object _gate = new();
    private readonly Dictionary<string, GrayImage?> _cache = new();

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
    /// Returns the dilated floor-boundary mask for <paramref name="mapAssetKey"/>,
    /// or <c>null</c> when alpha is unavailable or degenerate. Cached per key —
    /// repeat calls for the same key hit the cache (including cached nulls).
    /// </summary>
    public GrayImage? GetOrCompute(string mapAssetKey)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(mapAssetKey, out var cached))
            {
                return cached;
            }

            var alpha = _provider.TryGetTextureAlpha(mapAssetKey);
            if (alpha is null)
            {
                _logger?.LogWarning(
                    "Floor-boundary mask for {MapAsset} unavailable — provider returned no alpha (safe-degrade).",
                    mapAssetKey);
                _cache[mapAssetKey] = null;
                return null;
            }

            if (IsDegenerate(alpha))
            {
                _logger?.LogWarning(
                    "Floor-boundary mask for {MapAsset} skipped — alpha is degenerate (all-opaque or all-transparent) (safe-degrade).",
                    mapAssetKey);
                _cache[mapAssetKey] = null;
                return null;
            }

            var mask = ComputeBoundaryMask(alpha, _options.BoundaryDilationPx);
            _logger?.LogInformation(
                "Computed floor-boundary mask for {MapAsset} ({W}x{H}, dilation {DilationPx}px).",
                mapAssetKey, mask.Width, mask.Height, _options.BoundaryDilationPx);
            _cache[mapAssetKey] = mask;
            return mask;
        }
    }

    private static bool IsDegenerate(GrayImage alpha)
    {
        // A texture is degenerate (no boundary to mask) if every pixel sits on
        // the same side of the 128-threshold. Cheap single-pass scan.
        var p = alpha.Pixels;
        if (p.Length == 0) return true;
        bool firstOpaque = p[0] >= 128;
        for (int i = 1; i < p.Length; i++)
        {
            if ((p[i] >= 128) != firstOpaque) return false;
        }
        return true;
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
