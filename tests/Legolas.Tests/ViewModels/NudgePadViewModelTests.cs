using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;
using Mithril.MapCalibration;
using Mithril.Shared.Reference;
using Xunit;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// The on-screen nudge pad (wizard panel + overlay) binds its root
/// <c>IsEnabled</c> to <see cref="NudgePadViewModel.IsAvailable"/>. It must be
/// live for every <see cref="MapOverlayViewModel.Nudge"/> target — not just a
/// selected survey — or calibration-marker / manual-anchor nudging silently
/// no-ops (the pad stays disabled).
/// </summary>
public class NudgePadViewModelTests
{
    private static PixelPoint Project(double x, double z) => new(100 + 2 * x, 100 - 2 * z);

    private static (NudgePadViewModel pad, SessionState session, PinCalibrationCoordinator cal,
        FakeMapPinState pins) Build()
    {
        var session = new SessionState { Mode = SessionMode.Survey };
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var pins = new FakeMapPinState();
        var bus = new TestDomainEventBus();
        var cal = new PinCalibrationCoordinator(new FakeCalib(), pins, bus, settings);
        var map = new MapOverlayViewModel(session, projector, optimizer, surveyFlow, brushes, settings, cal);
        var pad = new NudgePadViewModel(session, map, settings);
        return (pad, session, cal, pins);
    }

    [Fact]
    public void Pad_is_unavailable_with_no_target()
    {
        var (pad, _, _, _) = Build();
        pad.IsAvailable.Should().BeFalse();
        pad.NudgeTargetLabel.Should().Be("(no target — select a pin)");
    }

    [Fact]
    public void Pad_becomes_available_when_a_calibration_marker_is_selected()
    {
        var (pad, _, cal, pins) = Build();
        pins.SeedExisting(
            FakeMapPinState.Pin(10, 10),
            FakeMapPinState.Pin(50, 60),
            FakeMapPinState.Pin(90, 20));
        cal.Arm(); // ≥3 pins → Pair phase

        bool raised = false;
        pad.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(pad.IsAvailable)) raised = true; };

        // Pairing selects the just-placed marker.
        cal.PairClick(Project(10, 10));

        raised.Should().BeTrue("availability must re-raise so the d-pad enables live");
        pad.IsAvailable.Should().BeTrue();
        pad.NudgeTargetLabel.Should().Be("Calibration pin");
    }

    [Fact]
    public void Pad_is_available_for_the_manual_player_anchor()
    {
        var (pad, session, _, _) = Build();
        session.SurveyPlayerPixel = new PixelPoint(100, 100);
        session.SurveyPlayerIsManual = true;

        pad.IsAvailable.Should().BeTrue();
        pad.NudgeTargetLabel.Should().Be("Your position");

        // The auto/tracker anchor (not manual) is not nudgeable.
        session.SurveyPlayerIsManual = false;
        pad.IsAvailable.Should().BeFalse();
    }

    private sealed class FakeCalib : IAreaCalibrationService
    {
        public AreaCalibration? CalibrateCurrentArea(
            IReadOnlyList<(WorldCoord World, PixelPoint Pixel)> placements, double calibrationZoom = 1.0)
            => new(1, 0, 0, 0, placements.Count, 0);
        public MapSceneRef? CurrentScene => new MapSceneRef("AreaTest", null, "Map_AreaTest");
        public string? CurrentAreaFriendlyName => "Test";
        public bool IsCurrentAreaCalibrated => false;
        public AreaCalibration? CurrentCalibration => null;
        public IReadOnlyList<CalibrationReference> CurrentAreaReferences => Array.Empty<CalibrationReference>();
        public IReadOnlyList<AreaEntry> AllAreas => Array.Empty<AreaEntry>();
        public event EventHandler? Changed { add { } remove { } }
        public void SelectScene(MapSceneRef scene) { }
        public void ClearCurrentAreaCalibration() { }
        public void NoteSurvey(string name, MetreOffset offset) { }
        public event EventHandler<CalibrationSurveyObservation>? SurveyObserved { add { } remove { } }
    }
}
