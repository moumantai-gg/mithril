using FluentAssertions;
using Mithril.MapCalibration.Capture.Diagnostics;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests.Diagnostics;

public sealed class CalibrationAttemptBundleSinkSelectorTests
{
    [Fact]
    public void Resolve_returns_filesystem_sink_when_toggle_on()
    {
        var options = new CaptureDiagnosticsOptions { DumpCalibrationBundles = true };
        var fs = new FilesystemCalibrationAttemptBundleSink("ignored", null, new AttemptBundleVisualizer());
        var selector = new CalibrationAttemptBundleSinkSelector(options, fs, NullCalibrationAttemptBundleSink.Instance);

        selector.Resolve().Should().BeSameAs(fs);
    }

    [Fact]
    public void Resolve_returns_null_sink_when_toggle_off()
    {
        var options = new CaptureDiagnosticsOptions { DumpCalibrationBundles = false };
        var fs = new FilesystemCalibrationAttemptBundleSink("ignored", null, new AttemptBundleVisualizer());
        var selector = new CalibrationAttemptBundleSinkSelector(options, fs, NullCalibrationAttemptBundleSink.Instance);

        selector.Resolve().Should().BeSameAs(NullCalibrationAttemptBundleSink.Instance);
    }

    [Fact]
    public void Resolve_reads_current_toggle_value_each_call()
    {
        var options = new CaptureDiagnosticsOptions { DumpCalibrationBundles = false };
        var fs = new FilesystemCalibrationAttemptBundleSink("ignored", null, new AttemptBundleVisualizer());
        var selector = new CalibrationAttemptBundleSinkSelector(options, fs, NullCalibrationAttemptBundleSink.Instance);

        selector.Resolve().Should().BeSameAs(NullCalibrationAttemptBundleSink.Instance);
        options.DumpCalibrationBundles = true;
        selector.Resolve().Should().BeSameAs(fs);
    }
}
