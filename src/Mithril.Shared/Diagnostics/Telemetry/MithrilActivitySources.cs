using System.Diagnostics;

namespace Mithril.Shared.Diagnostics.Telemetry;

/// <summary>
/// Canonical <see cref="ActivitySource"/> instances for Mithril, keyed by
/// logical subsystem. Producers reference these statically so the source
/// vocabulary lives in one place — when a listener (e.g. the perf-recorder
/// file exporter, or a future OTLP exporter from issue #815) filters by
/// source name, this file is the inventory.
///
/// Naming convention: <c>Mithril.&lt;subsystem&gt;[.&lt;area&gt;]</c>, mirroring the
/// <see cref="Microsoft.Extensions.Logging.ILogger"/> category vocabulary so the
/// two surfaces correlate (e.g. <c>"Arda.Player"</c> logger ↔
/// <c>"Mithril.Arda.Player"</c> ActivitySource).
///
/// Operation names (the second argument to <see cref="ActivitySource.StartActivity(string)"/>)
/// are lowercase dotted (e.g. <c>"batch.process"</c>, <c>"world_driver"</c>,
/// <c>"handle.add_item"</c>) — not enumerated here because they're per-call-site.
/// </summary>
public static class MithrilActivitySources
{
    /// <summary>WPF render loop, dispatcher operations, input, binding errors.</summary>
    public static readonly ActivitySource Wpf = new("Mithril.Wpf");

    /// <summary>Shell-level module lifecycle: discovery + activation (gate-open + view-resolve).</summary>
    public static readonly ActivitySource ShellModules = new("Mithril.Shell.Modules");

    /// <summary>Reference-data fetches (CDN, cache hit, bundled fallback) and version detection.</summary>
    public static readonly ActivitySource Reference = new("Mithril.Reference");

    /// <summary>Mithril.Overlay window lifecycle, device reset, per-area projection start (#835).</summary>
    public static readonly ActivitySource Overlay = new("Mithril.Overlay");

    /// <summary>Map auto-calibration capture pipeline: per-attempt capture → refine → solve timing (#914).</summary>
    public static readonly ActivitySource MapCalibration = new("Mithril.MapCalibration.Capture");

    // Arda pipeline sources live in Arda.Abstractions.Diagnostics.ArdaActivitySources
    // because Arda projects can't take a dependency on Mithril.Shared. Both catalogs
    // share the "Mithril." prefix below so listeners receive both uniformly.

    // MapCalibration Detection-layer sources live in Mithril.MapCalibration.
    // Diagnostics.MapCalibrationDiagnostics because Mithril.MapCalibration.csproj
    // deliberately doesn't take a dependency on Mithril.Shared (it must be
    // consumable by Mithril.Shared peers without depending up the layering).
    // The "Mithril." prefix below picks both catalogs up uniformly.

    /// <summary>Prefix all sources share — listeners filter on this so they receive every Mithril emit (including Arda's per-layer sources).</summary>
    public const string Prefix = "Mithril.";
}
