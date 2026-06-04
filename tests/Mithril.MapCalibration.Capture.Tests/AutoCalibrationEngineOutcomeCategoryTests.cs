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
    [Fact]
    public async Task Accept_outcome_carries_Accepted_category()
    {
        var h = new EngineHarness { Solve = TestHelpers.Accepted(0.5, 5) };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);
        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.Accepted);
    }

    [Fact]
    public async Task NoArea_outcome_carries_MapAssetNotYetKnown_category()
    {
        // mithril#1041: the outer resolution-cascade guard refuses both the
        // "no area" and "no scene observed yet" cells under the same outcome
        // category (MapAssetNotYetKnown) — there's no separate RejectedNoArea
        // category exposed past the outer guard anymore.
        var h = new EngineHarness { CurrentArea = null };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);
        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.MapAssetNotYetKnown);
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

}
