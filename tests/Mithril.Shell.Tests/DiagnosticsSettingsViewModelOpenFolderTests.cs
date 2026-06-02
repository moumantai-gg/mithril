using System.IO;
using System.Windows;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Capture.Diagnostics;
using Mithril.Shell.ViewModels;
using Mithril.Shared.Telemetry.Abstractions;
using Mithril.Shared.Telemetry.Catalog;
using Mithril.Shared.Telemetry.Export;
using Mithril.Shared.Telemetry.Settings;
using Mithril.Shared.Wpf.Dialogs;
using Xunit;

namespace Mithril.Shell.Tests;

/// <summary>
/// Smoke-tests the "Open calibration diagnostics folder" command on
/// <see cref="DiagnosticsSettingsViewModel"/>.  The command's only
/// unconditional side-effect that is safe to verify in a headless CI run is
/// that the target directory is created when missing — the
/// <c>Process.Start</c> path is intentionally not asserted (Explorer fails
/// silently without a desktop session, which is acceptable for the same
/// reason the existing <c>OpenLogDirectory</c> command is untested for the
/// Process side-effect).
/// </summary>
public sealed class DiagnosticsSettingsViewModelOpenFolderTests
{
    [Fact]
    public void OpenCalibrationDumpDirectoryCommand_creates_dump_directory_if_missing()
    {
        var dumpDir = CalibrationBundleDirectories.DefaultRoot;

        // Directory.CreateDirectory is idempotent — safe even if the dir
        // already exists from a prior run.
        var vm = BuildVm();
        vm.OpenCalibrationDumpDirectoryCommand.Execute(null);

        Directory.Exists(dumpDir).Should().BeTrue(
            "the command must create the diagnostics directory if it does not yet exist");
    }

    // ------------------------------------------------------------------ //
    //  Helper                                                              //
    // ------------------------------------------------------------------ //

    private static DiagnosticsSettingsViewModel BuildVm()
    {
        var telemetry = new TelemetrySettingsViewModel(
            new TelemetrySettings(),
            new TagCatalog(new ITagDescriptorProvider[0]),
            new HeaderValueProtection(),
            new NewlySeenTagsObserver(),
            new ExporterHealthMonitor());

        return new DiagnosticsSettingsViewModel(
            new ShellSettings(),
            telemetry,
            new AlwaysConfirmDialogService(),
            NullLoggerFactory.Instance);
    }

    /// <summary>
    /// Minimal <see cref="IDialogService"/> stub that always confirms.
    /// <c>DiagnosticsSettingsViewModel</c> calls <c>Confirm</c> before
    /// destructive log-clear operations, but not before the open-folder
    /// command — included here so the ctor compiles cleanly.
    /// </summary>
    private sealed class AlwaysConfirmDialogService : IDialogService
    {
        public bool Confirm(string title, string message) => true;
        public bool? ShowDialog(DialogViewModelBase viewModel, FrameworkElement content) => true;
    }
}
