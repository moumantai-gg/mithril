using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// Failing-first tests for the residual+ref-count picker (mithril#1046).
/// The new rule: prefer the candidate with the lowest residual whose
/// <see cref="AreaCalibration.ReferenceCount"/> meets a minimum floor
/// (<c>MapCalibrationService.MinReferences</c>, added in Task A2).
/// When residuals are equal, source-precedence (UserRefinement &gt;
/// AutoCapture &gt; CommunitySync &gt; BundledBaseline) breaks the tie.
/// When ALL candidates are below the floor, fall back to source-precedence
/// alone (same as the current behaviour for a single-candidate store).
/// </summary>
public sealed class MapCalibrationServicePickerTests
{
    private const string Key = "Map_AreaTest";
    private static readonly MapSceneRef Scene = new("AreaTest", null, Key);

    private static AreaCalibration Cal(double residual, int refs, CalibrationSource source) =>
        new(Scale: 1.0, RotationRadians: 0, OriginX: 0, OriginY: 0,
            ReferenceCount: refs, ResidualPixels: residual) { Source = source };

    private static MapCalibrationService NewSvc(
        IReadOnlyDictionary<string, AreaCalibration>? baseline = null,
        IDictionary<string, AreaCalibration>? userRefs = null) =>
        new(
            baseline: baseline ?? new Dictionary<string, AreaCalibration>(),
            userStore: UserRefinementStore.ForTests(userRefs),
            goodResidualThresholdPx: 0, // unused after picker rewrite; will be removed in Task A3
            logger: NullLogger.Instance);

    /// <summary>
    /// A baseline with 8 references should beat a user refinement with only 2
    /// even though the user fit has a lower residual — 2 refs is below the floor.
    /// </summary>
    [Fact]
    public void Picker_HighRefCountBaselineBeatsLowRefUserFit()
    {
        var svc = NewSvc(
            baseline: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.9, 8, CalibrationSource.BundledBaseline) },
            userRefs: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.3, 2, CalibrationSource.UserRefinement) });
        svc.GetCalibration(Scene)!.Source.Should().Be(CalibrationSource.BundledBaseline);
    }

    /// <summary>
    /// When both candidates meet the floor, the one with lower residual wins
    /// even if it comes from a lower-precedence source.
    /// </summary>
    [Fact]
    public void Picker_PrefersLowerResidualAcrossSources()
    {
        var svc = NewSvc(
            baseline: new Dictionary<string, AreaCalibration> { [Key] = Cal(2.1, 8, CalibrationSource.BundledBaseline) },
            userRefs: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.6, 5, CalibrationSource.AutoCapture) });
        svc.GetCalibration(Scene)!.Source.Should().Be(CalibrationSource.AutoCapture);
    }

    /// <summary>
    /// Identical residual + ref count: UserRefinement outranks AutoCapture.
    /// Both are stored via the userStore (tagged differently) vs baseline dict.
    /// </summary>
    [Fact]
    public void Picker_TiebreaksBySourcePrecedence_UserOverAuto()
    {
        // Both candidates have the same residual + ref count. Baseline holds
        // the numbers tagged AutoCapture; user store holds them tagged UserRefinement.
        var svc = NewSvc(
            baseline: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.8, 6, CalibrationSource.AutoCapture) },
            userRefs: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.8, 6, CalibrationSource.UserRefinement) });
        svc.GetCalibration(Scene)!.Source.Should().Be(CalibrationSource.UserRefinement);
    }

    /// <summary>
    /// Identical residual + ref count: AutoCapture (user store) outranks BundledBaseline.
    /// </summary>
    [Fact]
    public void Picker_TiebreaksBySourcePrecedence_AutoOverBaseline()
    {
        var svc = NewSvc(
            baseline: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.8, 6, CalibrationSource.BundledBaseline) },
            userRefs: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.8, 6, CalibrationSource.AutoCapture) });
        svc.GetCalibration(Scene)!.Source.Should().Be(CalibrationSource.AutoCapture);
    }

    /// <summary>
    /// When ALL candidates are below the ref-count floor, fall back to
    /// source-precedence (highest-precedence source wins regardless of residual).
    /// </summary>
    [Fact]
    public void Picker_BelowFloorAcrossAll_FallsBackToSourcePrecedence()
    {
        var svc = NewSvc(
            baseline: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.5, 3, CalibrationSource.BundledBaseline) },
            userRefs: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.3, 2, CalibrationSource.UserRefinement) });
        svc.GetCalibration(Scene)!.Source.Should().Be(CalibrationSource.UserRefinement);
    }

    [Fact]
    public void Picker_NoCandidates_ReturnsNull()
    {
        NewSvc().GetCalibration(Scene).Should().BeNull();
    }

    [Fact]
    public void Picker_OnlyBaseline_ReturnsBaseline()
    {
        var svc = NewSvc(
            baseline: new Dictionary<string, AreaCalibration> { [Key] = Cal(2.1, 6, CalibrationSource.BundledBaseline) });
        svc.GetCalibration(Scene)!.Source.Should().Be(CalibrationSource.BundledBaseline);
    }

    [Fact]
    public void Picker_OnlyUserBelowFloor_ReturnsIt()
    {
        var svc = NewSvc(
            userRefs: new Dictionary<string, AreaCalibration> { [Key] = Cal(0.3, 2, CalibrationSource.UserRefinement) });
        svc.GetCalibration(Scene)!.Source.Should().Be(CalibrationSource.UserRefinement);
    }
}
