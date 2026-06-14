using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// mithril#1163 Phase 1 — pins the <see cref="SceneCalibrationProfile"/>
/// Outdoor / Indoor values against regressions. Outdoor MUST stay identical to
/// today's universal constants quoted in <c>AutoCalibrationEngine.cs:61-62</c>
/// so the Outdoor replay-fixture battery (Serbule / Eltibule / Kur) keeps
/// solving byte-identically. Indoor MUST stay at T1+T2 (<c>MaxAspect 2.7,
/// MinSolidity 0.30</c>) so the Phase-2 measurement's 3/6 RIC recovery on the
/// canonical Hogan's 06-13 bundle remains the design contract.
/// </summary>
public sealed class SceneCalibrationProfileTests
{
    [Fact]
    public void Outdoor_profile_matches_today_universal_constants()
    {
        // Pinned to AutoCalibrationEngine.cs:61-62 — the gate-study sweet-spot
        // from mithril#897. If any of these change without the Outdoor replay
        // battery being re-verified, this assertion fails loudly.
        SceneCalibrationProfile.Outdoor.SceneClass.Should().Be(SceneClass.Outdoor);
        SceneCalibrationProfile.Outdoor.BlobOptions.MinArea.Should().Be(12);
        SceneCalibrationProfile.Outdoor.BlobOptions.MaxIconArea.Should().Be(900);
        SceneCalibrationProfile.Outdoor.BlobOptions.MinSolidity.Should().Be(0.35);
        SceneCalibrationProfile.Outdoor.BlobOptions.MaxAspect.Should().Be(2.5);
        SceneCalibrationProfile.Outdoor.BlobOptions.MinPeak.Should().Be(0.7);
    }

    [Fact]
    public void Indoor_profile_relaxes_aspect_and_solidity_per_T1_T2()
    {
        // T1 (MaxAspect 2.5 → 2.7) recovers IconE (aspect 2.56, reproduces
        // across distinct Hogan's bundles per the audit).
        // T2 (MinSolidity 0.35 → 0.30) recovers IconD (solidity 0.31 on
        // canonical 06-13).
        // Other knobs identical to Outdoor — measurement showed (win,
        // closeRadius, LowNcc) divergence doesn't move v1 recall.
        SceneCalibrationProfile.Indoor.SceneClass.Should().Be(SceneClass.Indoor);
        SceneCalibrationProfile.Indoor.BlobOptions.MinArea.Should().Be(12);
        SceneCalibrationProfile.Indoor.BlobOptions.MaxIconArea.Should().Be(900);
        SceneCalibrationProfile.Indoor.BlobOptions.MinSolidity.Should().Be(0.30);
        SceneCalibrationProfile.Indoor.BlobOptions.MaxAspect.Should().Be(2.7);
        SceneCalibrationProfile.Indoor.BlobOptions.MinPeak.Should().Be(0.7);
    }

    [Theory]
    [InlineData(SceneClass.Outdoor)]
    [InlineData(SceneClass.Indoor)]
    public void For_returns_the_matching_profile(SceneClass sceneClass)
    {
        var profile = SceneCalibrationProfile.For(sceneClass);
        profile.SceneClass.Should().Be(sceneClass);
    }

    [Fact]
    public void For_unknown_cast_value_falls_to_Outdoor_per_CS8524_arm()
    {
        // The `_ => Outdoor` arm in SceneCalibrationProfile.For exists because
        // warnings-as-errors makes CS8524 break the build without it (every
        // 2-arm enum dispatch needs an arm for the cast-from-int case). This
        // test pins the documented behaviour so a future refactor doesn't
        // accidentally change it to a throw — which would propagate into the
        // engine's catch and surface as an Error chip with no actionable text
        // (worse than safe-degrade). See mithril#1168 review for the full
        // argument.
        var profile = SceneCalibrationProfile.For((SceneClass)42);
        profile.Should().Be(SceneCalibrationProfile.Outdoor);
    }
}
