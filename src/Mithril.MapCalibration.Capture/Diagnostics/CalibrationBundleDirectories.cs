using System;
using System.IO;

namespace Mithril.MapCalibration.Capture.Diagnostics;

/// <summary>
/// Canonical filesystem paths for the per-attempt calibration diagnostic bundle.
/// Centralised here so both the sink and the settings-UI hint resolve the same
/// directory without duplicating the path logic.
/// </summary>
public static class CalibrationBundleDirectories
{
    /// <summary>
    /// Root directory for calibration diagnostic bundles:
    /// <c>%LocalAppData%/Mithril/diagnostics/calibration</c>.
    /// Each attempt writes its own subdirectory under this root when the toggle
    /// (<see cref="CaptureDiagnosticsOptions.DumpCalibrationBundles"/>) is on.
    /// </summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mithril", "diagnostics", "calibration");
}
