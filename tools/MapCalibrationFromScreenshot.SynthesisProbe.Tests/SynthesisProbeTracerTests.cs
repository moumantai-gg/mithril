using System.Diagnostics;
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class SynthesisProbeTracerTests
{
    [Fact]
    public void ActivitySource_emits_span_when_listener_is_attached()
    {
        var emitted = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == SynthesisProbeTracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = emitted.Add,
        };
        ActivitySource.AddActivityListener(listener);

        using (var act = SynthesisProbeTracer.Source.StartActivity("test.span"))
        {
            act?.SetTag("foo", "bar");
        }

        emitted.Should().ContainSingle(a => a.OperationName == "test.span");
        emitted[0].Tags.Should().Contain(kv => kv.Key == "foo" && kv.Value == "bar");
    }

    [Fact]
    public void No_exception_when_no_listener_attached()
    {
        var act = SynthesisProbeTracer.Source.StartActivity("never.listened");
        act.Should().BeNull();
    }
}
