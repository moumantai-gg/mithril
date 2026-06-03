using System.Threading.Tasks;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture.Diagnostics;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// #1005 Task 5: every <see cref="AutoCalibrationOutcome"/> produced by
/// <see cref="AutoCalibrationEngine"/> must carry an <c>OutcomeCategory</c>
/// from <see cref="OutcomeVocabulary"/> matching the <c>attempt.Outcome</c>
/// the bundle sink already records. The status formatter can then route on
/// the category instead of substring-matching the reject reason.
/// </summary>
public sealed class AutoCalibrationEngineOutcomeCategoryTests
{
    private const string Area = EngineHarness.DefaultArea;
    // #1021: calibration store keys are per-scene asset keys (Map_<X>).
    private const string AssetKey = EngineHarness.DefaultAssetKey;

    [Fact]
    public async Task Accept_outcome_carries_Accepted_category()
    {
        var h = new EngineHarness { Solve = TestHelpers.Accepted(0.5, 5) };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);
        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.Accepted);
    }

    [Fact]
    public async Task NoArea_outcome_carries_RejectedNoArea_category()
    {
        var h = new EngineHarness { CurrentArea = null };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);
        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.RejectedNoArea);
    }

    [Fact]
    public async Task NoBbox_outcome_carries_RejectedNoBbox_category()
    {
        var h = new EngineHarness { Bbox = null };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);
        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.RejectedNoBbox);
    }

    [Fact]
    public async Task PgNotForeground_outcome_carries_RejectedPgNotForeground_category()
    {
        var h = new EngineHarness { GameWindow = null };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);
        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.RejectedPgNotForeground);
    }

    [Fact]
    public async Task NoBaseTexture_outcome_carries_RejectedNoBaseTexture_category()
    {
        var h = new EngineHarness { BaseTexture = null };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);
        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.RejectedNoBaseTexture);
    }

    [Fact]
    public async Task MapNotLocated_outcome_carries_RejectedMapNotLocated_category()
    {
        var h = new EngineHarness { Refiner = FakeRefiner.NotLocated() };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);
        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.RejectedMapNotLocated);
    }

    [Fact]
    public async Task SolveReject_outcome_carries_a_Rejected_solve_subcategory()
    {
        var h = new EngineHarness { Solve = TestHelpers.Rejected("residual 25.00 px exceeds threshold 12.00 px") };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);
        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.RejectedSolveResidual);
    }

    [Fact]
    public async Task Monotonicity_reject_outcome_carries_RejectedNotMonotonic_category()
    {
        var svc = new FakeCalibrationService();
        svc.Seed(AssetKey, TestHelpers.SomeBaseline() with { LocatorScale = 0.408, ResidualPixels = 0.79, ReferenceCount = 10 });

        var h = new EngineHarness
        {
            Service = svc,
            Solve = TestHelpers.Accepted(residual: 4.03, inliers: 4),
            Refiner = new FakeRefiner(
                new MapRect(0, 0, 64, 64, 64, 64),
                TestLocateMetrics.ForScale(0.411)),
        };

        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.RejectedNotMonotonic);
    }
}
