using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Phase_synthesis_probe_is_a_recognized_phase_value()
    {
        Enum.IsDefined(typeof(Phase), Phase.SynthesisProbe).Should().BeTrue();
    }
}
