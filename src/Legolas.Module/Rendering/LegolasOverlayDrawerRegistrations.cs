using Mithril.Overlay.Internal;

namespace Legolas.Rendering;

/// <summary>
/// Single-entry-point registration of every Legolas-side
/// <see cref="Mithril.Overlay.IMarkerStyle"/> drawer with
/// <see cref="MarkerSceneRenderer"/>. Migration step 2 of #835 ships this
/// helper exposing the registrations but does not call it from
/// <c>Legolas.Module.Activate</c> — step 3 wires the consumer side and is
/// where production registration goes.
///
/// <para>For now, the registration helper is exercised by Legolas tests so
/// the renderer dispatch path runs end-to-end with the lifted drawers.</para>
///
/// <para>#835 step 5: the calibration drawer
/// (<see cref="LegolasCalibrationMarkerDrawer"/>) joins the family. Today's
/// calibration markers (Drop / Pair walkthrough) are also rendered by a WPF
/// <c>ItemsControl</c> in <c>MapOverlayView.xaml</c>; the marker pipeline
/// takes over for areas with a baseline calibration, the <c>ItemsControl</c>
/// stays as the fallback for brand-new areas with no baseline
/// (<c>OverlayToWorld</c> can't convert the click pixel to a world coord
/// without a baseline calibration).</para>
/// </summary>
public static class LegolasOverlayDrawerRegistrations
{
    /// <summary>
    /// Register every Legolas drawer with the supplied
    /// <see cref="MarkerSceneRenderer"/>. Safe to call from any thread (the
    /// renderer's registry is concurrent). Idempotent — re-registration of
    /// the same style type replaces the previous drawer, per the renderer
    /// contract.
    /// </summary>
    internal static void RegisterAll(MarkerSceneRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        // #835 step 6: Survey / Motherlode / Motherlode-guidance / Player
        // pins are now drawn by LegolasOverlaySceneDrawer.Draw (the freeform
        // scene-hook callback), not via the marker registry. Their drawers
        // stay registered here because the snapshot baselines in
        // LegolasMarkerDrawerSnapshotTests / MarkerPipelineSnapshotTests
        // exercise them directly via MarkerSceneRenderer to keep byte parity
        // with PinSceneRenderer (the soon-to-be-retired reference). Once
        // step 7 deletes those snapshot tests' parity reference, the four
        // pin-drawer registrations below can be retired with their
        // companion drawer + style classes.
        renderer.RegisterDrawer<LegolasSurveyMarkerStyle>(LegolasSurveyMarkerDrawer.Draw);
        renderer.RegisterDrawer<LegolasMotherlodeMarkerStyle>(LegolasMotherlodeMarkerDrawer.Draw);
        renderer.RegisterDrawer<LegolasMotherlodeGuidanceMarkerStyle>(LegolasMotherlodeGuidanceMarkerDrawer.Draw);
        renderer.RegisterDrawer<LegolasPlayerMarkerStyle>(LegolasPlayerMarkerDrawer.Draw);
        // Calibration placement pins are now drawn pixel-native by
        // LegolasOverlaySceneDrawer.DrawCalibrationPlacementPins, per the
        // dissolved-#868 framing on #835 ("no seed-calibration requirement").
        // The registry-side LegolasCalibrationMarkerStyle drawer is no
        // longer registered — producer plumbing in
        // MapOverlayViewModel.RefreshCalibrationMarker still calls
        // _markers.AddMarker for areas with a baseline, but render-time
        // silently drops those markers since no drawer matches.
        // Producer-side cleanup + style/drawer file deletion are step-7
        // work, mirroring the PinScene / MapOverlayView.xaml deferral.
    }
}
