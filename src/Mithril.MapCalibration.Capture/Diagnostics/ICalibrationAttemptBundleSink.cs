namespace Mithril.MapCalibration.Capture.Diagnostics;

/// <summary>
/// Persists a per-attempt diagnostic bundle. Implementations MUST be fail-soft:
/// any exception must be swallowed and logged, never propagated into the
/// calling AutoCalibrationEngine.
/// </summary>
public interface ICalibrationAttemptBundleSink
{
    void Write(CalibrationAttemptContext context);
}
