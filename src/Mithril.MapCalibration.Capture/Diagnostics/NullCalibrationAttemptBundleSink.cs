namespace Mithril.MapCalibration.Capture.Diagnostics;

/// <summary>
/// No-op sink. Used when CaptureDiagnosticsOptions.DumpCalibrationBundles is off.
/// </summary>
public sealed class NullCalibrationAttemptBundleSink : ICalibrationAttemptBundleSink
{
    public static readonly NullCalibrationAttemptBundleSink Instance = new();

    public void Write(CalibrationAttemptContext context) { }
}
