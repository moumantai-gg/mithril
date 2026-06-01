using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class CliArgsSynthesisProbeTests
{
    [Fact]
    public void Parses_truth_cal_five_tuple()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe",
            "--screenshot", "x.png",
            "--area", "AreaEltibule",
            "--truth-cal", "0.82,0.0,100.5,200.5,false",
        })!;

        args.TruthCal.Should().NotBeNull();
        args.TruthCal!.Value.Scale.Should().Be(0.82);
        args.TruthCal.Value.Rot.Should().Be(0.0);
        args.TruthCal.Value.Ox.Should().Be(100.5);
        args.TruthCal.Value.Oy.Should().Be(200.5);
        args.TruthCal.Value.Mirror.Should().BeFalse();
    }

    [Fact]
    public void Parses_ransac_seeds_csv_path()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe",
            "--screenshot", "x.png",
            "--area", "AreaEltibule",
            "--ransac-seeds-csv", "C:/seeds.csv",
        })!;
        args.RansacSeedsCsvPath.Should().Be("C:/seeds.csv");
    }

    [Fact]
    public void Parses_trace_console_flag()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe", "--screenshot", "x.png", "--area", "AreaEltibule",
            "--trace-console",
        })!;
        args.TraceConsole.Should().BeTrue();
    }

    [Fact]
    public void Parses_otlp_endpoint()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe", "--screenshot", "x.png", "--area", "AreaEltibule",
            "--otlp", "http://localhost:4317",
        })!;
        args.OtlpEndpoint.Should().Be("http://localhost:4317");
    }

    [Fact]
    public void Parses_aligned_base_path()
    {
        var args = CliArgs.Parse(new[]
        {
            "--phase", "synthesis-probe", "--screenshot", "x.png", "--area", "AreaEltibule",
            "--aligned-base", "C:/some/base.png",
        })!;
        args.AlignedBasePath.Should().Be("C:/some/base.png");
    }
}
