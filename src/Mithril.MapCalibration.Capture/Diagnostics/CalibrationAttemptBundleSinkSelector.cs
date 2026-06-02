namespace Mithril.MapCalibration.Capture.Diagnostics;

/// <summary>
/// Picks the live <see cref="ICalibrationAttemptBundleSink"/> based on
/// <see cref="CaptureDiagnosticsOptions.DumpCalibrationBundles"/>. Re-reads
/// the flag every call so a settings-UI toggle takes effect without restart.
/// </summary>
public sealed class CalibrationAttemptBundleSinkSelector
{
    private readonly CaptureDiagnosticsOptions _options;
    private readonly ICalibrationAttemptBundleSink _filesystemSink;
    private readonly ICalibrationAttemptBundleSink _nullSink;

    public CalibrationAttemptBundleSinkSelector(
        CaptureDiagnosticsOptions options,
        ICalibrationAttemptBundleSink filesystemSink,
        ICalibrationAttemptBundleSink nullSink)
    {
        _options = options;
        _filesystemSink = filesystemSink;
        _nullSink = nullSink;
    }

    /// <summary>
    /// Returns the filesystem sink when <see cref="CaptureDiagnosticsOptions.DumpCalibrationBundles"/>
    /// is <see langword="true"/>, otherwise the null sink.  Re-reads the flag on
    /// every call so a live settings toggle takes effect without an app restart.
    /// </summary>
    public ICalibrationAttemptBundleSink Resolve() =>
        _options.DumpCalibrationBundles ? _filesystemSink : _nullSink;
}
