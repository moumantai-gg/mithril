using Mithril.Overlay;

namespace Legolas.Rendering;

/// <summary>
/// Per-marker style for in-flow calibration markers (#460/#477A — the Drop /
/// Pair walkthrough). Mirrors the legacy WPF <c>ItemsControl</c> rendering in
/// <c>MapOverlayView.xaml</c>:
/// <list type="bullet">
/// <item><see cref="Outer"/> = the selection ring (drawn only while
/// <see cref="IsSelected"/> is true) — matches the <c>Outer.Size</c> ring
/// the WPF template hangs off <c>CalibrationPinStyle.Outer</c>.</item>
/// <item><see cref="Center"/> = the always-on dot — matches the
/// <c>Center.Size</c> dot the WPF template hangs off
/// <c>CalibrationPinStyle.Center</c>.</item>
/// </list>
///
/// <para>Per the issue body (§"Scope — what's IN" D), this is the calibration
/// drawer PR #853 explicitly deferred to step 5 because today's calibration
/// markers are <c>ItemsControl</c>-rendered, not <c>PinSceneRenderer</c>-
/// rendered — there was no D2D source-of-truth to byte-parity-compare against.
/// Step 5's snapshot baselines (committed alongside this style) become the
/// new ground truth.</para>
///
/// <para><b>Pixel vs world coord — design notebook.</b> The calibration
/// walkthrough captures clicks in pixel space (the user clicks the in-game
/// map's rendered pin); the marker registry holds world coords. The producer
/// (<c>PinCalibrationCoordinator</c>) converts pixel → world via
/// <c>IMapCalibrationService.OverlayToWorld</c> using the current (baseline /
/// pre-refinement) calibration at registration time. The render-time
/// <c>WorldToOverlay</c> then projects back through the same calibration —
/// round-trip is byte-identical iff the calibration didn't change between
/// register and render. Once the user confirms a refinement, the calibration
/// changes and the markers re-project — which is in fact MORE correct than
/// today's pixel-frozen rendering (the markers track the new calibration).</para>
///
/// <para><b>Brand-new-area fallback.</b> When an area has no baseline,
/// <c>OverlayToWorld</c> returns null and the marker can't register; in that
/// case the existing WPF <c>ItemsControl</c> in <c>MapOverlayView.xaml</c>
/// stays the rendering path. Step 6's "delete dead code" PR will revisit
/// the fallback once the no-baseline case has a strategy of its own.</para>
/// </summary>
public sealed record LegolasCalibrationMarkerStyle(
    PinLayerStyle Outer,
    PinLayerStyle Center,
    bool IsSelected) : IMarkerStyle;
