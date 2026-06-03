using System.Threading.Tasks;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture.Diagnostics;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// #1005 regression: a user calibrates at one in-game zoom, then re-captures
/// at a different zoom. The pre-#1005 monotonicity gate rejected the second
/// capture because the per-attempt inlier count drops with visible-icon size
/// (RenderSizePx-16 typed detection), and the chip then told the user to
/// "zoom the in-game map all the way out" — the action that just tripped
/// the gate. Both sides of the loop are tested end-to-end here.
/// </summary>
public sealed class AutoCalibrationEngineZoomChangeRegressionTests
{
    private const string Area = EngineHarness.DefaultArea;

    [Fact]
    public async Task User_recalibrates_at_a_different_zoom_and_lands_without_being_told_to_zoom_out()
    {
        var svc = new FakeCalibrationService();

        // Step 1: cold-start calibration at scale 0.408. High quality fit.
        var coldHarness = new EngineHarness
        {
            Service = svc,
            Solve = TestHelpers.Accepted(residual: 0.79, inliers: 10),
            Refiner = new FakeRefiner(
                new MapRect(0, 0, 64, 64, 64, 64),
                TestLocateMetrics.ForScale(0.408)),
        };
        var first = await coldHarness.Engine().TryCalibrateCurrentAreaAsync(default);

        first.Persisted.Should().BeTrue();
        first.OutcomeCategory.Should().Be(OutcomeVocabulary.Accepted);
        CalibrationStatusFormatter.ForOutcome(first).Should().BeNull();
        svc.Saved[Area].LocatorScale.Should().Be(0.408);

        // Step 2: user changes the in-game zoom, redraws the bbox, re-captures.
        // The new regime is scale 0.800 — outside the ±2% tolerance. Even though
        // the fit at the new zoom has fewer inliers than the old (icons render
        // smaller, fewer survive RenderSizePx-16 matching), the gate must skip
        // because the comparison is invalid across regimes.
        var zoomChangedHarness = new EngineHarness
        {
            Service = svc,
            Solve = TestHelpers.Accepted(residual: 1.2, inliers: 5),
            Refiner = new FakeRefiner(
                new MapRect(0, 0, 64, 64, 64, 64),
                TestLocateMetrics.ForScale(0.800)),
        };
        var second = await zoomChangedHarness.Engine().TryCalibrateCurrentAreaAsync(default);

        second.Persisted.Should().BeTrue();
        second.OutcomeCategory.Should().Be(OutcomeVocabulary.Accepted);
        CalibrationStatusFormatter.ForOutcome(second).Should().BeNull(); // chip clears

        // Saved cal is the NEW one (single-slot storage per #1005; per-scale is #1006).
        svc.Saved[Area].LocatorScale.Should().Be(0.800);
        svc.Saved[Area].ResidualPixels.Should().Be(1.2);
    }

    [Fact]
    public async Task Wrong_fit_at_the_same_zoom_still_keeps_the_good_one_with_the_new_chip_message()
    {
        // The original #988 protection. Same in-game zoom, second capture is
        // a 5× residual blow-up with fewer inliers — clearly wrong. Gate
        // fires, chip says "unchanged".
        var svc = new FakeCalibrationService();

        var firstHarness = new EngineHarness
        {
            Service = svc,
            Solve = TestHelpers.Accepted(residual: 0.79, inliers: 10),
            Refiner = new FakeRefiner(
                new MapRect(0, 0, 64, 64, 64, 64),
                TestLocateMetrics.ForScale(0.408)),
        };
        await firstHarness.Engine().TryCalibrateCurrentAreaAsync(default);

        var secondHarness = new EngineHarness
        {
            Service = svc,
            Solve = TestHelpers.Accepted(residual: 4.03, inliers: 4),
            Refiner = new FakeRefiner(
                new MapRect(0, 0, 64, 64, 64, 64),
                TestLocateMetrics.ForScale(0.411)), // within ±2%
        };
        var second = await secondHarness.Engine().TryCalibrateCurrentAreaAsync(default);

        second.Persisted.Should().BeFalse();
        second.OutcomeCategory.Should().Be(OutcomeVocabulary.RejectedNotMonotonic);
        var msg = CalibrationStatusFormatter.ForOutcome(second);
        msg.Should().Contain("Calibration unchanged");
        msg.Should().NotContain("zoom the in-game map all the way out");

        // Good first calibration preserved.
        svc.Saved[Area].ResidualPixels.Should().Be(0.79);
    }
}
