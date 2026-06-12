using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public class DeviationMaskCombinerTests
{
    [Fact]
    public void Combine_ORs_two_masks()
    {
        var floor = new GrayImage(2, 1, new byte[] { 255, 0 });
        var fog   = new GrayImage(2, 1, new byte[] { 0, 255 });

        var combined = DeviationMaskCombiner.Combine(floor, fog, 2, 1);

        combined.Pixels.Should().Equal(new byte[] { 255, 255 });
    }

    [Fact]
    public void Combine_returns_floor_when_fog_null()
    {
        var floor = new GrayImage(2, 1, new byte[] { 255, 0 });
        var combined = DeviationMaskCombiner.Combine(floor, fog: null, 2, 1);
        combined.Pixels.Should().Equal(new byte[] { 255, 0 });
    }

    [Fact]
    public void Combine_returns_fog_when_floor_null()
    {
        var fog = new GrayImage(2, 1, new byte[] { 0, 255 });
        var combined = DeviationMaskCombiner.Combine(floor: null, fog, 2, 1);
        combined.Pixels.Should().Equal(new byte[] { 0, 255 });
    }

    [Fact]
    public void Combine_returns_all_zeros_when_both_null()
    {
        var combined = DeviationMaskCombiner.Combine(floor: null, fog: null, 2, 1);
        combined.Pixels.Should().Equal(new byte[] { 0, 0 });
    }

    [Fact]
    public void Combine_treats_dimension_mismatch_as_null_source()
    {
        var floorWrongSize = new GrayImage(5, 1, new byte[] { 255, 255, 255, 255, 255 });
        var fog = new GrayImage(2, 1, new byte[] { 0, 255 });
        var combined = DeviationMaskCombiner.Combine(floorWrongSize, fog, 2, 1);
        // The wrong-sized floor mask is treated as null; only fog contributes.
        combined.Pixels.Should().Equal(new byte[] { 0, 255 });
    }
}
