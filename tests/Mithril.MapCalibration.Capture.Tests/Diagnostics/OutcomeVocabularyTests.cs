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
    [InlineData("only 3 inliers (need >= 4)", "rejected-solve-insufficient-inliers")]
    [InlineData("residual 14.20 px exceeds threshold 12.00 px", "rejected-solve-residual")]
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
    [InlineData("rejected-map-low-confidence", true)]
    public void ShouldWriteBundle_skips_pre_capture_outcomes(string outcome, bool expected)
    {
        OutcomeVocabulary.ShouldWriteBundle(outcome).Should().Be(expected);
    }

    [Fact]
    public void RejectedMapLowConfidence_constant_is_stable()
    {
        // mithril#1061: callers consume the literal string as bundle-subdir suffix
        // + daily-JSON category; locking it here keeps the diagnostic vocabulary
        // stable across refactors.
        OutcomeVocabulary.RejectedMapLowConfidence.Should().Be("rejected-map-low-confidence");
    }
}
