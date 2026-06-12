using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public class MapCalibrationDetectorOptionsTests
{
    [Fact]
    public void Defaults_match_spec_D5_and_D6()
    {
        var opts = new MapCalibrationDetectorOptions();
        opts.DeviationMaskingEnabled.Should().BeTrue();
        opts.BoundaryDilationPx.Should().Be(8);
        opts.FogOfWarDetectionEnabled.Should().BeTrue();
        opts.FogVarianceThreshold.Should().Be(30.0);
        opts.FogColorMin.Should().Be((byte)110);
        opts.FogColorMax.Should().Be((byte)140);
    }

    [Fact]
    public void Migrate_is_identity_for_v1()
    {
        var loaded = new MapCalibrationDetectorOptions { BoundaryDilationPx = 12 };
        var migrated = MapCalibrationDetectorOptions.Migrate(loaded);
        migrated.BoundaryDilationPx.Should().Be(12);
    }
}
