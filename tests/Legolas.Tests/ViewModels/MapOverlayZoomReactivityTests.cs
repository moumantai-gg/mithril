using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.ViewModels;
using Xunit;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// mithril#1095: SessionState.CurrentMapZoom / AreaCalibration.CalibrationZoom /
/// IsZoomMismatchWarningVisible deleted as part of the live-view-detector
/// migration. Live zoom state is now MapViewFix from ILiveMapViewService.
///
/// <para>Tests that required the deleted scalar zoom-slider surface are replaced
/// with minimal smoke tests that verify the VM constructs and properties are
/// readable without crash.</para>
/// </summary>
public class MapOverlayZoomReactivityTests
{
    private static (MapOverlayViewModel map, FakeAreaCalibrationService cal, SessionState session)
        Build(AreaCalibration? seedCal = null)
    {
        var session = new SessionState();
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var cal = new FakeAreaCalibrationService();
        if (seedCal is not null) cal.SetCalibration(seedCal);
        var map = new MapOverlayViewModel(session, projector, optimizer, surveyFlow, brushes,
            settings, pinCalibration: null, positionState: null, bus: null, areaCalibration: cal);
        return (map, cal, session);
    }

    // ---- Tests retained from #524 / updated for #1095 ----

    [Fact]
    public void Legacy_hint_always_false_post_1095()
    {
        // mithril#1095: CalibrationZoom removed; legacy-recalibrate hint retired.
        var cal = new AreaCalibration(2.0, 0.0, 100, 200, 3, 0);
        var (map, _, _) = Build(cal);

        map.IsLegacyRecalibrateHintVisible.Should().BeFalse(
            "mithril#1095: hint retired — no CalibrationZoom to detect legacy records");
    }

    [Fact]
    public void Calibration_load_does_not_crash_on_area_change()
    {
        // mithril#1095: CalibrationZoom removed; the auto-seed slider behavior
        // is deleted. Verify loading a calibration does not crash the VM.
        var calibration = new AreaCalibration(2.0, 0.0, 100, 200, 3, 0);
        var (_, cal, _) = Build();
        cal.SetCalibration(calibration);
        // No assertion needed — absence of exception is the pass criterion.
    }

    [Fact]
    public void Recalibrating_same_area_does_not_crash()
    {
        // mithril#1095: CalibrationZoom removed; just verify no crash on repeated
        // SetCalibration calls for the same area.
        var calibration = new AreaCalibration(2.0, 0.0, 100, 200, 3, 0);
        var (_, cal, _) = Build();
        cal.SetCalibration(calibration);
        cal.SetCalibration(new AreaCalibration(2.0, 0.0, 100, 200, 3, 0));
        // No assertion needed — absence of exception is the pass criterion.
    }

    // mithril#1095: IsZoomFieldVisible test removed — the property is being
    // moved/renamed as part of P2.5 XAML cleanup. Re-add once P2.5 lands.
}
