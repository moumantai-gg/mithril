using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// <see cref="IMapViewProbe"/> that delegates to the auto-cal's locator pipeline
/// (the registered <see cref="IMapRegionRefiner"/> — by default the
/// <see cref="CompositeMapRegionRefiner"/> dispatching ORB+RANSAC
/// → Sobel-padded-pyramid fallback). Replaces the original
/// <see cref="CrossCorrelationMapViewProbe"/> hand-rolled FFT-NCC.
///
/// <para><b>Why delegate.</b> The live-view probe and the auto-cal locator solve
/// the same inner problem: given a screenshot containing the map plus chrome,
/// find where the map sits and at what scale. The auto-cal pipeline already
/// handles both outdoor (ORB primary, fast and feature-rich) and indoor
/// (Sobel-NCC fallback, robust on smooth-corridor textures); the original
/// hand-rolled NCC was inferior to both — confirmed by the indoor+outdoor
/// real-screenshot benchmark in
/// <c>LiveMapViewProbeRealScreenshotBenchmark</c> (mithril#1107). The spec for
/// #1095 chose a separate algorithm under perf and indoor-coverage assumptions
/// that no longer apply (locator pipeline is sub-second; the Sobel fallback
/// landed in #1061 after the probe was specced).</para>
///
/// <para><b>Adapter math.</b> The locator returns a <see cref="MapRect"/>
/// describing the visible map's pixel rect within the screenshot and the source
/// texture's native dimensions. <see cref="MapViewFix.TextureToOverlay"/>
/// composes as <c>(tex_px − pan) × viewScale</c>, so for the locator's
/// texture(0,0) → screenshot(<see cref="MapRect.OriginX"/>,
/// <see cref="MapRect.OriginY"/>) similarity (PG's UI is axis-aligned, the
/// refiner enforces rotation~0), the fix is:
/// <code>
/// viewScale = Width / TextureWidth   // overlay-px per texture-px
/// panTexPxX = -OriginX / viewScale
/// panTexPxY = -OriginY / viewScale
/// </code></para>
///
/// <para><b>Fail-soft.</b> When the inner refiner returns
/// <see cref="MapRegionRefineResult.AcceptedRect"/> null (no fit, or fit rejected
/// by the refiner's gate), this probe returns <c>null</c>. Consumers refuse to
/// render — same contract as before.</para>
/// </summary>
public sealed class LocatorBackedMapViewProbe : IMapViewProbe
{
    private readonly IMapRegionRefiner _refiner;
    private readonly ILogger? _logger;

    public LocatorBackedMapViewProbe(
        IMapRegionRefiner refiner,
        ILogger<LocatorBackedMapViewProbe>? logger = null)
    {
        _refiner = refiner;
        _logger = logger;
    }

    /// <inheritdoc/>
    public MapViewFix? TryProbe(GrayImage screenshot, GrayImage baseTexture)
    {
        if (screenshot is null || baseTexture is null)
        {
            _logger?.LogWarning("TryProbe: null input (screenshot={Screenshot}, baseTex={BaseTex}).",
                screenshot is null ? "null" : $"{screenshot.Width}x{screenshot.Height}",
                baseTexture is null ? "null" : $"{baseTexture.Width}x{baseTexture.Height}");
            return null;
        }

        _logger?.LogTrace("TryProbe: screenshot {SW}x{SH}, baseTex {TW}x{TH}; delegating to {Refiner}.",
            screenshot.Width, screenshot.Height, baseTexture.Width, baseTexture.Height, _refiner.GetType().Name);

        var result = _refiner.Refine(screenshot, baseTexture);
        if (result.AcceptedRect is not { } rect)
        {
            _logger?.LogWarning(
                "TryProbe: refiner did not accept (rawFit={HasRawFit}, metricsConfidence={Conf}). Returning null.",
                result.RawFitRect is not null,
                result.Metrics?.Confidence?.ToString("0.000") ?? "n/a");
            return null;
        }

        // viewScale is overlay-pixels per texture-pixel. The refiner's transform
        // is an isotropic similarity (rotation~0, equal X/Y scale enforced by
        // MaxRotationDegrees + axis-aligned PG UI), so X and Y scale agree to
        // within rounding — average them so a one-off pixel rounding in
        // OriginX/Y or Width/Height doesn't bias one axis over the other.
        double viewScaleX = rect.Width / (double)rect.TextureWidth;
        double viewScaleY = rect.Height / (double)rect.TextureHeight;
        double viewScale = 0.5 * (viewScaleX + viewScaleY);

        double panX = -rect.OriginX / viewScale;
        double panY = -rect.OriginY / viewScale;

        double confidence = result.Metrics?.Confidence
            ?? result.Metrics?.InlierRatio
            ?? 0.0;

        _logger?.LogInformation(
            "TryProbe: ACCEPTED via {Provenance}. rect=({OX},{OY})+{W}x{H}, tex={TW}x{TH}, " +
            "viewScale={Scale:0.0000} (X={SX:0.0000} Y={SY:0.0000}), pan=({PX:0.0},{PY:0.0}), conf={Conf:0.000}.",
            result.Metrics?.Provenance, rect.OriginX, rect.OriginY, rect.Width, rect.Height,
            rect.TextureWidth, rect.TextureHeight, viewScale, viewScaleX, viewScaleY,
            panX, panY, confidence);

        return new MapViewFix(
            PanTexPxX: panX,
            PanTexPxY: panY,
            ViewScale: viewScale,
            Confidence: confidence,
            MeasuredAt: DateTimeOffset.UtcNow);
    }
}
