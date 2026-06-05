using Arda.World.Player.Events;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;
using Legolas.ViewModels;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// #476/#497 — pure precedence for the Survey "you are here" anchor:
/// freshest-wins, the character pin is the preferred manual, manual is sticky
/// against a calibration-only refresh, a genuinely newer tracker fix wins.
/// </summary>
public class SurveyAnchorResolutionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ProjectWorld with this cal: (100 + 2x, 200 - 2z).
    // #1076 Phase 6.5: typed overlay-frame view of the same similarity.
    private static WorldToOverlayCalibration Cal() => new(
        OriginX: 100, OriginY: 200, Scale: 2.0, RotationRadians: 0.0,
        MirrorNorth: false, CalibrationZoom: 1.0);
    private static TrackerFix Tracker(double x, double z, DateTimeOffset at,
        PositionSource src = PositionSource.Spawn) => new(x, 0, z, at, src);
    private static CharacterPinFix Pin(double x, double z, DateTimeOffset at) =>
        new(new WorldCoord(x, 0, z), at, "@me");

    [Fact]
    public void Character_pin_is_the_preferred_manual_when_calibrated()
    {
        var r = MapOverlayViewModel.ResolveSurveyAnchor(
            tracker: Tracker(0, 0, T0), pin: Pin(10, 5, T0.AddMinutes(5)), cal: Cal(),
            fromTrackerFix: false, currentIsManual: false, currentIsPinned: false);

        r.Should().NotBeNull();
        r!.Value.IsPinned.Should().BeTrue();
        r.Value.IsManual.Should().BeTrue();
        r.Value.Source.Should().BeNull();
        r.Value.Pixel.Should().Be(new OverlayPixel(120, 190));   // 100+2*10, 200-2*5
        r.Value.MeasuredAt.Should().Be(T0.AddMinutes(5));
    }

    [Fact]
    public void A_genuinely_newer_tracker_fix_supersedes_the_pin()
    {
        var r = MapOverlayViewModel.ResolveSurveyAnchor(
            tracker: Tracker(3, 4, T0.AddMinutes(10)), pin: Pin(10, 5, T0), cal: Cal(),
            fromTrackerFix: true, currentIsManual: true, currentIsPinned: true);

        r!.Value.IsPinned.Should().BeFalse();
        r.Value.IsManual.Should().BeFalse();
        r.Value.Source.Should().Be(PositionSource.Spawn);
        r.Value.Pixel.Should().Be(new OverlayPixel(106, 192));   // tracker projected
    }

    [Fact]
    public void An_older_tracker_fix_does_not_supersede_a_fresher_pin()
    {
        var r = MapOverlayViewModel.ResolveSurveyAnchor(
            tracker: Tracker(3, 4, T0), pin: Pin(10, 5, T0.AddMinutes(10)), cal: Cal(),
            fromTrackerFix: true, currentIsManual: true, currentIsPinned: true);

        r!.Value.IsPinned.Should().BeTrue();
        r.Value.Pixel.Should().Be(new OverlayPixel(120, 190));
    }

    [Fact]
    public void Uncalibrated_pin_cannot_win_and_a_sticky_click_is_kept()
    {
        // Pin present but no calibration to project it; an existing pixel-click
        // manual must survive a calibration-only refresh (return null = keep).
        var r = MapOverlayViewModel.ResolveSurveyAnchor(
            tracker: null, pin: Pin(10, 5, T0), cal: null,
            fromTrackerFix: false, currentIsManual: true, currentIsPinned: false);

        r.Should().BeNull();
    }

    [Fact]
    public void Pixel_click_manual_is_sticky_against_a_calibration_only_refresh()
    {
        var r = MapOverlayViewModel.ResolveSurveyAnchor(
            tracker: Tracker(1, 1, T0), pin: null, cal: Cal(),
            fromTrackerFix: false, currentIsManual: true, currentIsPinned: false);

        r.Should().BeNull("a calibration re-apply must not demote the #476 click");
    }

    [Fact]
    public void A_fresh_tracker_fix_supersedes_a_pixel_click_manual()
    {
        var r = MapOverlayViewModel.ResolveSurveyAnchor(
            tracker: Tracker(1, 1, T0), pin: null, cal: Cal(),
            fromTrackerFix: true, currentIsManual: true, currentIsPinned: false);

        r!.Value.IsManual.Should().BeFalse();
        r.Value.Pixel.Should().Be(new OverlayPixel(102, 198));
    }

    [Fact]
    public void Pin_removal_ends_the_pinned_manual_and_falls_back_to_auto()
    {
        // Was pinned (IsManual && IsPinned); pin now gone; not a tracker fix.
        var r = MapOverlayViewModel.ResolveSurveyAnchor(
            tracker: Tracker(2, 2, T0), pin: null, cal: Cal(),
            fromTrackerFix: false, currentIsManual: true, currentIsPinned: true);

        r!.Value.IsPinned.Should().BeFalse();
        r.Value.IsManual.Should().BeFalse();
        r.Value.Pixel.Should().Be(new OverlayPixel(104, 196));
    }

    [Fact]
    public void No_pin_no_fix_clears_everything()
    {
        var r = MapOverlayViewModel.ResolveSurveyAnchor(
            tracker: null, pin: null, cal: null,
            fromTrackerFix: true, currentIsManual: false, currentIsPinned: false);

        r.Should().Be(SurveyAnchorResolution.Cleared);
        r!.Value.Pixel.Should().BeNull();
    }
}
