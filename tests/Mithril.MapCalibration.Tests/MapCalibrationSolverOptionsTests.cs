using FluentAssertions;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public sealed class MapCalibrationSolverOptionsTests
{
    [Fact]
    public void Default_mode_is_Shadow()
    {
        var opts = new MapCalibrationSolverOptions();
        opts.SynthesisRerankMode.Should().Be(SynthesisRerankMode.Shadow);
        opts.SynthesisJMin.Should().Be(8.0);
        opts.SynthesisNMin.Should().Be(8);
        opts.RansacTopK.Should().Be(8);
    }

    [Fact]
    public void PropertyChanged_fires_on_mode_flip()
    {
        var opts = new MapCalibrationSolverOptions();
        var heard = new System.Collections.Generic.List<string?>();
        opts.PropertyChanged += (_, e) => heard.Add(e.PropertyName);

        opts.SynthesisRerankMode = SynthesisRerankMode.Enabled;
        opts.SynthesisRerankMode = SynthesisRerankMode.Enabled; // no event — no change

        heard.Should().Equal(nameof(MapCalibrationSolverOptions.SynthesisRerankMode));
    }
}
