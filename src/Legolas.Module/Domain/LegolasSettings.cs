using System.ComponentModel;
using Mithril.Shared.Character;

namespace Legolas.Domain;

public sealed class LegolasSettings : INotifyPropertyChanged, IVersionedState<LegolasSettings>
{
    public const int Version = 6;
    public static int CurrentVersion => Version;

    /// <summary>
    /// v1 had no schema field; v2 introduces <see cref="LegolasPinStyle"/> +
    /// <see cref="LegolasActivePinStyle"/> + per-pin <see cref="PlayerPinStyle"/>
    /// and removes the old <c>PinPending</c>/<c>PinFinalized</c>/<c>PlayerMarker</c>
    /// fields from <see cref="LegolasColors"/>. Defaults to <c>1</c> (not
    /// <see cref="Version"/>) so that v1 JSON, which lacks the
    /// <c>schemaVersion</c> field, deserializes with the legacy version and
    /// triggers <see cref="Migrate"/>. Fresh in-memory instances also start at
    /// <c>1</c> — no-op migration since their legacy fields are null — and the
    /// loader bumps to current after persisting.
    ///
    /// v2 → v3 adds <see cref="AreaCalibrations"/> (per-area projector
    /// calibration). The migration is a no-op: the new dictionary defaults to
    /// empty, which is exactly the "no calibration yet" state, so v2 JSON loads
    /// unchanged and simply starts uncalibrated per area.
    ///
    /// v3 → v4 adds <see cref="CalibrationPinStyle"/> (the in-flow #460/#477A
    /// calibration marker appearance, #478). The migration is a no-op for the
    /// same reason as v2 → v3: the new sub-object defaults via
    /// <see cref="LegolasPinStyle.CalibrationDefaults"/> to the pre-#478
    /// hardcoded look, so v3 JSON (which lacks the key) loads visually
    /// unchanged. <see cref="Migrate"/> needs no v3 → v4 branch — the v1 → v2
    /// colour-promotion block it always runs is itself a no-op on a v3 blob
    /// (the legacy colour fields are already absent / null).
    ///
    /// v4 → v5 (#919) removes <c>GameProcessName</c> + <c>CalibrationGoodResidualPx</c>,
    /// relocated to the shared <c>GameConfig</c> / <c>ShellSettings</c> store.
    /// The migration is a no-op in code: the dropped fields are simply no longer
    /// declared, so STJ ignores those keys on load and the loader's re-save
    /// strips them from disk. The one-time value carry-over (so a user's
    /// customised value isn't lost) lives in <c>GameConfigCarryOver</c> at the
    /// shell composition root, because a per-file <see cref="Migrate"/> cannot
    /// reach across into <c>shell.json</c>.
    ///
    /// v5 → v6 (#957) retires <c>MapOverlay</c>: the survey overlay window's frame
    /// is now the one shell-persisted map-capture rect
    /// (<c>ShellSettings.MapCaptureBbox</c>), so the overlay reads/writes its
    /// position through that store instead of a Legolas-owned <see cref="WindowLayout"/>.
    /// The migration is a no-op in code (the dropped field is no longer declared, so
    /// STJ ignores the orphaned <c>mapOverlay</c> key on load and the loader's re-save
    /// strips it). The one-time value carry-over into <c>shell.json</c> (DIU → physical)
    /// lives in <c>MapCaptureRectCarryOver</c> at the shell composition root, for the
    /// same cross-file reason as the #919 carry-over above. <c>InventoryOverlay</c> /
    /// <c>CalibrationOverlay</c> keep their <see cref="WindowLayout"/> bindings.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    public static LegolasSettings Migrate(LegolasSettings loaded)
    {
        if (loaded.SchemaVersion >= Version) return loaded;

        // v1 → v2: carry forward the user's customised pin colours into the
        // new style sub-states so they don't reset to defaults.
        //   * pinPending — single v1 colour, drove BOTH the survey pin's outer
        //     stroke AND its centre fill; lands in both fields.
        //   * pinFinalized — unused on main since #130; promoted to the
        //     active-pin highlight colour for users who customised it.
        //   * playerMarker — v1 player anchor's centre dot colour; lands in
        //     PlayerPinStyle.Center.FillColor (the outer ring was hardcoded
        //     white, so PlayerPinStyle.Outer.* keeps its v1-equivalent default).
        var legacyPending = loaded.Colors.LegacyPinPending;
        var legacyFinalized = loaded.Colors.LegacyPinFinalized;
        var legacyPlayer = loaded.Colors.LegacyPlayerMarker;

        if (!string.IsNullOrWhiteSpace(legacyPending))
        {
            loaded.PinStyle.Outer.StrokeColor = legacyPending;
            loaded.PinStyle.Center.FillColor = legacyPending;
        }
        if (!string.IsNullOrWhiteSpace(legacyFinalized))
        {
            loaded.ActivePinStyle.Color = legacyFinalized;
        }
        if (!string.IsNullOrWhiteSpace(legacyPlayer))
        {
            loaded.PlayerPinStyle.Center.FillColor = legacyPlayer;
        }

        loaded.Colors.LegacyPinPending = null;
        loaded.Colors.LegacyPinFinalized = null;
        loaded.Colors.LegacyPlayerMarker = null;
        return loaded;
    }

    private double _surveyDedupRadiusMetres = 5.0;
    public double SurveyDedupRadiusMetres
    {
        get => _surveyDedupRadiusMetres;
        set
        {
            var clamped = Math.Max(0, value);
            if (Math.Abs(_surveyDedupRadiusMetres - clamped) < 1e-6) return;
            _surveyDedupRadiusMetres = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SurveyDedupRadiusMetres)));
        }
    }

    private double _mapTargetDedupRadiusMetres = 5.0;
    /// <summary>
    /// #454 absolute path: a new <c>ProcessMapFx</c> target whose world
    /// <c>(X,Z)</c> is within this many metres of an existing uncollected
    /// absolute pin updates that pin instead of adding a duplicate (the same
    /// node re-surveyed re-emits an identical coord). Sibling of
    /// <see cref="SurveyDedupRadiusMetres"/> (the relative-path equivalent);
    /// additive — old settings JSON without it loads the 5 m default.
    /// </summary>
    public double MapTargetDedupRadiusMetres
    {
        get => _mapTargetDedupRadiusMetres;
        set
        {
            var clamped = Math.Max(0, value);
            if (Math.Abs(_mapTargetDedupRadiusMetres - clamped) < 1e-6) return;
            _mapTargetDedupRadiusMetres = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MapTargetDedupRadiusMetres)));
        }
    }

    private double _surveyPinRadiusMetres = 8.0;
    public double SurveyPinRadiusMetres
    {
        get => _surveyPinRadiusMetres;
        set
        {
            var clamped = Math.Clamp(value, 0.5, 100.0);
            if (Math.Abs(_surveyPinRadiusMetres - clamped) < 1e-6) return;
            _surveyPinRadiusMetres = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SurveyPinRadiusMetres)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public InventoryGridSettings InventoryGrid { get; set; } = new();
    public LegolasColors Colors { get; set; } = new();
    public LegolasPinStyle PinStyle { get; set; } = new();
    public LegolasPinStyle PlayerPinStyle { get; set; } = LegolasPinStyle.PlayerDefaults();
    public LegolasActivePinStyle ActivePinStyle { get; set; } = new();

    /// <summary>
    /// Appearance of the in-flow (#460/#477A) guided-calibration markers — the
    /// placed/paired dots rendered on the survey map overlay during
    /// <c>WizardStep.Calibrating</c> (#478). Same two-shape model as the survey
    /// pin: <c>Outer</c> is the selection ring (shown only while a marker is
    /// selected for drag/nudge), <c>Center</c> the always-on dot. Defaults via
    /// <see cref="LegolasPinStyle.CalibrationDefaults"/> reproduce the pre-#478
    /// hardcoded look, so v3 JSON without this key loads visually unchanged
    /// (see <see cref="SchemaVersion"/>). The standalone calibration window's
    /// markers are deliberately out of scope and unaffected.
    /// </summary>
    public LegolasPinStyle CalibrationPinStyle { get; set; } = LegolasPinStyle.CalibrationDefaults();

    /// <summary>
    /// Per-area solved projector calibration. Lifted to
    /// <see cref="Mithril.MapCalibration.IMapCalibrationService"/> in #836; the
    /// dual-write / dual-clear / one-time import paths were retired in
    /// mithril#1041 (D6). Field is retained for one release cycle so on-disk
    /// data isn't dropped from existing <c>LegolasSettings.json</c> mid-cycle;
    /// removed in a follow-up PR after mithril#1041.
    /// </summary>
    [Obsolete("Calibrations now live exclusively in IMapCalibrationService. " +
              "Field retained for one cycle to avoid downgrade-window data loss; " +
              "removed in a follow-up PR after mithril#1041.")]
    public Dictionary<string, AreaCalibration> AreaCalibrations { get; set; } = new(StringComparer.Ordinal);
    // #957: MapOverlay retired — the survey overlay window now reads/writes its
    // frame through the one shell-persisted capture rect (ShellSettings.MapCaptureBbox,
    // via CaptureRectWindowBinder), so capture-frame and overlay-frame are a single
    // source of truth. Its last value migrates into shell.json (MapCaptureRectCarryOver).
    public WindowLayout InventoryOverlay { get; set; } = new() { Width = 540, Height = 440 };
    public WindowLayout CalibrationOverlay { get; set; } = new() { Width = 940, Height = 660 };

    // WPF stops hit-testing fully-transparent elements regardless of IsHitTestVisible,
    // so a 0-opacity overlay silently becomes unclickable. Floor at 1% — visually
    // indistinguishable from invisible, but the surface stays interactive.
    public const double MinInteractiveOpacity = 0.01;

    private double _mapOpacity = 1.0;
    public double MapOpacity
    {
        get => _mapOpacity;
        set
        {
            var clamped = Math.Max(value, MinInteractiveOpacity);
            if (Math.Abs(_mapOpacity - clamped) < 1e-6) return;
            _mapOpacity = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MapOpacity)));
        }
    }

    private double _inventoryOpacity = 1.0;
    public double InventoryOpacity
    {
        get => _inventoryOpacity;
        set
        {
            var clamped = Math.Max(value, MinInteractiveOpacity);
            if (Math.Abs(_inventoryOpacity - clamped) < 1e-6) return;
            _inventoryOpacity = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InventoryOpacity)));
        }
    }

    public bool InvertDirections { get; set; }

    private bool _clickThroughInventory;
    public bool ClickThroughInventory
    {
        get => _clickThroughInventory;
        set
        {
            if (_clickThroughInventory == value) return;
            _clickThroughInventory = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ClickThroughInventory)));
        }
    }

    private bool _clickThroughMap;
    public bool ClickThroughMap
    {
        get => _clickThroughMap;
        set
        {
            if (_clickThroughMap == value) return;
            _clickThroughMap = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ClickThroughMap)));
        }
    }
    public bool ShowBearingWedges { get; set; } = true;
    public bool ShowRouteLabels { get; set; } = true;
    public int SurveyCount { get; set; } = 6;

    private bool _autoResetWhenAllCollected = true;
    public bool AutoResetWhenAllCollected
    {
        get => _autoResetWhenAllCollected;
        set
        {
            if (_autoResetWhenAllCollected == value) return;
            _autoResetWhenAllCollected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoResetWhenAllCollected)));
        }
    }

    /// <summary>
    /// Motherlode multi-map mode (#488). Default <c>true</c> — carrying several
    /// motherlode maps is the common case. ON declares the read-order contract:
    /// after re-locating, the player reads map 1, map 2, … map N in the same
    /// fixed order at every spot, so the k-th reading at a spot binds to the
    /// k-th tracked treasure (the log carries no per-read identity). OFF =
    /// single-active-treasure serial mode. Persisted but surfaced/edited on the
    /// Motherlode panel, not the settings tab (an operational toggle flipped
    /// mid-run — see <c>MotherlodeViewModel.MultiMapMode</c>). Additive: old
    /// settings JSON without this key deserializes to the <c>true</c> default,
    /// so no <see cref="SchemaVersion"/> bump / <see cref="Migrate"/> branch is
    /// needed (same additive convention as <see cref="MapTargetDedupRadiusMetres"/>).
    /// </summary>
    private bool _motherlodeMultiMapMode = true;
    public bool MotherlodeMultiMapMode
    {
        get => _motherlodeMultiMapMode;
        set
        {
            if (_motherlodeMultiMapMode == value) return;
            _motherlodeMultiMapMode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MotherlodeMultiMapMode)));
        }
    }

    /// <summary>
    /// Hide both overlays on Done→Ready (session ends) and re-show them on
    /// Ready→Listening (next survey lands). Lets the user keep the game
    /// window uncluttered between cycles without manual toggling. Default on
    /// — opt out for users who want overlays to stay visible across runs.
    /// </summary>
    private bool _hideOverlaysBetweenSessions = true;
    public bool HideOverlaysBetweenSessions
    {
        get => _hideOverlaysBetweenSessions;
        set
        {
            if (_hideOverlaysBetweenSessions == value) return;
            _hideOverlaysBetweenSessions = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HideOverlaysBetweenSessions)));
        }
    }

    /// <summary>
    /// When true, the end-of-run report dialog auto-pops as soon as the FSM hits Done.
    /// Snapshot is taken regardless — this only gates whether the dialog opens
    /// automatically. The wizard always offers a manual "View last report" button.
    /// </summary>
    private bool _showReportOnDone = true;
    public bool ShowReportOnDone
    {
        get => _showReportOnDone;
        set
        {
            if (_showReportOnDone == value) return;
            _showReportOnDone = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowReportOnDone)));
        }
    }

    /// <summary>
    /// Last directory the user saved a report image / JSON to. Pre-fills the SaveFileDialog
    /// so successive saves land in the same folder. Null = use the OS default.
    /// </summary>
    public string? ReportSaveDirectory { get; set; }

    private bool _autoClickThroughInventoryDuringSession = true;
    public bool AutoClickThroughInventoryDuringSession
    {
        get => _autoClickThroughInventoryDuringSession;
        set
        {
            if (_autoClickThroughInventoryDuringSession == value) return;
            _autoClickThroughInventoryDuringSession = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoClickThroughInventoryDuringSession)));
        }
    }

    /// <summary>
    /// Mirror the wizard panel's nudge pad onto the map overlay. Off by
    /// default to keep the overlay clean; users who prefer to nudge without
    /// alt-tabbing flip this on.
    /// </summary>
    private bool _showNudgePadOnOverlay;
    public bool ShowNudgePadOnOverlay
    {
        get => _showNudgePadOnOverlay;
        set
        {
            if (_showNudgePadOnOverlay == value) return;
            _showNudgePadOnOverlay = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowNudgePadOnOverlay)));
        }
    }

    private bool _autoHideOverlaysOnGameUnfocused = true;
    public bool AutoHideOverlaysOnGameUnfocused
    {
        get => _autoHideOverlaysOnGameUnfocused;
        set
        {
            if (_autoHideOverlaysOnGameUnfocused == value) return;
            _autoHideOverlaysOnGameUnfocused = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoHideOverlaysOnGameUnfocused)));
        }
    }

    // #919: GameProcessName + CalibrationGoodResidualPx relocated to the shared
    // Mithril.Shared GameConfig / ShellSettings store so the map-calibration
    // capture engine (shared infra) can consume them without depending on this
    // module. The v4 → v5 migration drops the now-unused JSON keys; a one-time
    // composition-root carry-over (GameConfigCarryOver in Mithril.Shell) copies
    // any non-default legolas.json value into the shared store before this loads.

    private double _nudgeStepDefault = 1.0;
    public double NudgeStepDefault
    {
        get => _nudgeStepDefault;
        set
        {
            // Clamp to a positive minimum so a misconfigured setting can't make
            // the nudge keys silently no-op. Using DefaultValue here would leave
            // the user unable to override, so we just guard the lower bound.
            var clamped = value > 0 ? value : 1.0;
            if (Math.Abs(_nudgeStepDefault - clamped) < 1e-6) return;
            _nudgeStepDefault = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NudgeStepDefault)));
        }
    }

    private double _nudgeStepFast = 5.0;
    public double NudgeStepFast
    {
        get => _nudgeStepFast;
        set
        {
            var clamped = value > 0 ? value : 5.0;
            if (Math.Abs(_nudgeStepFast - clamped) < 1e-6) return;
            _nudgeStepFast = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NudgeStepFast)));
        }
    }

    private double _nudgeStepFine = 0.25;
    public double NudgeStepFine
    {
        get => _nudgeStepFine;
        set
        {
            var clamped = value > 0 ? value : 0.25;
            if (Math.Abs(_nudgeStepFine - clamped) < 1e-6) return;
            _nudgeStepFine = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NudgeStepFine)));
        }
    }

    /// <summary>
    /// Pin count the perf harness commands inject. Lets a developer A/B
    /// median (~30) vs. tail-of-distribution (~100+) session sizes by changing
    /// one setting instead of bouncing between separate commands. Clamped to a
    /// sensible upper bound so a fat-fingered value doesn't lock the UI thread.
    /// Diagnostic only — the harness itself is gated under developer mode.
    /// </summary>
    private int _perfHarnessPinCount = 30;
    public int PerfHarnessPinCount
    {
        get => _perfHarnessPinCount;
        set
        {
            var clamped = Math.Clamp(value, 1, 1000);
            if (_perfHarnessPinCount == clamped) return;
            _perfHarnessPinCount = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PerfHarnessPinCount)));
        }
    }
}

public sealed class WindowLayout
{
    public double Left { get; set; } = 100;
    public double Top { get; set; } = 100;
    public double Width { get; set; } = 400;
    public double Height { get; set; } = 300;
}
