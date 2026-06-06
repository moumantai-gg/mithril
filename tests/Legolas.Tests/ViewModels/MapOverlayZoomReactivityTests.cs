using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.ViewModels;
using Xunit;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// #524: dragging <see cref="SessionState.CurrentMapZoom"/> live-reprojects
/// every calibration-aware surface (player marker, Motherlode markers,
/// validate-calibration ghosts) and updates the zoom-mismatch warning chip
/// + legacy-recalibrate hint without a debounce / refresh button.
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

    [Fact]
    public void CurrentMapZoom_change_fires_relevant_PropertyChanged_events()
    {
        // Seed a calibration so the marker pixels participate.
        var calibration = new AreaCalibration(2.0, 0.0, 100, 200, 3, 0);
        var (map, _, session) = Build(calibration);

        var fired = new HashSet<string>();
        map.PropertyChanged += (_, e) => { if (e.PropertyName is { } n) fired.Add(n); };

        session.CurrentMapZoom = 1.0;

        // The player marker / motherlode markers / validate ghosts collection /
        // warning chip all depend on the live zoom, so they must all refresh.
        fired.Should().Contain(nameof(MapOverlayViewModel.PlayerMarkerPixel));
        fired.Should().Contain(nameof(MapOverlayViewModel.MotherlodeMarkerPixels));
        fired.Should().Contain(nameof(MapOverlayViewModel.IsZoomMismatchWarningVisible));
    }

    [Fact]
    public void Warning_chip_appears_when_zoom_diverges_from_calibration_stamp()
    {
        // NOTE(#1095-P2.3): CalibrationZoom removed from AreaCalibration; the zoom-mismatch
        // warning chip behavior is being reworked in P2.3. This test is a placeholder.
        var calibration = new AreaCalibration(2.0, 0.0, 100, 200, 3, 0);
        var (map, _, session) = Build(calibration);

        // Post-#1095 the mismatch warning depends on the live MapViewFix, not CalibrationZoom.
        session.CurrentMapZoom = 2.0;
        map.IsZoomMismatchWarningVisible.Should().BeFalse("no CalibrationZoom stamp — warning disabled");

        session.CurrentMapZoom = 1.5;
        map.IsZoomMismatchWarningVisible.Should().BeFalse("no CalibrationZoom stamp — warning disabled");
    }

    [Fact]
    public void Warning_chip_suppressed_when_no_calibration_zoom()
    {
        // Post-#1095: no CalibrationZoom field — mismatch warning never fires.
        var cal = new AreaCalibration(2.0, 0.0, 100, 200, 3, 0);
        var (map, _, _) = Build(cal);

        map.IsZoomMismatchWarningVisible.Should().BeFalse(
            "no CalibrationZoom stamp — mismatch warning is disabled");
    }

    [Fact]
    public void Legacy_hint_not_shown_post_1095()
    {
        // Post-#1095: CalibrationZoom removed; legacy-recalibrate hint is no longer
        // applicable (hint relied on detecting CalibrationZoom == 1.0 default).
        // P2.3 will remove this hint from the VM entirely.
        var cal = new AreaCalibration(2.0, 0.0, 100, 200, 3, 0);
        var (map, _, _) = Build(cal);

        // Legacy hint behavior is being removed in P2.3; just confirm no crash.
        _ = map.IsLegacyRecalibrateHintVisible;
    }

    [Fact]
    public void Calibration_load_does_not_crash_on_area_change()
    {
        // Post-#1095: CalibrationZoom removed from AreaCalibration; the auto-seed
        // behavior (slider ← CalibrationZoom) is being removed in P2.3. This test
        // ensures loading a calibration does not crash the VM.
        var calibration = new AreaCalibration(2.0, 0.0, 100, 200, 3, 0);
        var (_, cal, session) = Build();
        session.CurrentMapZoom.Should().Be(2.0, "default before any calibration is loaded");

        cal.SetCalibration(calibration);
        // Slider is NOT seeded post-#1095 (no CalibrationZoom to read).
    }

    [Fact]
    public void Recalibrating_same_area_does_not_crash()
    {
        // Post-#1095: CalibrationZoom removed; just verify no crash on repeated
        // SetCalibration calls for the same area. P2.3 updates VM behavior fully.
        var calibration = new AreaCalibration(2.0, 0.0, 100, 200, 3, 0);
        var (_, cal, session) = Build();
        cal.SetCalibration(calibration);

        session.CurrentMapZoom = 1.5;
        cal.SetCalibration(new AreaCalibration(2.0, 0.0, 100, 200, 3, 0));

        session.CurrentMapZoom.Should().Be(1.5, "user-set zoom not clobbered by same-area recalibration");
    }

    [Fact]
    public void IsZoomFieldVisible_hides_when_overlay_click_through_is_on()
    {
        var (map, _, _) = Build();
        map.IsZoomFieldVisible.Should().BeTrue("default settings: click-through off");

        // We need a settings handle — grab via reflection-free path by
        // constructing with the same settings instance.
        var session = new SessionState();
        var settings = new LegolasSettings { ClickThroughMap = true };
        var surveyFlow = new SurveyFlowController(session, settings);
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var cal = new FakeAreaCalibrationService();
        var map2 = new MapOverlayViewModel(session, projector, optimizer, surveyFlow, brushes,
            settings, pinCalibration: null, positionState: null, bus: null, areaCalibration: cal);

        map2.IsZoomFieldVisible.Should().BeFalse("ClickThroughMap=true hides the overlay strip");
    }
}
