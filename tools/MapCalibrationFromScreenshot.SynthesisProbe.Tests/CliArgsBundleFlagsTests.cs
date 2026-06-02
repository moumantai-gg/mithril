using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class CliArgsBundleFlagsTests
{
    [Fact]
    public void Parses_bundle_dir()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe", "--area", "AreaEltibule",
            "--bundle-dir", "C:/bundles/foo",
        })!;
        args.BundleDir.Should().Be("C:/bundles/foo");
    }

    [Fact]
    public void Parses_maprect_json()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe", "--area", "AreaEltibule",
            "--maprect-json", "C:/bundles/foo/04-maprect.json",
        })!;
        args.MapRectJsonPath.Should().Be("C:/bundles/foo/04-maprect.json");
    }

    [Fact]
    public void Parses_recovered_cal_json()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe", "--area", "AreaEltibule",
            "--recovered-cal-json", "C:/bundles/foo/11-recovered-cal.json",
        })!;
        args.RecoveredCalJsonPath.Should().Be("C:/bundles/foo/11-recovered-cal.json");
    }

    [Fact]
    public void Parses_aligned_deviation()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe", "--area", "AreaEltibule",
            "--aligned-deviation", "C:/bundles/foo/07-deviation.png",
        })!;
        args.AlignedDeviationPath.Should().Be("C:/bundles/foo/07-deviation.png");
    }

    [Fact]
    public void Parses_detections_json()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe", "--area", "AreaEltibule",
            "--detections-json", "C:/bundles/foo/10-detections.json",
        })!;
        args.DetectionsJsonPath.Should().Be("C:/bundles/foo/10-detections.json");
    }

    [Fact]
    public void Parses_hand_truth_cal_five_tuple()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe", "--area", "AreaEltibule",
            "--hand-truth-cal", "0.7632,3.141276,2146.21,-202.47,false",
        })!;

        args.HandTruthCal.Should().NotBeNull();
        args.HandTruthCal!.Value.Scale.Should().BeApproximately(0.7632, 1e-9);
        args.HandTruthCal.Value.Rot.Should().BeApproximately(3.141276, 1e-9);
        args.HandTruthCal.Value.Ox.Should().BeApproximately(2146.21, 1e-9);
        args.HandTruthCal.Value.Oy.Should().BeApproximately(-202.47, 1e-9);
        args.HandTruthCal.Value.Mirror.Should().BeFalse();
    }
}
