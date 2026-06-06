using Arda.Abstractions.Logs;
using Arda.World.Player;
using Arda.World.Player.Events;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// #497 end-to-end — a character-named / <c>@me</c> map pin drives the Survey
/// "you are here" through <see cref="MapOverlayViewModel"/>: it wins over a
/// stale tracker fix, is superseded by a genuinely newer one, follows the pin,
/// and falls back on removal. The unchanged <c>SurveyPlayerGpsTests</c> is the
/// no-pin regression guard.
/// </summary>
public class MapOverlayPinnedAnchorTests
{
    private static readonly AreaCalibration Calib = new(
        Scale: 1.0, RotationRadians: 0.0, OriginX: 0.0, OriginY: 0.0,
        ReferenceCount: 3, ResidualPixels: 0.5);

    // #1076 Phase 6.5: frame-typed overlay view of Calib for projection assertions.
    private static readonly WorldToOverlayCalibration CalibOverlay = new(
        OriginX: 0.0, OriginY: 0.0, Scale: 1.0, RotationRadians: 0.0,
        MirrorNorth: false);

    private static readonly LogLineMetadata Meta = new(
        Timestamp: new DateTimeOffset(2026, 5, 22, 14, 0, 0, TimeSpan.Zero),
        ReadOn: DateTimeOffset.UtcNow,
        IsReplay: false);

    private static (SessionState session, MapOverlayViewModel map,
        FakePositionState posState, TestDomainEventBus bus,
        FakeMapPinState pinState, FakeActiveCharacterService chr, FakeAreaCalibrationService areaCal)
        BuildSut(bool calibrated)
    {
        var session = new SessionState();
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var posState = new FakePositionState();
        var bus = new TestDomainEventBus();
        var pinState = new FakeMapPinState();
        var chr = new FakeActiveCharacterService();
        chr.SetName("Arthas");
        var charPin = new CharacterPinAnchor(bus, pinState, chr);
        var areaCal = new FakeAreaCalibrationService();
        if (calibrated) areaCal.SetCalibration(Calib);

        var map = new MapOverlayViewModel(
            session, projector, new AdaptiveRouteOptimizer(
                new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer()),
            surveyFlow, brushes, settings,
            pinCalibration: null, positionState: posState, bus: bus, areaCalibration: areaCal,
            motherlode: null, characterPin: charPin);
        return (session, map, posState, bus, pinState, chr, areaCal);
    }

    [Fact]
    public void Dropping_a_character_named_pin_becomes_the_anchor()
    {
        var (session, map, _, bus, pinState, _, _) = BuildSut(calibrated: true);

        bus.Publish(new MapPinAdded(40, -25, "Arthas", 0, 0, Meta));
        pinState.Add(new MapPinEntry(40, -25, "Arthas", 0, 0));

        session.SurveyPlayerIsPinned.Should().BeTrue();
        session.SurveyPlayerIsManual.Should().BeTrue();
        session.SurveyPlayerPixel.Should().Be(CalibOverlay.ToOverlay(new WorldCoord(40, 0, -25)));
        map.PlayerAnchorStatus.Should().StartWith("You — pinned");
    }

    [Fact]
    public void The_at_me_sentinel_also_works()
    {
        var (session, _, _, bus, pinState, _, _) = BuildSut(calibrated: true);

        bus.Publish(new MapPinAdded(10, 10, "@me", 0, 0, Meta));
        pinState.Add(new MapPinEntry(10, 10, "@me", 0, 0));

        session.SurveyPlayerIsPinned.Should().BeTrue();
        session.SurveyPlayerPixel.Should().Be(CalibOverlay.ToOverlay(new WorldCoord(10, 0, 10)));
    }

    [Fact]
    public void The_pin_beats_a_stale_tracker_fix()
    {
        var (session, _, _, bus, pinState, _, _) = BuildSut(calibrated: true);
        var staleMeta = Meta with { Timestamp = DateTimeOffset.UtcNow.AddHours(-1) };
        bus.Publish(new PlayerPositionChanged(999, 0, 999, PositionSource.Spawn, staleMeta));

        bus.Publish(new MapPinAdded(40, -25, "Arthas", 0, 0, Meta));
        pinState.Add(new MapPinEntry(40, -25, "Arthas", 0, 0));

        session.SurveyPlayerIsPinned.Should().BeTrue();
        session.SurveyPlayerPixel.Should().Be(CalibOverlay.ToOverlay(new WorldCoord(40, 0, -25)));
    }

    [Fact]
    public void A_genuinely_newer_tracker_fix_supersedes_the_pin()
    {
        var (session, _, _, bus, pinState, _, _) = BuildSut(calibrated: true);
        bus.Publish(new MapPinAdded(40, -25, "Arthas", 0, 0, Meta));
        pinState.Add(new MapPinEntry(40, -25, "Arthas", 0, 0));
        session.SurveyPlayerIsPinned.Should().BeTrue();

        var newerMeta = Meta with { Timestamp = DateTimeOffset.UtcNow.AddMinutes(5) };
        bus.Publish(new PlayerPositionChanged(7, 0, 8, PositionSource.Movement, newerMeta));

        session.SurveyPlayerIsPinned.Should().BeFalse();
        session.SurveyPlayerIsManual.Should().BeFalse();
        session.SurveyPlayerPixel.Should().Be(CalibOverlay.ToOverlay(new WorldCoord(7, 0, 8)));
    }

    [Fact]
    public void Removing_the_pin_falls_back_to_the_auto_fix()
    {
        var (session, _, _, bus, pinState, _, _) = BuildSut(calibrated: true);
        var oldMeta = Meta with { Timestamp = DateTimeOffset.UtcNow.AddHours(-1) };
        bus.Publish(new PlayerPositionChanged(7, 0, 8, PositionSource.Spawn, oldMeta));
        bus.Publish(new MapPinAdded(40, -25, "Arthas", 0, 0, Meta));
        pinState.Add(new MapPinEntry(40, -25, "Arthas", 0, 0));
        session.SurveyPlayerIsPinned.Should().BeTrue();

        pinState.Remove(40, -25);
        bus.Publish(new MapPinRemoved(40, -25, "Arthas", Meta));

        session.SurveyPlayerIsPinned.Should().BeFalse();
        session.SurveyPlayerIsManual.Should().BeFalse();
        session.SurveyPlayerPixel.Should().Be(CalibOverlay.ToOverlay(new WorldCoord(7, 0, 8)));
    }

    [Fact]
    public void Uncalibrated_area_cannot_show_the_pinned_marker()
    {
        var (session, map, _, bus, pinState, _, _) = BuildSut(calibrated: false);

        bus.Publish(new MapPinAdded(40, -25, "Arthas", 0, 0, Meta));
        pinState.Add(new MapPinEntry(40, -25, "Arthas", 0, 0));

        session.SurveyPlayerIsPinned.Should().BeFalse();
        session.SurveyPlayerPixel.Should().BeNull();
        map.PlayerAnchorStatus.Should().BeEmpty();
    }
}
