using FluentAssertions;
using Mithril.MapCalibration.Capture.Diagnostics;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests.Diagnostics;

public sealed class OutcomeVocabularyTests
{
    [Theory]
    [InlineData(null, "rejected-solve")]
    [InlineData("", "rejected-solve")]
    [InlineData("no geometrically-consistent fit", "rejected-solve")]
    [InlineData("no detections cleared the threshold", "rejected-solve-no-detections")]
    [InlineData("insufficient inliers (3 < 4 required)", "rejected-solve-insufficient-inliers")]
    [InlineData("residual 14.2 px exceeds 12 px gate", "rejected-solve-residual")]
    public void RejectSolveSubcategory_maps_reject_reasons(string? reason, string expected)
    {
        OutcomeVocabulary.RejectSolveSubcategory(reason).Should().Be(expected);
    }

    [Theory]
    [InlineData("rejected-no-area", false)]
    [InlineData("rejected-pg-not-foreground", false)]
    [InlineData("rejected-no-bbox", false)]
    [InlineData("rejected-capture-failed", true)]
    [InlineData("rejected-no-base-texture", true)]
    [InlineData("accepted", true)]
    [InlineData("error", true)]
    public void ShouldWriteBundle_skips_pre_capture_outcomes(string outcome, bool expected)
    {
        OutcomeVocabulary.ShouldWriteBundle(outcome).Should().Be(expected);
    }
}
