using System.Diagnostics;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Game;
using Mithril.Shared.MapCalibration;
using Microsoft.Win32;

namespace Mithril.Shell.ViewModels;

public sealed partial class GameConfigViewModel : ObservableObject
{
    private readonly GameConfig _config;
    private readonly IClassDataTpkProvisioner? _tpkProvisioner;

    public GameConfigViewModel(GameConfig config, IClassDataTpkProvisioner? tpkProvisioner = null)
    {
        _config = config;
        _tpkProvisioner = tpkProvisioner;
        _gameRoot = config.GameRoot;
        _installRoot = config.InstallRoot;
        _pollSeconds = config.PollIntervalSeconds;
        _gameProcessName = config.GameProcessName;
        _calibrationGoodResidualPx = config.CalibrationGoodResidualPx;
        _tpkInstalled = _tpkProvisioner?.IsInstalled() ?? false;
        _downloadTpkStatus = _tpkInstalled
            ? "Map-decoder data is installed."
            : string.Empty;
    }

    [ObservableProperty] private string _gameRoot;
    [ObservableProperty] private string _installRoot;
    [ObservableProperty] private double _pollSeconds;
    [ObservableProperty] private string _gameProcessName;
    [ObservableProperty] private double _calibrationGoodResidualPx;

    /// <summary>Status line shown after "Detect from foreground" completes.</summary>
    [ObservableProperty] private string _detectGameProcessStatus = string.Empty;

    /// <summary>True when the classdata.tpk map-decoder package is present (#960).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadTpk))]
    private bool _tpkInstalled;

    /// <summary>
    /// Drives the download button's <c>IsEnabled</c>: offer the download only when
    /// a provisioner is wired and the tpk isn't already installed.
    /// </summary>
    public bool CanDownloadTpk => _tpkProvisioner is not null && !TpkInstalled;

    /// <summary>Status/progress line for the map-decoder-data download.</summary>
    [ObservableProperty] private string _downloadTpkStatus = string.Empty;

    public string PlayerLogPath => string.IsNullOrEmpty(GameRoot) ? "(unset)" : System.IO.Path.Combine(GameRoot, "Player.log");

    partial void OnGameRootChanged(string value)
    {
        _config.GameRoot = value;
        OnPropertyChanged(nameof(PlayerLogPath));
    }

    partial void OnInstallRootChanged(string value) => _config.InstallRoot = value;

    partial void OnPollSecondsChanged(double value) => _config.PollIntervalSeconds = value;

    partial void OnGameProcessNameChanged(string value) => _config.GameProcessName = value;

    partial void OnCalibrationGoodResidualPxChanged(double value) => _config.CalibrationGoodResidualPx = value;

    [RelayCommand]
    private void Browse()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Project Gorgon root folder (the one containing Player.log)",
        };
        if (dlg.ShowDialog() == true) GameRoot = dlg.FolderName;
    }

    [RelayCommand]
    private void AutoDetect()
    {
        var detected = GameLocator.AutoDetectGameRoot();
        if (detected is not null) GameRoot = detected;
    }

    [RelayCommand]
    private void BrowseInstall()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Project Gorgon install folder (the Steam one containing WindowsPlayer_Data)",
        };
        if (dlg.ShowDialog() == true) InstallRoot = dlg.FolderName;
    }

    [RelayCommand]
    private void AutoDetectInstall()
    {
        var detected = GameLocator.AutoDetectInstallRoot();
        if (detected is not null) InstallRoot = detected;
    }

    /// <summary>
    /// Capture the foreground window's process name 3 seconds after click,
    /// so the user has time to alt-tab to the game. Skips Mithril's own
    /// process — the foreground at click-time is always Mithril, so without
    /// the delay we'd just overwrite the setting with our own name.
    /// </summary>
    [RelayCommand]
    private async Task DetectGameProcessAsync()
    {
        for (var i = 3; i > 0; i--)
        {
            DetectGameProcessStatus = $"Switch to the game window... capturing in {i}s";
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            DetectGameProcessStatus = "No foreground window detected.";
            return;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
        {
            DetectGameProcessStatus = "Could not resolve foreground process ID.";
            return;
        }

        if (pid == (uint)Environment.ProcessId)
        {
            DetectGameProcessStatus = "Mithril was foreground — switch to the game and try again.";
            return;
        }

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            GameProcessName = proc.ProcessName;
            DetectGameProcessStatus = $"Captured: {proc.ProcessName}";
        }
        catch (Exception ex)
        {
            DetectGameProcessStatus = $"Could not read process: {ex.Message}";
        }
    }

    /// <summary>
    /// One-click download of the third-party UABEA <c>classdata.tpk</c> the
    /// map-calibration asset-extractor sidecar needs to decode PG icons (#960).
    /// Fail-soft: the provisioner never throws; any failure just updates the
    /// status line and leaves icons disabled (calibration degrades as before).
    /// </summary>
    [RelayCommand]
    private async Task DownloadTpkAsync()
    {
        if (_tpkProvisioner is null)
        {
            DownloadTpkStatus = "Map-decoder download is unavailable.";
            return;
        }

        if (_tpkProvisioner.IsInstalled())
        {
            TpkInstalled = true;
            DownloadTpkStatus = "Map-decoder data is already installed.";
            return;
        }

        DownloadTpkStatus = "Downloading map-decoder data…";
        var progress = new Progress<TpkProvisionProgress>(p =>
        {
            DownloadTpkStatus = p.TotalBytes is { } total && total > 0
                ? $"Downloading map-decoder data… {p.BytesReceived * 100 / total}%"
                : $"Downloading map-decoder data… {p.BytesReceived / 1024:N0} KB";
        });

        var result = await _tpkProvisioner.EnsureAsync(progress, CancellationToken.None);
        TpkInstalled = _tpkProvisioner.IsInstalled();
        DownloadTpkStatus = result.Status switch
        {
            TpkProvisionStatus.AlreadyPresent => "Map-decoder data is installed.",
            TpkProvisionStatus.Downloaded => "Map-decoder data installed.",
            _ => $"Download failed — map icons stay disabled. {result.Message}",
        };
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
