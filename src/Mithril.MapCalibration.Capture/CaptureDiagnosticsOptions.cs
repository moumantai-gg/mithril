namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Debug knobs for the capture/auto-calibration seam. Off by default — flip
/// <see cref="DumpCalibrationBundles"/> on to have <see cref="AutoCalibrationEngine"/>
/// write a per-attempt diagnostic bundle under
/// <c>%LocalAppData%/Mithril/diagnostics/calibration/</c> for every attempt that
/// reaches the capture stage.
///
/// <para>Threaded through DI as a singleton (see
/// <c>CaptureServiceCollectionExtensions.AddMithrilMapCalibrationCapture</c>); a
/// settings surface can bind the flag. Kept a plain mutable POCO so it can be
/// flipped at runtime without re-resolving the graph.</para>
/// </summary>
public sealed class CaptureDiagnosticsOptions
{
    /// <summary>
    /// When <see langword="true"/>, <see cref="AutoCalibrationEngine"/> writes a
    /// per-attempt diagnostic bundle to
    /// <c>%LocalAppData%/Mithril/diagnostics/calibration/&lt;area&gt;-&lt;ts&gt;-&lt;outcome&gt;/</c>
    /// for every attempt that reaches the capture stage (outcomes that have no data to
    /// bundle — no area, PG not foreground, no bbox — are skipped).
    /// Default <see langword="false"/>.
    /// </summary>
    public bool DumpCalibrationBundles { get; set; }
}
