using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Mithril.Shared.Character;
using Mithril.Shared.Hotkeys;

namespace Mithril.Shell;

public sealed class ShellSettings : INotifyPropertyChanged, IActiveCharacterPersistence, IVersionedState<ShellSettings>
{
    /// <summary>Current persisted schema version. ShellSettings became versioned in
    /// #957 (#208 hygiene — every persisted JSON root should carry a version). Bump
    /// this and add upgrade logic in <see cref="Migrate"/> when a field is
    /// renamed/retyped/removed; a purely additive field needs no bump.
    /// v1 → v2: <c>DumpCalibrationCaptureFrames</c> renamed to <c>DumpCalibrationBundles</c>;
    /// <c>DumpCalibrationGrayFrames</c> removed (#984).</summary>
    public const int Version = 2;

    /// <inheritdoc cref="IVersionedState{T}.CurrentVersion"/>
    public static int CurrentVersion => Version;

    /// <summary>Migrate a loaded instance to the current shape.
    /// v1 → v2: <c>dumpCalibrationCaptureFrames</c> (old JSON key) is lifted into
    /// <see cref="DumpCalibrationBundles"/> by the obsolete shim setter that STJ
    /// calls during deserialization; <c>dumpCalibrationGrayFrames</c> is silently
    /// dropped.  The schema version is stamped to current so the store persists the
    /// new shape on the next save.</summary>
    public static ShellSettings Migrate(ShellSettings loaded)
    {
        loaded.SchemaVersion = Version;
        return loaded;
    }

    /// <summary>Persisted schema version of this instance (see <see cref="Version"/>).</summary>
    public int SchemaVersion { get; set; } = Version;

    private string _gameRoot = "";
    public string GameRoot { get => _gameRoot; set => Set(ref _gameRoot, value); }

    /// <summary>
    /// The PG Unity/Steam <b>install</b> directory (Steam
    /// <c>…\steamapps\common\Project Gorgon</c>, contains <c>WindowsPlayer_Data</c>),
    /// consumed by the map-calibration asset-extractor sidecar. Distinct from
    /// <see cref="GameRoot"/> (the LocalLow data dir). Auto-detected when empty
    /// (<see cref="Mithril.Shared.Game.GameLocator.AutoDetectInstallRoot"/>); a manual
    /// override always wins. Purely additive field → no schema bump needed (missing
    /// key → "" on load).
    /// </summary>
    private string _installRoot = "";
    public string InstallRoot { get => _installRoot; set => Set(ref _installRoot, value); }

    /// <summary>
    /// Case-insensitive substring matched against the foreground window's
    /// process name to decide whether the game is in focus. Relocated here from
    /// <c>LegolasSettings</c> (#919) so the shared map-calibration capture engine
    /// can consume it without depending on a module. Mirrors the live
    /// <see cref="Mithril.Shared.Game.GameConfig.GameProcessName"/>.
    /// </summary>
    private string _gameProcessName = "ProjectGorgon";
    public string GameProcessName { get => _gameProcessName; set => Set(ref _gameProcessName, value); }

    /// <summary>
    /// RMS pixel-residual at or below which a calibration is considered "good".
    /// Relocated here from <c>LegolasSettings</c> (#919); mirrors the live
    /// <see cref="Mithril.Shared.Game.GameConfig.CalibrationGoodResidualPx"/>.
    /// </summary>
    private double _calibrationGoodResidualPx = 12.0;
    public double CalibrationGoodResidualPx { get => _calibrationGoodResidualPx; set => Set(ref _calibrationGoodResidualPx, value); }

    private string _activeModuleId = "";
    public string ActiveModuleId { get => _activeModuleId; set => Set(ref _activeModuleId, value); }

    private string? _activeCharacterName;
    public string? ActiveCharacterName { get => _activeCharacterName; set => Set(ref _activeCharacterName, value); }

    private string? _activeServer;
    public string? ActiveServer { get => _activeServer; set => Set(ref _activeServer, value); }

    private bool _developerMode;
    public bool DeveloperMode { get => _developerMode; set => Set(ref _developerMode, value); }

    /// <summary>Master toggle for the perf-trace harness. When false, the hotkey
    /// is a no-op and no WPF hooks are attached, so the feature costs nothing.</summary>
    private bool _enablePerfTrace;
    public bool EnablePerfTrace { get => _enablePerfTrace; set => Set(ref _enablePerfTrace, value); }

    /// <summary>When true, emit a <c>frame</c> event per render rather than
    /// aggregating to <c>frame_summary</c>. Use for short captures where
    /// per-frame fidelity matters; otherwise leave off to keep trace files small.</summary>
    private bool _verboseFrameEvents;
    public bool VerboseFrameEvents { get => _verboseFrameEvents; set => Set(ref _verboseFrameEvents, value); }

    /// <summary>When true (and <see cref="EnablePerfTrace"/> is true), starts a
    /// perf-trace session automatically right after the WPF Application is
    /// created in <c>Program.Main</c> — before the shell view-model resolves,
    /// so the initial <c>module_activated</c> event, first-frame render, and
    /// dispatcher-queue ramp during startup all land in the trace. Use this
    /// when investigating slow launches; otherwise leave off and toggle
    /// sessions via the hotkey for targeted captures.</summary>
    private bool _autoStartPerfTrace;
    public bool AutoStartPerfTrace { get => _autoStartPerfTrace; set => Set(ref _autoStartPerfTrace, value); }

    /// <summary>When true, <c>AutoCalibrationEngine</c> writes a per-attempt
    /// diagnostic bundle to <c>%LocalAppData%/Mithril/diagnostics/calibration/</c>
    /// (mirrors <c>CaptureDiagnosticsOptions.DumpCalibrationBundles</c>, #984). Off
    /// by default — a debug aid for investigating solve failures, capturing the full
    /// intermediate-artifact set (screenshot, ECC crop, deviation map, detections,
    /// recovered calibration). Renamed from <c>DumpCalibrationCaptureFrames</c> in
    /// schema v2.</summary>
    private bool _dumpCalibrationBundles;
    public bool DumpCalibrationBundles { get => _dumpCalibrationBundles; set => Set(ref _dumpCalibrationBundles, value); }

    /// <summary>
    /// Migration shim: the old JSON key <c>dumpCalibrationCaptureFrames</c> (v1
    /// shape) is deserialized here by STJ, which invokes the setter and lifts the
    /// value into <see cref="DumpCalibrationBundles"/>. Dropped from the persisted
    /// schema in v2 — this property is write-only for migration purposes only.
    /// </summary>
    [Obsolete("Renamed to DumpCalibrationBundles in schema v2. Write-only shim for migration of old persisted state.")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public bool DumpCalibrationCaptureFrames
    {
        // No getter needed — the JSON context emits DumpCalibrationBundles, not this.
        // The setter is the only path invoked: by STJ when deserializing old JSON.
        set => DumpCalibrationBundles = value;
    }

    /// <summary>
    /// Migration shim: the old JSON key <c>dumpCalibrationGrayFrames</c> (v1 shape)
    /// is silently dropped in v2. This setter absorbs the deserialized value and
    /// discards it (the gray frame is now written unconditionally inside the bundle).
    /// </summary>
    [Obsolete("Removed in schema v2 — gray frame is always included in the bundle when DumpCalibrationBundles is on. Write-only shim for migration of old persisted state.")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public bool DumpCalibrationGrayFrames
    {
        set { /* silently drop */ }
    }

    private string _uiFontFamily = "Segoe UI";
    public string UiFontFamily { get => _uiFontFamily; set => Set(ref _uiFontFamily, value); }

    private double _uiFontSize = 12.0;
    public double UiFontSize { get => _uiFontSize; set => Set(ref _uiFontSize, value); }

    private double _windowLeft = 200, _windowTop = 200, _windowWidth = 1100, _windowHeight = 700;
    public double WindowLeft { get => _windowLeft; set => Set(ref _windowLeft, value); }
    public double WindowTop { get => _windowTop; set => Set(ref _windowTop, value); }
    public double WindowWidth { get => _windowWidth; set => Set(ref _windowWidth, value); }
    public double WindowHeight { get => _windowHeight; set => Set(ref _windowHeight, value); }

    private double _sidebarWidth = 260.0;
    public double SidebarWidth { get => _sidebarWidth; set => Set(ref _sidebarWidth, value); }

    /// <summary>
    /// The persisted map-capture bbox (#947), in absolute virtual-desktop PHYSICAL
    /// pixels (the frame BitBlt reads — resolved at snip-confirm time from the snip
    /// window's own device scale, so the read path needs no DPI work).
    /// <see langword="null"/> until the user first snips a region (the legitimate
    /// "no bbox set" state). Owned by the shell — not Legolas — because the shell
    /// references both Legolas and the Capture project, so a shell-side
    /// <c>ShellMapCaptureRectStore</c> can back the Capture-defined
    /// <c>IMapCaptureRectStore</c> seam without crossing the
    /// <c>Capture ↛ Legolas.Module</c> boundary. A nullable field is purely
    /// additive (missing key → null on load), so no schema migration is needed.
    /// </summary>
    private MapCaptureBbox? _mapCaptureBbox;
    public MapCaptureBbox? MapCaptureBbox { get => _mapCaptureBbox; set => Set(ref _mapCaptureBbox, value); }

    public Dictionary<string, HotkeyBinding> HotkeyBindings { get; set; } = new();
    public Dictionary<string, bool> ModuleEagerOverrides { get; set; } = new();

    private string? _lastDismissedUpdateVersion;
    public string? LastDismissedUpdateVersion { get => _lastDismissedUpdateVersion; set => Set(ref _lastDismissedUpdateVersion, value); }

    private double _updateCheckIntervalHours = 4.0;
    public double UpdateCheckIntervalHours { get => _updateCheckIntervalHours; set => Set(ref _updateCheckIntervalHours, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T f, T v, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(f, v)) return;
        f = v;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

/// <summary>
/// The persisted map-capture bbox (#947), in absolute virtual-desktop PHYSICAL
/// pixels (the frame <c>GetDC(NULL)</c>/<c>BitBlt</c> read; origin signed on a
/// multi-monitor layout). A plain POCO so STJ source-gen serializes it as a nested
/// object on <see cref="ShellSettings.MapCaptureBbox"/>. The physical rect is
/// resolved once at snip-confirm time from the snip window's single device scale, so
/// the Capture project's region provider returns it verbatim with no read-time DPI
/// work. Mirrors the Capture project's <c>CaptureRect</c> (kept as a separate POCO
/// so the shell-settings schema doesn't take a Capture dependency in its persisted
/// shape).
/// </summary>
public sealed class MapCaptureBbox
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ShellSettings))]
[JsonSerializable(typeof(MapCaptureBbox))]
public partial class ShellSettingsJsonContext : JsonSerializerContext { }
