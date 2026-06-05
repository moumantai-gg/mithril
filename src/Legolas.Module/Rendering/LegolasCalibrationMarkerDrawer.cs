using Mithril.MapCalibration;
using Mithril.Overlay;
using Mithril.Overlay.Internal;
using Vortice.Direct2D1;

namespace Legolas.Rendering;

/// <summary>
/// Pure-D2D drawer for the in-flow calibration marker style (#460/#477A).
/// Mirrors the WPF <c>ItemsControl</c> rendering in
/// <c>MapOverlayView.xaml</c>: the selection ring (Outer) draws only when
/// <see cref="LegolasCalibrationMarkerStyle.IsSelected"/> is true, and the
/// always-on dot (Center) draws unconditionally. Both layers share the
/// composited-pin geometry helpers in <see cref="LegolasMarkerDrawerCore"/>
/// so dash patterns / stroke insets / shape-builder paths match the rest of
/// the Legolas drawer family byte-for-byte.
///
/// <para>Step 5 of #835: the calibration drawer ships here together with the
/// producer-side switch in <see cref="Legolas.ViewModels.MapOverlayViewModel"/> +
/// <see cref="Legolas.Services.PinCalibrationCoordinator"/>. PR #853 explicitly
/// deferred this drawer because there was no <c>PinSceneRenderer</c> branch
/// to byte-parity-compare against — the new baselines committed alongside
/// these tests become the regression contract.</para>
/// </summary>
internal static class LegolasCalibrationMarkerDrawer
{
    /// <summary>Draw one calibration marker. Outer ring shows iff selected,
    /// centre dot always shows. <paramref name="pixel"/> is the projected
    /// position from the marker registry.</summary>
    public static void Draw(
        LegolasCalibrationMarkerStyle style,
        OverlayPixel pixel,
        ID2D1RenderTarget rt,
        ID2D1Factory factory,
        D2DBrushCache brushes)
    {
        // Selection ring — the WPF template binds Visibility to IsSelected,
        // so we mirror that exactly here. Uses Outer.Size as the diameter,
        // matching the WPF <Path>'s NegativeHalfConverter + Size geometry.
        if (style.IsSelected)
        {
            LegolasMarkerDrawerCore.DrawPinLayer(
                rt, factory, brushes, pixel, style.Outer.Size, style.Outer);
        }

        // Centre dot — always-on. Uses Center.Size for diameter.
        LegolasMarkerDrawerCore.DrawPinLayer(
            rt, factory, brushes, pixel, style.Center.Size, style.Center);
    }
}
