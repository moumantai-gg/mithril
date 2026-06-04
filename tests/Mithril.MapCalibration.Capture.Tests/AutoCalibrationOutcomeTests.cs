using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Capture.Diagnostics;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class AutoCalibrationOutcomeTests
{
    [Fact]
    public void OutcomeCategory_defaults_to_null_for_legacy_callers()
    {
        // Three-positional construction must still compile — callers that
        // haven't been updated keep working with OutcomeCategory = null,
        // and CalibrationStatusFormatter falls back to its substring path.
        var outcome = new AutoCalibrationOutcome(Persisted: false, AreaKey: "AreaTest", RejectReason: "x");

        outcome.OutcomeCategory.Should().BeNull();
    }

    [Fact]
    public void OutcomeCategory_carries_through_when_set()
    {
        var outcome = new AutoCalibrationOutcome(
            Persisted: false,
            AreaKey: "AreaTest",
            RejectReason: "x",
            OutcomeCategory: OutcomeVocabulary.RejectedSolveResidual);

        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.RejectedSolveResidual);
    }
}
