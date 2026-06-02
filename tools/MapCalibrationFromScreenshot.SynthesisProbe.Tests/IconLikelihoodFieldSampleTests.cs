using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class IconLikelihoodFieldSampleTests
{
    [Fact]
    public void Sample_at_integer_position_returns_grid_value()
    {
        var field = new double[3, 3];
        field[1, 1] = 0.7;
        IconLikelihoodField.Sample(field, 1.0, 1.0).Should().BeApproximately(0.7, 1e-9);
    }

    [Fact]
    public void Sample_between_grid_points_interpolates_monotonically()
    {
        // Linearly-rising field along x: f(x,y) = x*0.1.
        var field = new double[3, 5];
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 5; x++)
                field[y, x] = x * 0.1;

        var s1 = IconLikelihoodField.Sample(field, 1.0, 1.0);
        var s15 = IconLikelihoodField.Sample(field, 1.5, 1.0);
        var s2 = IconLikelihoodField.Sample(field, 2.0, 1.0);

        s15.Should().BeGreaterThan(s1);
        s15.Should().BeLessThan(s2);
        s15.Should().BeApproximately(0.15, 0.02);  // bicubic stays close to linear on a linear field
    }

    [Fact]
    public void Sample_outside_field_returns_zero()
    {
        var field = new double[3, 3];
        for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++) field[y, x] = 1.0;

        IconLikelihoodField.Sample(field, -1.0, 1.0).Should().Be(0.0);
        IconLikelihoodField.Sample(field, 3.5, 1.0).Should().Be(0.0);
        IconLikelihoodField.Sample(field, 1.0, -0.5).Should().Be(0.0);
    }
}
