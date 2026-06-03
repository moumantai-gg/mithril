using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Mithril.Shared.Game;

public sealed class GameConfig : INotifyPropertyChanged
{
    private string _gameRoot = "";
    public string GameRoot
    {
        get => _gameRoot;
        set => Set(ref _gameRoot, value);
    }

    /// <summary>
    /// The PG Unity/Steam <b>install</b> directory (e.g.
    /// <c>…\steamapps\common\Project Gorgon</c>, which contains
    /// <c>WindowsPlayer_Data\StreamingAssets</c>). This is what the map-calibration
    /// asset-extractor sidecar reads as its <c>--install</c> root. Distinct from
    /// <see cref="GameRoot"/>, which is the LocalLow <i>data</i> dir (Player.log,
    /// ChatLogs, Reports). Deliberately does NOT participate in the
    /// <see cref="PlayerLogPath"/>/<see cref="ChatLogDirectory"/>/<see cref="ReportsDirectory"/>
    /// recomputation — those stay GameRoot-only.
    /// </summary>
    private string _installRoot = "";
    public string InstallRoot
    {
        get => _installRoot;
        set => Set(ref _installRoot, value);
    }

    private double _pollIntervalSeconds = 1.0;
    public double PollIntervalSeconds
    {
        get => _pollIntervalSeconds;
        set => Set(ref _pollIntervalSeconds, Math.Max(0.1, value));
    }

    /// <summary>
    /// Case-insensitive substring matched against the foreground window's
    /// process name to decide whether the game is in focus. Covers common
    /// launcher name variations (ProjectGorgon, ProjectGorgon64, Project Gorgon,
    /// etc.) without a code change. Internal whitespace is preserved; only
    /// leading/trailing whitespace is trimmed on set.
    /// </summary>
    private string _gameProcessName = "ProjectGorgon";
    public string GameProcessName
    {
        get => _gameProcessName;
        set
        {
            // Trim only — the predicate is a case-insensitive substring match,
            // so internal whitespace (e.g. "Project Gorgon") is allowed for
            // launchers that name the executable with a space.
            var v = value?.Trim() ?? string.Empty;
            Set(ref _gameProcessName, v);
        }
    }

    /// <summary>
    /// RMS pixel-residual at or below which a calibration is considered "good"
    /// and the guided walkthrough's Confirm is ungated. Clamped to a positive
    /// value (bad input resets to 12.0). Mirrors the long-standing default in
    /// Mithril.MapCalibration (DefaultGoodResidualThresholdPx = 12.0).
    /// </summary>
    private double _calibrationGoodResidualPx = 12.0;
    public double CalibrationGoodResidualPx
    {
        get => _calibrationGoodResidualPx;
        set => Set(ref _calibrationGoodResidualPx, value > 0 ? value : 12.0);
    }

    public string PlayerLogPath => string.IsNullOrEmpty(GameRoot) ? "" : Path.Combine(GameRoot, "Player.log");
    public string ChatLogDirectory => string.IsNullOrEmpty(GameRoot) ? "" : Path.Combine(GameRoot, "ChatLogs");
    public string ReportsDirectory => string.IsNullOrEmpty(GameRoot) ? "" : Path.Combine(GameRoot, "Reports");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(GameRoot))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayerLogPath)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChatLogDirectory)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReportsDirectory)));
        }
    }
}
