using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Capture.Diagnostics;
using Mithril.Shared.Wpf.Dialogs;

namespace Mithril.Shell.ViewModels;

/// <summary>
/// View-model for the Settings → Diagnostics panel. Exposes the
/// perf-trace toggles directly off <see cref="ShellSettings"/> so the
/// checkboxes two-way-bind to the same persisted state the shell reads
/// at startup; no separate save step needed (autosave already handles it).
///
/// Hosts the Telemetry sub-section VM (<see cref="TelemetrySettingsViewModel"/>)
/// as a composed child so the Diagnostics page renders both perf-trace
/// controls and the OTLP export configuration in one place — see mithril#815.
///
/// Also hosts the log-maintenance commands (clear diagnostics logs / clear all
/// logs / open the log folder). The only files held open at runtime — the live
/// diagnostics log (<c>mithril-&lt;date&gt;.json</c>, Serilog <c>shared:false</c>)
/// and, while recording, the active perf-session file — are skipped and clear on
/// next restart. <c>mithril-boot.log</c> is written line-by-line with
/// <c>File.AppendAllText</c> (not held open), so it IS deleted by "Clear all
/// logs" and simply re-created on the next launch.
/// </summary>
public sealed partial class DiagnosticsSettingsViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly ILogger _logger;

    public ShellSettings Settings { get; }

    /// <summary>
    /// The OTLP-export sub-section VM. Always present — ShellComposition
    /// registers fallback singletons for the scrubber-graph collaborators when
    /// AddMithrilOtlpExport is no-op (EnableOtlpExport=false) so the master
    /// toggle and connection fields remain editable. The user can opt in,
    /// restart, and the live exporter picks up the persisted settings.
    /// </summary>
    public TelemetrySettingsViewModel Telemetry { get; }

    public DiagnosticsSettingsViewModel(
        ShellSettings settings,
        TelemetrySettingsViewModel telemetry,
        IDialogService dialogs,
        ILoggerFactory loggerFactory)
    {
        Settings = settings;
        Telemetry = telemetry;
        _dialogs = dialogs;
        _logger = loggerFactory.CreateLogger("Diagnostics");
    }

    /// <summary>The folder where perf-trace sessions land. Surfaced so the
    /// settings UI can offer a "Show me" button without duplicating the
    /// path-derivation logic from <c>Program.Main</c>.</summary>
    public string PerfDirectoryHint => PerfDirectory;

    /// <summary>The unified log directory (<c>…\Mithril\Shell\logs</c>). Mirrors
    /// <see cref="PerfDirectoryHint"/> — surfaced for display and the
    /// "Open log directory" command.</summary>
    public string LogDirectoryHint => LogDirectory;

    /// <summary>The folder where per-attempt calibration diagnostic bundles land
    /// when the dump toggle (#984) is on. Surfaced read-only by delegating to
    /// <see cref="CalibrationBundleDirectories.DefaultRoot"/> — the single source of
    /// truth for the path — so the displayed hint can never drift from where bundles
    /// are written.</summary>
    public string CalibrationDumpDirectoryHint => CalibrationBundleDirectories.DefaultRoot;

    private static string ShellDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mithril", "Shell");

    private static string LogDirectory => Path.Combine(ShellDirectory, "logs");

    private static string PerfDirectory => Path.Combine(ShellDirectory, "perf");

    /// <summary>Outcome of the most recent clear command, shown beneath the buttons.</summary>
    [ObservableProperty]
    private string _maintenanceStatus = string.Empty;

    /// <summary>
    /// Deletes the rolling diagnostics stream (<c>logs\mithril-*.json</c>) only.
    /// Leaves <c>mithril-boot.log</c> / <c>mithril-crash.log</c> (different
    /// extension) and the perf sessions untouched.
    /// </summary>
    [RelayCommand]
    private void ClearMithrilLogs()
    {
        if (!_dialogs.Confirm(
                "Clear Mithril logs",
                "Delete the rolling diagnostics logs (mithril-*.json) in the log folder?\n\n" +
                "Boot and crash logs are kept. Files currently in use stay until the next restart."))
            return;

        var result = LogDirectoryCleaner.Clean(new[]
        {
            new LogDirectoryCleaner.CleanTarget(LogDirectory, "mithril-*.json"),
        });
        ReportClean("diagnostics logs", result);
    }

    /// <summary>
    /// Deletes everything in <c>logs\</c> (<c>*.json</c> + <c>*.log</c>) plus the
    /// perf-session files in <c>perf\</c>. Superset of <see cref="ClearMithrilLogsCommand"/>.
    /// Files held open are skipped.
    /// </summary>
    [RelayCommand]
    private void ClearAllLogs()
    {
        if (!_dialogs.Confirm(
                "Clear all logs",
                "This removes ALL diagnostics and boot/crash logs in the log folder " +
                "(mithril-*.json and *.log) AND every saved performance-trace session.\n\n" +
                "The active diagnostics log (and the perf session, if you're recording) " +
                "is held open and stays until the next restart. The boot log is recreated " +
                "on the next launch.\n\nContinue?"))
            return;

        var result = LogDirectoryCleaner.Clean(new[]
        {
            new LogDirectoryCleaner.CleanTarget(LogDirectory, "*.json"),
            new LogDirectoryCleaner.CleanTarget(LogDirectory, "*.log"),
            new LogDirectoryCleaner.CleanTarget(PerfDirectory, "*"),
        });
        ReportClean("all logs", result);
    }

    /// <summary>Opens the unified log directory in the OS file browser. No confirmation.</summary>
    [RelayCommand]
    private void OpenLogDirectory()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open log directory {LogDirectory}", LogDirectory);
            MaintenanceStatus = $"Could not open {LogDirectory}: {ex.Message}";
        }
    }

    private void ReportClean(string what, LogDirectoryCleaner.CleanResult result)
    {
        _logger.LogInformation(
            "Cleared {What}: {Removed} removed, {Skipped} skipped (in use)",
            what, result.Removed, result.Skipped);

        MaintenanceStatus = result.Skipped == 0
            ? $"Removed {result.Removed} file(s)."
            : $"Removed {result.Removed} file(s); {result.Skipped} in use (kept until restart).";
    }
}
