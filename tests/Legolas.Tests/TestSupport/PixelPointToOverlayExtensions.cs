namespace Legolas.Tests.TestSupport;

/// <summary>
/// #1076 5a: a small re-tag helper for test assertions. The
/// <see cref="Mithril.MapCalibration.AreaCalibration.WorldToWindow"/> and
/// <see cref="Mithril.MapCalibration.IMapCalibrationService.WorldToWindow"/>
/// surfaces still return the untyped <see cref="Mithril.MapCalibration.PixelPoint"/>
/// in Phase 5a (Phase 6 typifies the core). Legolas-side assertions and
/// session-state fields use <see cref="OverlayPixel"/>, so we re-tag at the
/// test boundary the same way production code does.
/// </summary>
internal static class PixelPointToOverlayExtensions
{
    public static OverlayPixel AsOverlay(this Mithril.MapCalibration.PixelPoint p) => new(p.X, p.Y);

    public static OverlayPixel? AsOverlay(this Mithril.MapCalibration.PixelPoint? p) =>
        p is { } v ? new OverlayPixel(v.X, v.Y) : null;
}
