using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// Pins the load-bearing constructor defaults on
/// <see cref="MapCalibrationLocateOptions"/> so silent drift surfaces here
/// rather than in field-collected diagnostic bundles. Mirrors the pin-test
/// pattern from <c>SceneCalibrationProfileTests</c> (mithril#1173).
///
/// <para>The scale-ladder bounds are the headline contract: ScaleMin / ScaleMax
/// define the search window for <see cref="SobelPaddedPyramidRefiner"/>. A
/// regression that narrowed the window again would silently reintroduce the
/// mithril#1153 failure mode (locator picks ladder-edge degenerate regions and
/// downstream pipeline starves).</para>
/// </summary>
public sealed class MapCalibrationLocateOptionsDefaultsTests
{
    [Fact]
    public void ScaleMax_default_is_two_per_mithril_1153()
    {
        // mithril#1153: bumped 1.20 → 2.00 after two corroborating bundles four
        // days apart on Map_HogansKeepBasement (engine 3.0.0.88 / 3.0.0.96)
        // showed the true map-render scale was ~1.50 — outside the prior
        // [0.20, 1.20] range. A future PR that reverts this without a fresh
        // measurement and explicit cross-link to #1153 (or a successor that
        // bounds the search differently, e.g. an adaptive coarse stage) fails
        // here loudly.
        new MapCalibrationLocateOptions().ScaleMax.Should().Be(2.00);
    }

    [Fact]
    public void ScaleMin_default_is_unchanged_at_zero_point_two()
    {
        // mithril#1153 deliberately scoped to the upper bound — the lower
        // bound stays at the mithril#1061 default. Pin so a casual symmetric
        // widening would fail here.
        new MapCalibrationLocateOptions().ScaleMin.Should().Be(0.20);
    }

    [Fact]
    public void ScaleStep_default_is_unchanged_at_zero_point_zero_two()
    {
        new MapCalibrationLocateOptions().ScaleStep.Should().Be(0.02);
    }

    [Fact]
    public void FallbackNccFloor_default_is_unchanged_at_zero_point_two()
    {
        // mithril#1153 explicitly does NOT touch the gate floor — that's a
        // separate concern. Pin so a future change to either field is an
        // intentional, separate-PR action.
        new MapCalibrationLocateOptions().FallbackNccFloor.Should().Be(0.20);
    }
}
