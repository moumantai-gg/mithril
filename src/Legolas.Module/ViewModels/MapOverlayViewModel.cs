using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Arda.Contracts;
using Arda.World.Player;
using Arda.World.Player.Events;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Rendering;
using Legolas.Services;
using Mithril.MapCalibration;
using Mithril.Overlay;
using Mithril.Shared.Diagnostics.Telemetry;

namespace Legolas.ViewModels;

public sealed partial class MapOverlayViewModel : ObservableObject, IDisposable
{
    private readonly SessionState _session;
    private readonly ICoordinateProjector _projector;
    private readonly IRouteOptimizer _optimizer;
    private readonly LegolasSettings? _settings;
    private readonly SurveyFlowController _surveyFlow;
    private readonly LegolasBrushes _brushes;
    private readonly PinCalibrationCoordinator? _pinCal;
    private readonly IPositionState? _positionState;
    private readonly IAreaCalibrationService? _areaCalibration;
    private readonly MotherlodeMeasurementCoordinator? _motherlode;
    private readonly ICharacterPinAnchor? _characterPin;
    private readonly ILiveMapViewService? _liveView;
    private readonly IComposedOverlayCalibrationResolver? _composedResolver;   // mithril#1096
    private readonly IOverlayWindow? _overlayWindow;                            // mithril#1096
    private readonly IDisposable? _positionSub;

    // #835 step 3: shared Mithril.Overlay marker registry. Survey pins are
    // additionally registered as IWorldOverlayMarkers entries so the new
    // overlay pipeline can render them; the legacy PinSceneRenderer path
    // remains the visible production overlay until step 6 retires
    // MapOverlayView. Optional — null in tests using the simpler ctor.
    private readonly IWorldOverlayMarkers? _markers;
    private readonly IAreaState? _areaState;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    // #835 step 6 review iteration-1 B2: per-area first-time-trace dedup
    // for the silent early-returns in RefreshCalibrationMarker. Mirrors
    // OverlayWindowService._projectionMissAreasLogged pattern. TryAdd is
    // lock-free, so the per-marker cost stays a hashed lookup. The trace
    // surfaces the reason (no area / not pairing / no service / no cal /
    // pixel-not-projectable) so silent fallbacks are observable.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte>
        _calibrationFallbackAreasLogged = new(StringComparer.Ordinal);

    // Survey → marker handle map. Keyed on the VM (stable per pin) so a
    // Survey.Model 'with' replace doesn't churn the dictionary. Reads + writes
    // run on the WPF dispatcher (CollectionChanged + PropertyChanged fire
    // there), so no extra locking needed.
    private readonly Dictionary<SurveyItemViewModel, MarkerHandle> _surveyMarkers = new();

    // Cached latest position event — IPositionState has X/Y/Z but no timestamp/source.
    private TrackerFix? _latestTrackerFix;

    public MapOverlayViewModel(SessionState session, ICoordinateProjector projector, IRouteOptimizer optimizer, SurveyFlowController surveyFlow, LegolasBrushes brushes)
        : this(session, projector, optimizer, surveyFlow, brushes, settings: null) { }

    public MapOverlayViewModel(SessionState session, ICoordinateProjector projector, IRouteOptimizer optimizer, SurveyFlowController surveyFlow, LegolasBrushes brushes, LegolasSettings? settings, PinCalibrationCoordinator? pinCalibration = null, IPositionState? positionState = null, IDomainEventSubscriber? bus = null, IAreaCalibrationService? areaCalibration = null, MotherlodeMeasurementCoordinator? motherlode = null, ICharacterPinAnchor? characterPin = null, IWorldOverlayMarkers? markers = null, IAreaState? areaState = null, Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null, ILiveMapViewService? liveView = null, IComposedOverlayCalibrationResolver? composedResolver = null, IOverlayWindow? overlayWindow = null)
    {
        _session = session;
        _projector = projector;
        _optimizer = optimizer;
        _surveyFlow = surveyFlow;
        _brushes = brushes;
        _settings = settings;
        _pinCal = pinCalibration;
        _positionState = positionState;
        _areaCalibration = areaCalibration;
        _motherlode = motherlode;
        _characterPin = characterPin;
        _markers = markers;
        _areaState = areaState;
        _logger = loggerFactory?.CreateLogger("Legolas.MapOverlay");
        _liveView = liveView;
        _composedResolver = composedResolver;   // mithril#1096
        _overlayWindow = overlayWindow;          // mithril#1096
        if (_liveView is not null)
            _liveView.Changed += OnLiveViewChanged;
        if (_motherlode is not null)
            _motherlode.Changed += () => PostToUi(NotifyMotherlodeGuidanceChanged);
        if (_areaCalibration is not null)
            _areaCalibration.Changed += (_, _) => NotifyMotherlodeGuidanceChanged();
        if (_pinCal is not null)
        {
            _pinCal.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(PinCalibrationCoordinator.IsPairing)
                                   or nameof(PinCalibrationCoordinator.IsDropping)
                                   or nameof(PinCalibrationCoordinator.IsArmed))
                {
                    OnPropertyChanged(nameof(IsCalibrationCapturing));
                    OnPropertyChanged(nameof(IsCalibrationDropping));
                    // #835 step 5: IsPairing on/off gates the marker
                    // pipeline — re-derive every calibration marker so the
                    // registry mirrors the live phase.
                    RefreshAllCalibrationMarkers();
                }
                else if (e.PropertyName is nameof(PinCalibrationCoordinator.PromptText))
                {
                    OnPropertyChanged(nameof(CalibrationPrompt));
                }
                else if (e.PropertyName is nameof(PinCalibrationCoordinator.SelectedMarker))
                {
                    OnPropertyChanged(nameof(HasSelectedCalibrationMarker));
                }
            };

            // #835 step 5: wire the placed-marker collection into the marker
            // pipeline. New markers register on add, IsSelected/Pixel updates
            // re-derive in place, removals (Clear / partial undo) unregister.
            //
            // Iteration-2 nit I2: pre-existing markers at construction time
            // also need their initial registration — the OnCalibrationMarkers
            // Changed.NewItems path runs RefreshCalibrationMarker, mirror that
            // here for symmetry. A coordinator that already had markers
            // placed before MapOverlayViewModel was resolved would otherwise
            // stay silently unregistered until the next user mutation.
            if (_pinCal.PlacedMarkers is { } placed)
            {
                placed.CollectionChanged += OnCalibrationMarkersChanged;
                foreach (var m in placed)
                {
                    m.PropertyChanged += OnCalibrationMarkerPropertyChanged;
                    RefreshCalibrationMarker(m);
                }
            }
        }
        if (_settings is not null)
        {
            _settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LegolasSettings.SurveyPinRadiusMetres))
                {
                    OnPropertyChanged(nameof(PinRadius));
                    OnPropertyChanged(nameof(PinDiameter));
                }
            };
        }

        _session.Surveys.CollectionChanged += OnSurveysCollectionChanged;
        _session.PropertyChanged += (_, e) =>
        {
            // #835 step 3: selection drives active-pin treatment. Re-derive
            // markers for both the previously-selected and newly-selected pin
            // so the active treatment lands on the right one.
            if (e.PropertyName is nameof(SessionState.SelectedSurvey))
            {
                RefreshAllSurveyMarkers();
            }
            // #1095: when the map overlay becomes visible (survey overlay
            // enable), trigger a live-view probe so pins render at the
            // correct position from the first frame without a manual hotkey.
            if (e.PropertyName is nameof(SessionState.IsMapVisible)
                && _session.IsMapVisible)
            {
                TriggerLiveViewRefresh();
            }
            if (e.PropertyName is nameof(SessionState.PlayerPosition))
            {
                OnPropertyChanged(nameof(PlayerPosition));
                OnPropertyChanged(nameof(PlayerMarkerPixel));
                RebuildRouteGeometry();
                RebuildAllWedges();
            }
            else if (e.PropertyName is nameof(SessionState.HasPlayerPosition))
            {
                OnPropertyChanged(nameof(PlayerMarkerPixel));
            }
            else if (e.PropertyName is nameof(SessionState.SurveyPlayerPixel))
            {
                // #476: the Survey GPS moved (zone-in / teleport / calibration
                // (re)applied). It is the route start + the rendered marker +
                // the pre-first-collection guidance segment, so rebuild all
                // three.
                OnPropertyChanged(nameof(PlayerMarkerPixel));
                RebuildRouteGeometry();
            }
            else if (e.PropertyName is nameof(SessionState.SurveyPlayerMeasuredAt)
                     or nameof(SessionState.SurveyPlayerSource)
                     or nameof(SessionState.SurveyPlayerIsManual)
                     or nameof(SessionState.SurveyPlayerIsPinned))
            {
                OnPropertyChanged(nameof(PlayerAnchorStatus));
                OnPropertyChanged(nameof(IsPlayerAnchorStatusVisible));
            }
            else if (e.PropertyName is nameof(SessionState.ShowRouteLines))
            {
                RebuildRouteGeometry();
                RebuildAllWedges();
            }
            else if (e.PropertyName is nameof(SessionState.ShowBearingWedges))
            {
                RebuildAllWedges();
            }
            else if (e.PropertyName is nameof(SessionState.Mode))
            {
                // Switching between Survey and Motherlode flips wedge
                // visibility wholesale — Survey hides them, Motherlode shows —
                // and swaps which player pixel the marker reads (#476).
                OnPropertyChanged(nameof(PlayerMarkerPixel));
                OnPropertyChanged(nameof(PlayerAnchorStatus));
                OnPropertyChanged(nameof(IsPlayerAnchorStatusVisible));
                NotifyMotherlodeGuidanceChanged();
                RebuildAllWedges();
                // #1095: switching to Motherlode enables the map dot — trigger
                // a fresh live-view probe so the dot renders at the correct
                // overlay position without a manual hotkey.
                if (_session.Mode == SessionMode.Motherlode)
                    TriggerLiveViewRefresh();
            }
        };


        // Forward FSM state changes so the pin DataTemplate can gate the
        // active-pin halo on Listening (the only phase where SelectedSurvey
        // is meaningful — Gathering uses IsActiveTarget + marching ants).
        _surveyFlow.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SurveyFlowController.CurrentState))
            {
                OnPropertyChanged(nameof(IsListening));
                OnPropertyChanged(nameof(IsSettingPosition));
                OnPropertyChanged(nameof(OverlayHint));
                OnPropertyChanged(nameof(IsOverlayHintVisible));
                SetPositionCommand.NotifyCanExecuteChanged();
                CancelSetPositionCommand.NotifyCanExecuteChanged();
                // #835 step 3: FSM Listening⇄other gates the active-pin
                // treatment in the existing MapOverlayView path. Mirror that
                // in the marker pipeline by re-deriving styles.
                RefreshAllSurveyMarkers();
            }
        };

        // #476: Survey player-GPS. The tracker fix and the area calibration
        // are two independent inputs to the same projection — either changing
        // re-resolves the anchor. The Arda bus subscription delivers live
        // events; we seed from IPositionState if a fix already exists at
        // startup. The calibration Changed event covers the (common) case
        // where the area's calibration is applied after the overlay VM is
        // constructed.
        if (_positionState is not null && _areaCalibration is not null)
        {
            if (_positionState.X is { } px && _positionState.Z is { } pz)
                _latestTrackerFix = new TrackerFix(px, _positionState.Y ?? 0, pz, DateTimeOffset.UtcNow, PositionSource.Spawn);
            _positionSub = bus?.Subscribe<PlayerPositionChanged>(evt =>
            {
                var at = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;
                _latestTrackerFix = new TrackerFix(evt.X, evt.Y, evt.Z, at, evt.Source);
                PostToUi(() => RefreshSurveyPlayerAnchor(fromTrackerFix: true));
            });
            _areaCalibration.Changed += (_, _) => PostToUi(() => RefreshSurveyPlayerAnchor(fromTrackerFix: false));
            RefreshSurveyPlayerAnchor(fromTrackerFix: false);
        }

        // #497: a character-named / "@me" pin is a manual position declaration
        // (freshest-wins, sticky vs a calibration re-apply, superseded only by
        // a genuinely newer tracker fix). Not a tracker fix → fromTrackerFix:false.
        if (_characterPin is not null)
        {
            _characterPin.Changed += () => PostToUi(() => RefreshSurveyPlayerAnchor(fromTrackerFix: false));
            RefreshSurveyPlayerAnchor(fromTrackerFix: false);
        }

        // #494: keep the validate-calibration gate + live ghosts in sync when
        // the area changes or its calibration is (re)solved/cleared. Guarded
        // only on the service (independent of the #476 position tracker).
        if (_areaCalibration is not null)
        {
            _areaCalibration.Changed += (_, _) => PostToUi(OnCalibrationChanged);
            // Bootstrap: if the area was loaded before the VM constructed
            // (lazy module attach + PlayerAreaState's synchronous Snapshot
            // replay), the first Changed event already fired. Run the
            // handler once so the calibration-stamp label + the #524 zoom
            // auto-seed pick up the already-applied area.
            if (_areaCalibration.CurrentCalibration is not null)
                OnCalibrationChanged();
        }
    }

    /// <summary>
    /// Re-project the tracker's last world fix to a pixel through the current
    /// area's calibration and publish it (plus its age/source) onto the
    /// session. No tracker fix or no calibrated area ⇒ clear it (degrade
    /// silently — same "no marker" behaviour as before #476). The projection
    /// is <see cref="WorldToOverlayCalibration.ToOverlay(WorldCoord)"/> — the exact transform the
    /// <c>ProcessMapFx</c> pins use, so the marker lands in the same frame as
    /// the pins (subject to the ±10% non-affine map ceiling — it is "near you",
    /// not pixel-exact, and that is expected).
    ///
    /// <para>#476 Option&#160;C — manual-override interaction:
    /// <list type="bullet">
    /// <item><paramref name="fromTrackerFix"/> = a genuinely new fix
    /// (zone-in / teleport). Fresh data is authoritative again, so it
    /// supersedes a manual override (the override only existed to fix a
    /// <em>stale</em> anchor).</item>
    /// <item><paramref name="fromTrackerFix"/> = false (calibration
    /// re-applied). A manual override is a raw screen pixel that does not
    /// depend on the calibration, so leave it untouched; otherwise
    /// re-project auto.</item>
    /// </list></para>
    /// </summary>
    private void RefreshSurveyPlayerAnchor(bool fromTrackerFix)
    {
        // #1093 §5.3 + §10: PlayerPositionChanged is sparse (zone-in /
        // teleport only) per pg_log_timezones / signals wiki, and the other
        // triggers (_characterPin.Changed, _areaCalibration.Changed,
        // ILiveMapViewService.Changed) all sit on user-action / lifecycle cadence.
        // Information level is safe here. Skip path uses LogCalibrationFallback +
        // ProjectionSkipped(consumer=survey_anchor) so the projection-miss
        // counter family stays uniform across consumers.
        // mithril#1096: route through the shared composed-cal resolver so the
        // survey "you-are-here" anchor projects on texture-frame-only scenes.
        var (overlayCal, _, missReason) = ResolveOverlayCal();
        if (overlayCal is null && _latestTrackerFix is not null)
        {
            // Only count as a "skip" when there WAS a tracker fix to project
            // — without one, ResolveSurveyAnchor returns Cleared by design
            // (no tracker = no anchor, unrelated to calibration presence).
            var skippedArea = _areaCalibration?.CurrentScene?.MapAssetKey ?? "<unknown>";
            LogCalibrationFallback(skippedArea, "RefreshSurveyPlayerAnchor", missReason ?? "no_overlay_cal");
            MithrilMeters.LegolasCalibration.ProjectionSkipped.Add(1,
                new KeyValuePair<string, object?>("consumer", "survey_anchor"),
                new KeyValuePair<string, object?>("area", skippedArea));
        }

        // mithril#1095: resolve live-view fix for layer-2 composition.
        var anchorArea = _areaCalibration?.CurrentScene?.MapAssetKey;
        var liveFix = anchorArea is not null ? _liveView?.GetCurrent(anchorArea) : null;

        var res = ResolveSurveyAnchor(
            _latestTrackerFix,
            _characterPin?.Current,
            overlayCal,
            fromTrackerFix,
            _session.SurveyPlayerIsManual,
            _session.SurveyPlayerIsPinned,
            fix: liveFix);
        if (res is not { } r) return;   // keep current (manual sticky / no change)

        _session.SurveyPlayerPixel = r.Pixel;
        _session.SurveyPlayerMeasuredAt = r.MeasuredAt;
        _session.SurveyPlayerSource = r.Source;
        _session.SurveyPlayerIsManual = r.IsManual;
        _session.SurveyPlayerIsPinned = r.IsPinned;

        // Success log (sparse — same cadence as the inputs). Skipped when the
        // resolution cleared (no source/pixel) to avoid a "no anchor" entry
        // every Changed event in an area with no fix yet.
        if (r.Pixel is { } px)
        {
            var areaKey = _areaCalibration?.CurrentScene?.MapAssetKey ?? "<unknown>";
            _logger?.LogInformation(
                "RefreshSurveyPlayerAnchor({Area}): anchor={Px:0},{Py:0} source={Source} isManual={IsManual} isPinned={IsPinned} fromTrackerFix={FromTrackerFix}.",
                areaKey, px.X, px.Y, r.Source?.ToString() ?? "<none>", r.IsManual, r.IsPinned, fromTrackerFix);
        }
    }

    /// <summary>
    /// Pure precedence for the Survey "you are here" anchor (#476/#497),
    /// extracted so freshest-wins is unit-testable without the VM. Rules:
    /// <list type="number">
    /// <item>A character-named / <c>@me</c> map pin (#497) is the preferred
    /// <b>manual</b> anchor — its exact world coord projected through the
    /// calibration. Sticky across a calibration re-apply; superseded only by a
    /// genuinely newer tracker fix (<paramref name="fromTrackerFix"/> and
    /// <c>tracker.MeasuredAt &gt; pin.ObservedAt</c>). Needs calibration to
    /// project; uncalibrated ⇒ it can't win.</item>
    /// <item>A pixel-click manual (#476, <c>IsManual &amp;&amp; !IsPinned</c>)
    /// keeps its existing stickiness: a calibration-only refresh leaves it
    /// untouched (return <c>null</c> = no change); a fresh tracker fix
    /// supersedes it.</item>
    /// <item>Otherwise the projected tracker fix, or a full clear when there
    /// is none.</item>
    /// </list>
    /// Returns <c>null</c> to mean "leave the current anchor as-is".
    /// </summary>
    /// <summary>
    /// Pure precedence for the Survey "you are here" anchor (overload with layer-2
    /// composition via <see cref="MapViewFix"/>). When <paramref name="fix"/> is
    /// non-null the projection uses <see cref="WorldToOverlayCalibration.ToLiveOverlay"/>
    /// (layer-2 composition); when null it falls back to canonical
    /// <see cref="WorldToOverlayCalibration.ToOverlay(WorldCoord)"/> (tests + callers
    /// without a live fix). The <paramref name="currentMapZoom"/> overload is deleted
    /// (mithril#1095: CalibrationZoom removed from AreaCalibration).
    /// </summary>
    public static SurveyAnchorResolution? ResolveSurveyAnchor(
        TrackerFix? tracker,
        CharacterPinFix? pin,
        WorldToOverlayCalibration? cal,
        bool fromTrackerFix,
        bool currentIsManual,
        bool currentIsPinned,
        MapViewFix? fix = null)
    {
        if (pin is { } p && cal is { } pinCal)
        {
            var supersededByFresherAuto =
                fromTrackerFix && tracker is { } ft && ft.MeasuredAt > p.ObservedAt;
            if (!supersededByFresherAuto)
            {
                // mithril#1095: layer-2 composition when a live fix is available.
                var pwt = fix is { } f
                    ? pinCal.ToLiveOverlay(p.World, f)
                    : pinCal.ToOverlay(p.World);
                return new SurveyAnchorResolution(
                    pwt,
                    p.ObservedAt,
                    Source: null, IsManual: true, IsPinned: true);
            }
            // else: a genuinely newer zone-in/teleport wins over the pin.
        }

        // Pixel-click manual (#476): sticky against a calibration-only refresh.
        if (currentIsManual && !currentIsPinned && !fromTrackerFix)
            return null;

        if (tracker is not { } trackerFix || cal is not { } c)
            return SurveyAnchorResolution.Cleared;

        // mithril#1095: layer-2 composition when a live fix is available.
        var world = new WorldCoord(trackerFix.X, trackerFix.Y, trackerFix.Z);
        var fxp = fix is { } lf
            ? c.ToLiveOverlay(world, lf)
            : c.ToOverlay(world);
        return new SurveyAnchorResolution(
            fxp,
            trackerFix.MeasuredAt, trackerFix.Source, IsManual: false, IsPinned: false);
    }

    /// <summary>
    /// Record the user's "set my position" map click (#476 Option&#160;C,
    /// the stale-anchor override). A raw screen pixel — independent of the
    /// area calibration, no log <c>Source</c>, stamped with the click time —
    /// that wins over the projected auto anchor until the next fresh tracker
    /// fix (zone-in / teleport) takes over again.
    /// </summary>
    private void RecordManualPosition(OverlayPixel where)
    {
        _session.SurveyPlayerPixel = where;
        _session.SurveyPlayerMeasuredAt = DateTimeOffset.UtcNow;
        _session.SurveyPlayerSource = null;
        _session.SurveyPlayerIsManual = true;
        _session.SurveyPlayerIsPinned = false;   // a click is the non-pinned manual
    }

    /// <summary>
    /// Marshal to the WPF dispatcher — the tracker fires from the Player.log
    /// ingestion thread and we mutate observable session state bound to the
    /// overlay. Falls back to a direct call in headless/test contexts. Mirrors
    /// <c>PlayerLogIngestionService.PostToUi</c>.
    /// </summary>
    private static void PostToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) action();
        else dispatcher.InvokeAsync(action);
    }

    /// <summary>
    /// Releases the Arda bus subscription stored at construction. The numerous
    /// CLR <c>event +=</c> handlers wired on the same constructor remain;
    /// unhooking them would require recording delegate references at the call
    /// site and is out of scope for this change. Sufficient because the VM is
    /// registered <c>Singleton</c> and only leaks once per process.
    /// </summary>
    public void Dispose()
    {
        _positionSub?.Dispose();
    }

    /// <summary>True iff the survey FSM is in <c>Listening</c>.</summary>
    public bool IsListening => _surveyFlow.CurrentState == SurveyFlowState.Listening;

    /// <summary>#476 Option&#160;C: true while the optional manual
    /// position-override detour is active — the overlay routes the next
    /// viewport click to <see cref="RecordManualPosition"/> and the wizard
    /// shows the cancel affordance.</summary>
    public bool IsSettingPosition => _surveyFlow.CurrentState == SurveyFlowState.SettingPosition;

    /// <summary>Enter the manual position-override detour (#476). Enabled
    /// only from Listening/Gathering — the FSM guards it too, but gating the
    /// command keeps the button disabled rather than a silent no-op.</summary>
    [RelayCommand(CanExecute = nameof(CanSetPosition))]
    private void SetPosition() => _surveyFlow.RequestSetPosition();

    private bool CanSetPosition() =>
        _surveyFlow.CurrentState is SurveyFlowState.Listening or SurveyFlowState.Gathering;

    /// <summary>Abandon the detour without changing the anchor (#476).</summary>
    [RelayCommand(CanExecute = nameof(IsSettingPosition))]
    private void CancelSetPosition() => _surveyFlow.CancelSetPosition();

    // ---- #494 Validate calibration (visual ghost re-check) ---------------

    /// <summary>Projected known landmarks/NPCs for the validation overlay.
    /// Empty unless <see cref="ShowCalibrationGhosts"/>; rebuilt on toggle and
    /// whenever the area's calibration changes. Read per-frame by the D2D
    /// surface (snapshotted there).</summary>
    public ObservableCollection<GhostMarker> CalibrationGhosts { get; } = new();

    /// <summary>True when the current area has a persisted calibration — gates
    /// the wizard's "Validate calibration" affordance. Always false in the
    /// settings-less test ctor (no service).</summary>
    public bool IsCurrentAreaCalibrated => _areaCalibration?.IsCurrentAreaCalibrated == true;

    // ---- #524 legacy-recalibrate hint (zoom-slider UI deleted in #1095) ----

    /// <summary>(Per-area, session-ephemeral): areas whose legacy recalibrate
    /// hint the user dismissed during this Mithril run. Cleared on process
    /// restart (intentional — the hint reappears if the area is still
    /// legacy-stamped next session).</summary>
    private readonly HashSet<string> _legacyHintDismissedAreas = new(StringComparer.Ordinal);

    /// <summary>mithril#1095: CalibrationZoom removed from AreaCalibration; the
    /// legacy recalibrate hint (which detected pre-zoom-tracking calibrations via
    /// <c>CalibrationZoom == 1.0</c>) is retired. Always returns false.</summary>
    public bool IsLegacyRecalibrateHintVisible => false;

    /// <summary>#524: dismiss the legacy hint for the current area for the
    /// rest of this Mithril session. No persistence (a fresh process gets the
    /// hint back; recalibrating clears the underlying condition outright).</summary>
    [RelayCommand]
    private void DismissLegacyRecalibrateHint()
    {
        var key = _areaCalibration?.CurrentScene?.ParentAreaKey;
        if (key is null) return;
        _legacyHintDismissedAreas.Add(key);
        OnPropertyChanged(nameof(IsLegacyRecalibrateHintVisible));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalibrationValidationStatus))]
    private bool _showCalibrationGhosts;

    /// <summary>The map overlay's user-intended visibility captured the moment
    /// validation forced it on, so toggling validation off restores it instead
    /// of leaving the overlay stuck open. Null when validation isn't showing.</summary>
    private bool? _mapVisibleBeforeValidation;

    /// <summary>Honest status: the ghosts are an independent visual check; the
    /// fit residual is pin-click consistency, NOT accuracy.</summary>
    public string CalibrationValidationStatus
    {
        get
        {
            if (!ShowCalibrationGhosts) return string.Empty;
            var n = CalibrationGhosts.Count;
            var resid = _areaCalibration?.CurrentCalibration?.ResidualPixels;
            var residText = resid is { } r
                ? $" Last fit's pin-click consistency: {r:0} px — fit tightness, not accuracy."
                : string.Empty;
            return $"{n} known landmark/NPC marker{(n == 1 ? "" : "s")} shown. Each should sit " +
                   $"on its real map feature; a consistent offset means recalibrate " +
                   $"(usually an in-game map-zoom change).{residText}";
        }
    }

    /// <summary>Toggle the calibration-validation ghost overlay. Disabled when
    /// the area isn't calibrated (nothing to validate). The not-surveying /
    /// not-plotting-motherlodes gate lives on the wizard
    /// (<c>LegolasWizardViewModel.CanValidateCalibration</c> drives the header
    /// button's enablement and auto-calls <see cref="ForceHideCalibrationValidation"/>
    /// when a flow step is entered) so the command itself only needs the
    /// "is there anything to validate" guard.</summary>
    [RelayCommand(CanExecute = nameof(IsCurrentAreaCalibrated))]
    private void ToggleCalibrationValidation() =>
        SetCalibrationValidation(!ShowCalibrationGhosts);

    /// <summary>Single on/off path. Turning on captures the overlay's current
    /// user-intended visibility then forces it up so the markers are visible;
    /// turning off clears the markers and restores that captured visibility
    /// (don't leave the overlay stuck open just because validation opened it).</summary>
    private void SetCalibrationValidation(bool on)
    {
        // #1093 D7 — toggle is THE lifecycle anchor. Capture pre-state up
        // front so the Information log at the end can report what the user
        // asked for and what context the VM saw. Always emitted regardless
        // of `on`, so a triager grepping for "SetCalibrationValidation"
        // finds the one entry that started the chain.
        var area = _areaCalibration?.CurrentScene?.MapAssetKey ?? "<unknown>";
        var scene = _areaCalibration?.CurrentScene;
        var isCalibrated = IsCurrentAreaCalibrated;
        // mithril#1096: "usable" now means "present-OR-composable" — the resolver
        // returns non-null when either the direct overlay-frame cal exists OR a
        // texture-frame record composes onto the live surface.
        var overlayCalUsable = ResolveOverlayCal().Cal is not null;
        string action;

        if (on)
        {
            _mapVisibleBeforeValidation = _session.IsMapVisible;
            ShowCalibrationGhosts = true;
            _session.IsMapVisible = true;
            // mithril#1096 review fix: setting IsMapVisible=true triggers OverlayController
            // to Show() the overlay window, but WPF's layout pass that sizes
            // OverlaySurface.ActualWidth is async. A synchronous RebuildCalibrationGhosts
            // here sees ActualWidth=0 on first toggle, the composer's unsized_surface
            // branch fires for texture-frame-only scenes (exactly the case #1096 fixes),
            // and ghosts never build — the user toggles, sees nothing, and has to toggle
            // again to recover. Defer to Loaded priority so layout completes first.
            DeferAfterLayout(RebuildCalibrationGhosts);
            // #1095: trigger a fresh live-view probe so the ghosts render
            // against the current pan/zoom without requiring a manual hotkey.
            TriggerLiveViewRefresh();
            action = "shown_and_rebuilt";
        }
        else
        {
            ShowCalibrationGhosts = false;
            CalibrationGhosts.Clear();
            if (_mapVisibleBeforeValidation is { } prev)
                _session.IsMapVisible = prev;
            _mapVisibleBeforeValidation = null;
            action = "hidden_and_cleared";
        }
        OnPropertyChanged(nameof(CalibrationValidationStatus));

        _logger?.LogInformation(
            "SetCalibrationValidation(on={On}, area={Area}, scene={Scene}, isCalibrated={IsCalibrated}, overlayCalUsable={OverlayCalUsable}): {Action} → ghostsBuilt={GhostsBuilt}.",
            on, area, scene?.SceneFriendlyName ?? "<none>", isCalibrated, overlayCalUsable, action, CalibrationGhosts.Count);
    }

    /// <summary>#495: the wizard calls this when the user enters a step where
    /// validation isn't available (surveying / plotting motherlodes) — remove
    /// the markers and restore the overlay's prior visibility. No-op when not
    /// showing.</summary>
    public void ForceHideCalibrationValidation()
    {
        if (ShowCalibrationGhosts) SetCalibrationValidation(false);
    }

    private void RebuildCalibrationGhosts()
    {
        // #1093 §5.3: state-change frequency (fires on toggle / area-change /
        // recalibrate / zoom slider while showing) → safe to log at Information
        // on the success path. Span + histogram pair lets the perf-recorder
        // surface "how often / how long / what shape." Producer cost is zero
        // when no listener is attached.
        using var act = MithrilActivitySources.LegolasCalibration.StartActivity("calibration.ghosts.rebuild");
        var sw = Stopwatch.StartNew();

        CalibrationGhosts.Clear();
        // mithril#1096: route through the shared composed-cal resolver when wired
        // (so a texture-frame-only record renders pink dots via composition) and
        // fall back to direct-overlay-only when not wired (legacy test contracts).
        var (cal, path, missReason) = ResolveOverlayCal();
        if (cal is null)
        {
            // #1093 D4 + §5.3 skip path: the dedup helper is the "human-readable
            // explanation" (one Trace per (area, callSite, reason)); the meter
            // is the "how often" answer (every call). Use the live scene's
            // MapAssetKey as the area; fall back when no scene resolved yet.
            var skippedArea = _areaCalibration?.CurrentScene?.MapAssetKey ?? "<unknown>";
            LogCalibrationFallback(skippedArea, "RebuildCalibrationGhosts", missReason ?? "no_overlay_cal");
            MithrilMeters.LegolasCalibration.ProjectionSkipped.Add(1,
                new KeyValuePair<string, object?>("consumer", "ghosts"),
                new KeyValuePair<string, object?>("area", skippedArea));
            act?.SetTag("cal.path", "none");
            act?.SetTag("area", skippedArea);
            return;
        }
        // mithril#1095: layer-2 composition — resolve the live MapViewFix for this
        // area and pass it to GhostLabelDeclutter.Build. If no fix is available yet,
        // fall back to canonical projection (no layer-2 applied).
        var areaKey = _areaCalibration?.CurrentScene?.MapAssetKey ?? "<unknown>";
        var ghostFix = areaKey != "<unknown>" ? _liveView?.GetCurrent(areaKey) : null;
        var refs = _areaCalibration!.CurrentAreaReferences;
        foreach (var g in GhostLabelDeclutter.Build(refs, cal.Value, ghostFix))
            CalibrationGhosts.Add(g);
        OnPropertyChanged(nameof(CalibrationValidationStatus));

        // #1093 §5.3 success path. WorldToOverlayCalibration carries the math
        // but not the picked record's Source/ResidualPixels; pull those from
        // AreaCalibration when available (typical case in production — both
        // come out of the same picker call). Sentinels match the
        // <c>cal.source</c>/<c>cal.residual_px</c> vocabulary in the tag
        // descriptor file.
        var source = _areaCalibration.CurrentCalibration?.Source.ToString() ?? "<unknown>";
        var residual = _areaCalibration.CurrentCalibration?.ResidualPixels ?? double.NaN;
        _logger?.LogInformation(
            "RebuildCalibrationGhosts({Area}): built {Ghosts} from {Refs} refs (cal source={Source}, residual={Residual:0.00}px).",
            areaKey, CalibrationGhosts.Count, refs.Count, source, residual);

        act?.SetTag("area", areaKey);
        act?.SetTag("refs_count", refs.Count);
        act?.SetTag("ghosts_built", CalibrationGhosts.Count);
        act?.SetTag("cal.path", path switch
        {
            CalPath.DirectOverlay => "direct_overlay",
            CalPath.ComposedFromTexture => "composed_from_texture",
            _ => "none",
        });
        act?.SetTag("cal.source", source);
        act?.SetTag("cal.residual_px", residual);

        MithrilMeters.LegolasCalibration.GhostsRebuildMs.Record(
            sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("area", areaKey),
            new KeyValuePair<string, object?>("refs_count", refs.Count),
            new KeyValuePair<string, object?>("ghosts_built", CalibrationGhosts.Count));
    }

    /// <summary>Area switched or its calibration (re)solved/cleared. Refresh
    /// the gate and live ghosts. Marshalled to the UI thread by the caller.</summary>
    private void OnCalibrationChanged()
    {
        OnPropertyChanged(nameof(IsCurrentAreaCalibrated));
        ToggleCalibrationValidationCommand.NotifyCanExecuteChanged();
        // #1093 §5.3: track which branch fired so the Information log at the
        // end names the action the triager actually wants — `drop_validation`,
        // `rebuild`, or `noop` (the most common case: the calibration changed
        // but ghosts weren't showing, so no UI rebuild was needed).
        string action;
        if (!IsCurrentAreaCalibrated && ShowCalibrationGhosts)
        {
            SetCalibrationValidation(false);   // calibration gone — drop + restore
            action = "drop_validation";
        }
        else if (ShowCalibrationGhosts)
        {
            RebuildCalibrationGhosts();
            action = "rebuild";
        }
        else
        {
            action = "noop";
        }
        OnPropertyChanged(nameof(CalibrationValidationStatus));
        // Area switch / recalibrate flips legacy-hint condition (a recalibration
        // moves CalibrationZoom off 1.0; an area change makes the per-area
        // dismissal set re-evaluate against the new key).
        OnPropertyChanged(nameof(IsLegacyRecalibrateHintVisible));

        // #1093 §5.3: state-change frequency — fires on area-switch /
        // recalibrate / clear. Information level lets the triager grep one
        // line per gate flip without flooding the log.
        _logger?.LogInformation(
            "OnCalibrationChanged({Area}): IsCalibrated={IsCalibrated} ShowGhosts={ShowGhosts} → {Action}.",
            _areaCalibration?.CurrentScene?.MapAssetKey ?? "<unknown>",
            IsCurrentAreaCalibrated, ShowCalibrationGhosts, action);
    }

    /// <summary>Raised by <see cref="ILiveMapViewService.Changed"/> (UI thread)
    /// after a fresh view-fix probe completes. Marks projection-dependent
    /// collections dirty so their getters re-read the new fix. The actual
    /// re-projection is delegated to the getters / rebuild methods wired in
    /// P2.4.</summary>
    private void OnLiveViewChanged(string area)
    {
        OnPropertyChanged(nameof(CalibrationGhosts));
        OnPropertyChanged(nameof(MotherlodeMarkerPixels));
        OnPropertyChanged(nameof(MotherlodeGuidanceOverlay));
        OnPropertyChanged(nameof(LiveViewStatusText));
    }

    /// <summary>
    /// Short live-view status for the overlay header badge. Shows the most
    /// recently measured fix age + view scale, or a human-readable failure /
    /// not-measured reason. Empty when no area is current (uncalibrated or
    /// before the first area-change event).
    ///
    /// <para>Updated by <see cref="OnLiveViewChanged"/> after each probe
    /// completes, so the badge reflects the actual status without polling.</para>
    /// </summary>
    public string LiveViewStatusText
    {
        get
        {
            var area = _areaCalibration?.CurrentScene?.MapAssetKey;
            if (string.IsNullOrEmpty(area) || _liveView is null) return string.Empty;
            var status = _liveView.GetStatus(area);
            var fix = _liveView.GetCurrent(area);
            return status switch
            {
                LiveMapViewStatus.Detected when fix is { } f =>
                    $"View: detected ({f.MeasuredAt.LocalDateTime:HH:mm:ss}) — {f.ViewScale:0.00}×",
                LiveMapViewStatus.Detecting => "View: detecting…",
                LiveMapViewStatus.FailedNoBaseTexture => "View: failed — no base texture for this area",
                LiveMapViewStatus.FailedNoCapture => "View: failed — overlay not capturable",
                LiveMapViewStatus.FailedLowConfidence => "View: failed — couldn't match base texture",
                _ => "View: not measured — use the re-detect hotkey with the map open",
            };
        }
    }

    /// <summary>
    /// Fire-and-forget refresh of the live view for the current area.
    /// Wired at every user-gesture that "enables" marker rendering: toggle
    /// validation on, switch to Motherlode mode (map dot needs a fix), and
    /// show the map overlay. If no area or service is present the call is a
    /// no-op. Errors are captured inside <see cref="ILiveMapViewService"/>
    /// itself (the status badge surfaces them).
    /// </summary>
    private void TriggerLiveViewRefresh()
    {
        var area = _areaCalibration?.CurrentScene?.MapAssetKey;
        if (string.IsNullOrEmpty(area) || _liveView is null) return;
        _ = _liveView.RefreshAsync(area);
    }

    /// <summary>#460/#477A: true while the guided calibration walkthrough is in
    /// its <see cref="CalibrationPhase.Pair"/> phase — the overlay captures
    /// left-clicks (pair the named pin / select+drag a marker), so it must NOT
    /// be click-through. The view routes viewport clicks to
    /// <see cref="PairCalibrationClick"/> / marker selection while this holds.</summary>
    public bool IsCalibrationCapturing => _pinCal?.IsPairing == true;

    /// <summary>#477A: true while the walkthrough is in
    /// <see cref="CalibrationPhase.Drop"/> — the overlay must be click-through
    /// so right-clicks reach the game to drop pins. Drives the view's
    /// phase-aware click-through override (the panel button toggles the phase,
    /// not the overlay directly — the separate-window assumption).</summary>
    public bool IsCalibrationDropping => _pinCal?.IsDropping == true;

    /// <summary>Pair a calibration overlay-click with the currently-named
    /// (suggested/overridden) pin. No-op when not in the Pair phase.</summary>
    public void PairCalibrationClick(OverlayPixel pixel) => _pinCal?.PairClick(pixel);

    /// <summary>Mouse-down hit-test against placed calibration markers
    /// (select-then-drag correction). False ⇒ the click should pair instead.</summary>
    public bool TrySelectCalibrationMarkerAt(OverlayPixel at, double radius) =>
        _pinCal?.TrySelectMarkerAt(at, radius) == true;

    /// <summary>Drag the selected calibration marker to an absolute pixel.</summary>
    public void DragCalibrationMarkerTo(OverlayPixel at) => _pinCal?.DragSelectedTo(at);

    /// <summary>True iff a calibration marker is currently selected (so the
    /// nudge keys/pad target it ahead of survey pins / the manual anchor).</summary>
    public bool HasSelectedCalibrationMarker => _pinCal?.SelectedMarker is not null;

    /// <summary>Deselect any calibration marker (Escape, or starting a fresh
    /// pair). No-op without a coordinator.</summary>
    public void ClearCalibrationSelection() => _pinCal?.ClearSelection();

    /// <summary>The guided walkthrough's current on-overlay prompt (names the
    /// pin to click next, etc.). Empty without a coordinator.</summary>
    public string CalibrationPrompt => _pinCal?.PromptText ?? string.Empty;

    /// <summary>Click-paired calibration markers to render on the overlay
    /// (null when no coordinator — e.g. the test ctor).</summary>
    public System.Collections.ObjectModel.ObservableCollection<CalibrationMarker>? CalibrationMarkers
        => _pinCal?.PlacedMarkers;

    /// <summary>
    /// Move the currently-nudgeable target by <c>(dx, dy) * step</c>. Precedence:
    /// <list type="number">
    /// <item>a selected <b>calibration marker</b> (#477A — the guided
    /// walkthrough's just-placed/selected marker, correcting the dominant
    /// click-precision error);</item>
    /// <item>the selected <see cref="SessionState.SelectedSurvey"/> pin
    /// (a survey still wins over the manual anchor);</item>
    /// <item>the <b>manual</b> Survey player anchor (#477C) — only when no
    /// survey is selected and <see cref="SessionState.SurveyPlayerIsManual"/>;
    /// the auto/tracker-projected anchor is intentionally non-interactive
    /// (nudging a data-sourced fix would mask staleness).</item>
    /// </list>
    /// No-op otherwise. Shared by the keyboard hotkeys (NudgePinCommandBase)
    /// and the on-screen nudge pad.
    /// </summary>
    public void Nudge(double dx, double dy, double step)
    {
        if (_pinCal?.SelectedMarker is not null)
        {
            _pinCal.NudgeSelected(dx * step, dy * step);
            return;
        }

        var selected = _session.SelectedSurvey;
        if (selected is not null && selected.EffectivePixel.HasValue)
        {
            var p = selected.EffectivePixel.Value;
            CorrectSurveyCommand.Execute(
                new CorrectionArgs(selected, new OverlayPixel(p.X + dx * step, p.Y + dy * step)));
            return;
        }

        // #477C: the manual "Set my position" anchor is selectable/nudgeable on
        // this same shared layer. Mutate only SurveyPlayerPixel and keep the
        // manual flag (a fresh tracker fix still supersedes it per #476); never
        // touch the Motherlode PlayerPosition or the retired MoveAnchor model.
        // #497: a pin-sourced anchor is data-sourced (re-drop the pin to move
        // it) — excluded, like the auto fix, so a nudge can't be silently
        // overwritten on the next pin refresh.
        if (_session.Mode == SessionMode.Survey
            && _session.SurveyPlayerIsManual
            && !_session.SurveyPlayerIsPinned
            && _session.SurveyPlayerPixel is { } anchor)
        {
            _session.SurveyPlayerPixel =
                new OverlayPixel(anchor.X + dx * step, anchor.Y + dy * step);
            _session.SurveyPlayerIsManual = true;
        }
    }

    private void OnSurveysCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (SurveyItemViewModel s in e.NewItems)
            {
                s.PropertyChanged += OnSurveyPropertyChanged;
                RegisterSurveyMarker(s);
            }
        }
        if (e.OldItems is not null)
        {
            foreach (SurveyItemViewModel s in e.OldItems)
            {
                s.PropertyChanged -= OnSurveyPropertyChanged;
                UnregisterSurveyMarker(s);
            }
        }
        // A Reset (e.g. SessionState.ClearSurveys) reports no OldItems but
        // the collection is now empty. Drop any markers still in the map.
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            UnregisterAllSurveyMarkers();
        }
        // Active-last invariant (iteration-2 fix, #835 review B1): any add
        // pushed a non-active marker to the tail of _insertionOrder; if the
        // active pin existed before, it lost its tail position. Re-register
        // it so the renderer's "active halo on top" contract holds.
        if (e.NewItems is not null && _markers is not null)
        {
            var active = IsListening ? _session.SelectedSurvey : null;
            if (active is not null
                && Surveys.Contains(active)
                && !ContainsActiveInNewItems(e.NewItems, active))
            {
                RegisterSurveyMarker(active);
            }
        }
        RebuildRouteGeometry();
        RebuildAllWedges();
    }

    /// <summary>True iff one of the just-added surveys IS the active one,
    /// in which case its re-registration above would be redundant — it's
    /// already at the tail.</summary>
    private static bool ContainsActiveInNewItems(System.Collections.IList newItems, SurveyItemViewModel active)
    {
        foreach (var item in newItems)
        {
            if (ReferenceEquals(item, active)) return true;
        }
        return false;
    }

    private void OnSurveyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SurveyItemViewModel.Model))
        {
            RebuildRouteGeometry();
            if (sender is SurveyItemViewModel s)
            {
                RebuildWedgeFor(s);
                // #835 step 3: a Model swap can flip Collected/Skipped/World
                // — re-derive the marker so the registry mirrors current
                // visibility + position.
                RefreshSurveyMarker(s);
            }
        }
        else if (e.PropertyName is nameof(SurveyItemViewModel.IsActiveTarget))
        {
            // Active-target change rotates the live segment without changing
            // the rest of the route, so don't churn the full polyline.
            RebuildActiveSegment();
        }
    }

    // ---- #835 step 3: Survey marker registry plumbing -------------------

    /// <summary>Register or refresh the marker for a single Survey. Removes
    /// any prior registration so style swaps (active treatment, fill-swap)
    /// land via remove+re-add — preserves the insertion-order semantics that
    /// <c>PinSceneRenderer</c>'s "active pin last" rule depends on (when the
    /// active pin is re-registered last, it renders last in the marker list
    /// and its halo sits on top of neighbours). No-op when the marker
    /// registry isn't wired (tests using the simpler ctor) or when the survey
    /// has no absolute world coord / area key (legacy relative pins).</summary>
    private void RegisterSurveyMarker(SurveyItemViewModel s)
    {
        // #835 step 6: Survey pins are drawn by LegolasOverlaySceneDrawer
        // (the freeform scene-hook callback) reading SessionState.Surveys +
        // SelectedSurvey directly off the VM, not via the marker registry.
        // The remove-then-no-op shape preserves the surrounding callback
        // wiring (OnSurveysCollectionChanged, RefreshSurveyMarker, etc.)
        // for step 7's deletion pass; today no registry side-effects fire
        // for survey markers. The `_surveyMarkers` dictionary stays empty,
        // and UnregisterSurveyMarker is a no-op against an empty map.
        UnregisterSurveyMarker(s);
    }

    /// <summary>Drop the marker for a single Survey if registered.</summary>
    private void UnregisterSurveyMarker(SurveyItemViewModel s)
    {
        if (_markers is null) return;
        if (_surveyMarkers.Remove(s, out var handle))
        {
            _markers.RemoveMarker(handle);
        }
    }

    /// <summary>Drop every registered Survey marker. Called on a Reset
    /// CollectionChanged (e.g. <see cref="SessionState.ClearSurveys"/>) so
    /// the registry doesn't leak stale handles into the next area's session.</summary>
    private void UnregisterAllSurveyMarkers()
    {
        if (_markers is null) return;
        foreach (var (_, handle) in _surveyMarkers)
        {
            _markers.RemoveMarker(handle);
        }
        _surveyMarkers.Clear();
    }

    /// <summary>Refresh a single survey marker — used when its
    /// <see cref="SurveyItemViewModel.Model"/> swaps. Idempotent for the
    /// "no change" case (the remove+re-add does churn one handle, which is
    /// acceptable because Model swaps are user-driven, not per-frame).
    ///
    /// <para><b>Active-last invariant (iteration-2 fix, #835 review B1).</b>
    /// A non-active survey's re-register would otherwise push it to the
    /// tail of <c>_insertionOrder</c>, kicking the active pin off the tail
    /// and breaking the renderer's "active halo on top" contract. When the
    /// refreshed survey is not the active one, follow it by re-registering
    /// the active one so the tail invariant holds.</para></summary>
    private void RefreshSurveyMarker(SurveyItemViewModel s)
    {
        if (_markers is null) return;
        // Remove-then-re-add covers every possible transition uniformly:
        // collected/skipped → unregister; uncollected → re-register; active
        // selection flipped → re-add with the new style at end-of-list to
        // mirror PinSceneRenderer's active-last ordering.
        RegisterSurveyMarker(s);

        // If the refreshed survey wasn't the active one, the active pin
        // (if any) just lost its tail position. Re-register it so it
        // returns to the tail.
        var active = IsListening ? _session.SelectedSurvey : null;
        if (active is not null && !ReferenceEquals(s, active) && Surveys.Contains(active))
        {
            RegisterSurveyMarker(active);
        }
    }

    /// <summary>Re-derive every survey marker. Cheaper than tearing the
    /// whole list down — only the selected pin's style is actually
    /// changing in the active-treatment case — but
    /// <see cref="IWorldOverlayMarkers.UpdateMarker"/> doesn't accept a style
    /// swap, so a full refresh is the simplest correct path. Called on
    /// SelectedSurvey / FSM state flips.
    ///
    /// <para><b>Active-last insertion order (iteration-2 fix, #835 review B1).</b>
    /// <c>PinSceneRenderer.DrawSurveyPins</c> renders the active pin LAST so
    /// its halo sits on top of neighbouring pins. <see cref="MarkerSceneRenderer.Render"/>
    /// iterates insertion order with no active-pin special-casing, so the
    /// registry's <c>_insertionOrder</c> tail MUST be the active marker
    /// when one is selected. Iteration-1 of #835 registered surveys in
    /// source order; with the selected pin not at the end of
    /// <see cref="SessionState.Surveys"/>, its halo rendered occluded.
    /// Fix: register non-active pins first, then the active one last.</para>
    /// </summary>
    private void RefreshAllSurveyMarkers()
    {
        if (_markers is null) return;
        var active = IsListening ? _session.SelectedSurvey : null;
        foreach (var s in Surveys)
        {
            if (ReferenceEquals(s, active)) continue; // hold the active pin
            RegisterSurveyMarker(s);
        }
        // Register active last so it lands at the tail of _insertionOrder
        // and the renderer draws its halo on top of every other pin. Guard
        // against a stale selection that's no longer in Surveys (defensive;
        // SessionState wipes SelectedSurvey on ClearSurveys but the VM
        // shouldn't crash if some other path leaves a dangling reference).
        if (active is not null && Surveys.Contains(active))
        {
            RegisterSurveyMarker(active);
        }
    }

    /// <summary>Build the marker style for a Survey. Mirrors the
    /// <c>MapOverlayView.OnMapSurfaceRender</c> branch that builds
    /// <c>PinScene.SurveyOuter/Center/SurveyOuterDiameter</c> +
    /// <c>ActivePinIndex</c>/<c>ActiveTreatment</c>, so byte parity with
    /// <c>PinSceneRenderer</c>'s output via the new
    /// <see cref="LegolasSurveyMarkerDrawer"/> holds.</summary>
    private LegolasSurveyMarkerStyle BuildSurveyMarkerStyle(SurveyItemViewModel s)
    {
        var pinStyle = PinStyle;
        var outerStyle = new PinLayerStyle(
            Shape: pinStyle.Outer.Shape,
            FillColor: ParseColor(pinStyle.Outer.FillColor),
            StrokeColor: ParseColor(pinStyle.Outer.StrokeColor),
            StrokeStyle: pinStyle.Outer.StrokeStyle,
            StrokeThickness: pinStyle.Outer.StrokeThickness,
            // Survey outer Size is unused (driven by SurveyPinRadiusMetres).
            Size: 0);
        var centerStyle = new PinLayerStyle(
            Shape: pinStyle.Center.Shape,
            FillColor: ParseColor(pinStyle.Center.FillColor),
            StrokeColor: ParseColor(pinStyle.Center.StrokeColor),
            StrokeStyle: pinStyle.Center.StrokeStyle,
            StrokeThickness: pinStyle.Center.StrokeThickness,
            Size: pinStyle.Center.Size);

        ActivePinTreatmentSpec? activeSpec = null;
        if (IsListening && ReferenceEquals(s, _session.SelectedSurvey))
        {
            var aps = ActivePinStyle;
            activeSpec = new ActivePinTreatmentSpec(
                Treatment: aps.Treatment,
                Color: ParseColor(aps.Color),
                HaloPaddingPx: aps.HaloPaddingPx,
                StrokeThickness: aps.HaloThickness,
                GlowBlurRadius: aps.GlowBlurRadius);
        }

        return new LegolasSurveyMarkerStyle(outerStyle, centerStyle, PinDiameter, activeSpec);
    }

    private static Color ParseColor(string hex) => LegolasBrushes.Parse(hex);

    // ---- #835 step 4: Motherlode marker registry plumbing --------------

    // Motherlode pins + the single guidance ring tracked as a list of handles
    // so the same "tear down + rebuild" pattern as Survey markers stays simple.
    // Motherlode state mutates rarely (one event per use/distance/measurement),
    // so the cost of remove+re-add per Changed event is negligible.
    private readonly List<MarkerHandle> _motherlodeMarkers = new();

    /// <summary>Tear down and rebuild every Motherlode marker — pins (one per
    /// non-collected solved treasure) + the optional guidance ring.
    /// Runs on the WPF dispatcher (callers marshal via <see cref="PostToUi"/>).
    /// No-op without a registry / area / calibration / coordinator — same
    /// degrade-silently rule as the legacy <see cref="MotherlodeMarkerPixels"/>
    /// getter.</summary>
    private void RefreshMotherlodeMarkers()
    {
        // #835 step 6: Motherlode pins + guidance ring are drawn by
        // LegolasOverlaySceneDrawer (the freeform scene-hook callback)
        // reading MotherlodeMarkerPixels + MotherlodeGuidanceOverlay
        // directly off the VM, not via the marker registry. The remove
        // call below drains any handles a previous build may have leaked
        // (defensive for hot-reload scenarios); the rest of this method's
        // build path no longer fires.
        UnregisterAllMotherlodeMarkers();
    }

    private void UnregisterAllMotherlodeMarkers()
    {
        if (_markers is null) return;
        foreach (var h in _motherlodeMarkers) _markers.RemoveMarker(h);
        _motherlodeMarkers.Clear();
    }

    // ---- #835 step 5: Calibration marker registry plumbing -------------

    // Calibration marker -> marker handle. Keyed on the VM (CalibrationMarker
    // is an ObservableObject; identity is stable across pixel updates).
    private readonly Dictionary<CalibrationMarker, MarkerHandle> _calibrationMarkers = new();

    /// <summary>Register or refresh one calibration marker. Pixel -> world
    /// conversion uses <see cref="IMapCalibrationService.OverlayToWorld"/>;
    /// when the area has no baseline (<c>OverlayToWorld</c> returns null), the
    /// marker stays unregistered and the legacy WPF <c>ItemsControl</c> in
    /// <c>MapOverlayView.xaml</c> continues to render it. Areas without a
    /// baseline are the only case where the walkthrough starts from scratch,
    /// so the fallback path stays meaningful.</summary>
    private void RefreshCalibrationMarker(CalibrationMarker marker)
    {
        if (_markers is null) return;

        // Drop previous registration so style / pixel updates land as a
        // remove+re-add. Calibration markers are placed during the walkthrough
        // only — interaction rate is human-scale; the churn cost is fine.
        if (_calibrationMarkers.Remove(marker, out var prev))
        {
            _markers.RemoveMarker(prev);
        }

        // Per-area first-time Trace log on each silent early-return, so
        // a stuck "no calibration markers visible" symptom is observable
        // (review iteration-1 B2). Uses the same per-area dedup pattern
        // as OverlayWindowService._projectionMissAreasLogged so a busy
        // area doesn't flood the trace.
        if (_areaState?.CurrentArea is not { Length: > 0 } areaKey)
        {
            LogCalibrationFallback("(no-area)", "RefreshCalibrationMarker", "Area state has no current area key.");
            return;
        }
        // Only register while the Pair phase is live — Drop captures right-
        // clicks to the game, the marker rendering is meaningless then.
        if (_pinCal?.IsPairing != true)
        {
            // Phase flips are user-driven (Drop ⇄ Pair); chatty if logged
            // per marker. Skip the trace for this branch — it's the
            // expected steady state outside the wizard, not a fallback.
            return;
        }

        // Convert click pixel -> world via the calibration service.
        if (_areaCalibration is null)
        {
            LogCalibrationFallback(areaKey, "RefreshCalibrationMarker", "No IAreaCalibrationService injected — marker cannot anchor.");
            return;
        }
        // mithril#1096 NOT MIGRATED: this site reads CurrentOverlayCalibration directly
        // (not via ResolveOverlayCal) because the Pair-phase pixel→world inverse is
        // an overlay-frame-only operation by design — there is no overlay-frame cal yet
        // during the calibration walkthrough that BUILDS it, so texture-frame composition
        // doesn't apply. The IsPairing gate above means we only reach here when an
        // overlay-frame seed already exists (from a prior solve / community sync /
        // bundled baseline). Spec §2.
        var cal = _areaCalibration.CurrentOverlayCalibration;
        if (cal is null)
        {
            LogCalibrationFallback(areaKey, "RefreshCalibrationMarker",
                "No baseline calibration for area — calibration walkthrough requires a seed (review iter-1 B2).");
            return;
        }
        // #1076 Phase 6.5: frame-typed inverse — marker.Pixel is already
        // OverlayPixel, FromOverlay returns WorldCoord directly.
        // mithril#1095: FromOverlay is single-arg (no zoom factor — CalibrationZoom removed).
        if (cal.Value.FromOverlay(marker.Pixel) is not { } world)
        {
            LogCalibrationFallback(areaKey, "RefreshCalibrationMarker",
                "FromOverlay returned null for marker pixel — calibration shape rejected the point.");
            return;
        }

        var style = BuildCalibrationMarkerStyle(marker.IsSelected);
        _calibrationMarkers[marker] = _markers.AddMarker(areaKey, world.X, world.Z, style);
    }

    /// <summary>mithril#1096 review fix — defer <paramref name="action"/> until after the
    /// next WPF layout/render pass so newly-shown overlay surfaces have their
    /// <c>ActualWidth</c>/<c>ActualHeight</c> populated before <see cref="ResolveOverlayCal"/>
    /// is invoked. <c>DispatcherPriority.Loaded</c> is the right priority: it fires AFTER
    /// <c>Render</c> (which runs layout), so by the time the queued action runs the
    /// overlay surface is sized. When no WPF dispatcher is available (test ctor),
    /// runs synchronously — preserves legacy test behaviour.</summary>
    private static void DeferAfterLayout(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            // No WPF dispatcher (test ctor, headless): run synchronously.
            // GetSurfaceSize() returns (0,0) on the legacy test fakes anyway,
            // and the ResolveOverlayCal helper falls back to direct-overlay-only
            // when the composer isn't wired, so this preserves pre-#1096 behaviour.
            action();
            return;
        }
        dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>mithril#1096 — single point of policy for "give me a usable
    /// overlay-frame calibration for the current scene." When the composer +
    /// overlay window are wired (production, new tests), routes through the
    /// shared <see cref="IComposedOverlayCalibrationResolver"/> so texture-frame-
    /// only records compose onto the live surface (parity with OverlayWindowService).
    /// When EITHER is null (legacy test ctors that don't wire them), falls back to
    /// the pre-#1096 direct-overlay-only read so every existing test stays green.
    /// Returns <c>(Cal, Path, MissReason)</c>; consumers feed MissReason into
    /// <see cref="LogCalibrationFallback"/>'s dedup key.</summary>
    private (WorldToOverlayCalibration? Cal, CalPath Path, string? MissReason) ResolveOverlayCal()
    {
        if (_composedResolver is not null && _overlayWindow is not null)
        {
            var (w, h) = _overlayWindow.GetSurfaceSize();
            var r = _composedResolver.Resolve(_areaCalibration?.CurrentScene, w, h);
            return (r.Calibration, r.Path, r.MissReason);
        }
        // Legacy path: pre-#1096 direct-overlay-only behaviour. Mirrors what every
        // call site did before this migration; preserves the contract for test
        // ctors that don't wire the new dependencies.
        var direct = _areaCalibration?.CurrentOverlayCalibration;
        return direct is not null
            ? (direct, CalPath.DirectOverlay, null)
            : (null, CalPath.None, "no_overlay_cal");
    }

    /// <summary>Trace one calibration-marker early-return per
    /// (area, callSite, reason) so silent fallbacks are observable in production
    /// without flooding the trace on a busy area. Mirrors
    /// <c>OverlayWindowService._projectionMissAreasLogged</c>.
    /// <para>#1093 D4 generalisation: the original helper hardcoded the
    /// <c>RefreshCalibrationMarker</c> call-site name; every VM projection
    /// path (RebuildCalibrationGhosts, MotherlodeMarkerPixels,
    /// MotherlodeGuidanceOverlay, RefreshSurveyPlayerAnchor) calls in with
    /// its own <paramref name="callSite"/> so a triager can read which
    /// path silently dropped.</para></summary>
    private void LogCalibrationFallback(string areaKey, string callSite, string reason)
    {
        var dedupKey = areaKey + "|" + callSite + "|" + reason;
        if (_calibrationFallbackAreasLogged.TryAdd(dedupKey, 0))
        {
            _logger?.LogTrace(
                "MapOverlayViewModel.{CallSite} fallback for area {AreaKey}: {Reason}",
                callSite, areaKey, reason);
        }
    }

    private LegolasCalibrationMarkerStyle BuildCalibrationMarkerStyle(bool isSelected)
    {
        var s = CalibrationPinStyle;
        var outerStyle = new PinLayerStyle(
            Shape: s.Outer.Shape,
            FillColor: ParseColor(s.Outer.FillColor),
            StrokeColor: ParseColor(s.Outer.StrokeColor),
            StrokeStyle: s.Outer.StrokeStyle,
            StrokeThickness: s.Outer.StrokeThickness,
            Size: s.Outer.Size);
        var centerStyle = new PinLayerStyle(
            Shape: s.Center.Shape,
            FillColor: ParseColor(s.Center.FillColor),
            StrokeColor: ParseColor(s.Center.StrokeColor),
            StrokeStyle: s.Center.StrokeStyle,
            StrokeThickness: s.Center.StrokeThickness,
            Size: s.Center.Size);
        return new LegolasCalibrationMarkerStyle(outerStyle, centerStyle, isSelected);
    }

    private void UnregisterCalibrationMarker(CalibrationMarker marker)
    {
        if (_markers is null) return;
        if (_calibrationMarkers.Remove(marker, out var h))
        {
            _markers.RemoveMarker(h);
        }
    }

    private void UnregisterAllCalibrationMarkers()
    {
        if (_markers is null) return;
        foreach (var (_, h) in _calibrationMarkers) _markers.RemoveMarker(h);
        _calibrationMarkers.Clear();
    }

    private void RefreshAllCalibrationMarkers()
    {
        if (_markers is null) return;
        if (CalibrationMarkers is null) return;
        foreach (var m in CalibrationMarkers) RefreshCalibrationMarker(m);
    }

    private void OnCalibrationMarkersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (CalibrationMarker m in e.NewItems)
            {
                m.PropertyChanged += OnCalibrationMarkerPropertyChanged;
                RefreshCalibrationMarker(m);
            }
        }
        if (e.OldItems is not null)
        {
            foreach (CalibrationMarker m in e.OldItems)
            {
                m.PropertyChanged -= OnCalibrationMarkerPropertyChanged;
                UnregisterCalibrationMarker(m);
            }
        }
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            UnregisterAllCalibrationMarkers();
        }
    }

    private void OnCalibrationMarkerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CalibrationMarker m) return;
        if (e.PropertyName is nameof(CalibrationMarker.Pixel)
                           or nameof(CalibrationMarker.IsSelected))
        {
            RefreshCalibrationMarker(m);
        }
    }

    public ObservableCollection<SurveyItemViewModel> Surveys => _session.Surveys;

    public SessionState Session => _session;

    public SurveyFlowController SurveyFlow => _surveyFlow;

    public LegolasBrushes Brushes => _brushes;

    private static readonly LegolasPinStyle _defaultPinStyle = new();
    private static readonly LegolasPinStyle _defaultPlayerPinStyle = LegolasPinStyle.PlayerDefaults();
    private static readonly LegolasPinStyle _defaultCalibrationPinStyle = LegolasPinStyle.CalibrationDefaults();
    private static readonly LegolasActivePinStyle _defaultActivePinStyle = new();

    /// <summary>Survey pin shape configuration for the DataTemplate. Falls
    /// back to defaults when the simpler test constructor is used (no settings).</summary>
    public LegolasPinStyle PinStyle => _settings?.PinStyle ?? _defaultPinStyle;

    /// <summary>Player anchor pin shape configuration. Same shape model as
    /// <see cref="PinStyle"/> but with player-specific defaults; the player
    /// pin's outer Size is meaningful (drives Thumb bounds) while the survey
    /// pin's outer size still comes from <c>SurveyPinRadiusMetres</c>.</summary>
    public LegolasPinStyle PlayerPinStyle => _settings?.PlayerPinStyle ?? _defaultPlayerPinStyle;

    /// <summary>Active-pin highlight configuration. Falls back to defaults
    /// when the simpler test constructor is used (no settings).</summary>
    public LegolasActivePinStyle ActivePinStyle => _settings?.ActivePinStyle ?? _defaultActivePinStyle;

    /// <summary>In-flow (#460/#477A) calibration marker appearance (#478).
    /// <c>Outer</c> = the selection ring (drawn only while the marker is
    /// selected); <c>Center</c> = the always-on dot. Drives the overlay's
    /// calibration-marker DataTemplate; falls back to
    /// <see cref="LegolasPinStyle.CalibrationDefaults"/> for the test ctor.</summary>
    public LegolasPinStyle CalibrationPinStyle => _settings?.CalibrationPinStyle ?? _defaultCalibrationPinStyle;

    /// <summary>
    /// Context-aware on-overlay instruction. Empty during normal use; appears
    /// for the two states where a new user can stall:
    ///  * AwaitingPosition: needs a click to set the anchor.
    ///  * Listening with anchor placed but no surveys yet: the anchor's initial
    ///    projection scale can stick the pin off-screen (#131 follow-up). The
    ///    drag-anywhere gesture rescues it; the hint tells the user how.
    /// Hidden in Gathering/Done where the route geometry speaks for itself.
    /// </summary>
    // #454 retired the anchor-bootstrap states this hint coached through.
    // Absolute placement needs no setup. #476 reuses the (still-empty by
    // default) hint to coach the optional manual position-override click.
    public string OverlayHint =>
        IsSettingPosition
            ? "Click the map where your character is standing now."
            : string.Empty;

    public bool IsOverlayHintVisible => !string.IsNullOrEmpty(OverlayHint);

    public OverlayPixel PlayerPosition
    {
        get => _session.PlayerPosition;
        set => _session.PlayerPosition = value;
    }

    /// <summary>
    /// The "you are here" pixel the overlay renderer should draw, or null for
    /// no marker. Mode-routed (#476): Motherlode keeps its manual-click anchor
    /// (only when one has been recorded); Survey uses the projected tracker
    /// GPS (null until a fix lands in a calibrated area — degrades silently,
    /// same as pre-#476). Never presented as live: pair it with
    /// <see cref="PlayerAnchorStatus"/> so the staleness is honest.
    /// </summary>
    public OverlayPixel? PlayerMarkerPixel =>
        _session.Mode == SessionMode.Motherlode
            ? (_session.HasPlayerPosition ? _session.PlayerPosition : null)
            : _session.SurveyPlayerPixel;

    /// <summary>
    /// #113 Layer 5: solved Motherlode treasures projected to overlay pixels
    /// via the persisted area calibration. Read fresh by the per-frame D2D
    /// render handler (cheap — a handful of treasures, same cost class as the
    /// existing survey-pin loop). Empty unless in Motherlode mode <b>and</b>
    /// the area is calibrated — the projector is the only thing here that needs
    /// it; the relative-text guidance is calibration-free, so an uncalibrated
    /// area silently shows no dot rather than a wrong one. Collected treasures
    /// drop out. The dot inherits the ±10% non-affine map warp (#488) — the
    /// solved coord is exact, the marker is approximate; surfaced as such in
    /// the wizard copy.
    /// </summary>
    public IReadOnlyList<OverlayPixel> MotherlodeMarkerPixels
    {
        get
        {
            if (_session.Mode != SessionMode.Motherlode || _motherlode is null)
                return Array.Empty<OverlayPixel>();
            // mithril#1096: route through the shared composed-cal resolver so a
            // texture-frame-only record lights the motherlode markers via composition.
            var (cal, _, missReason) = ResolveOverlayCal();
            if (cal is null)
            {
                // #1093 §5.3: per-frame getter — meter + first-time-Trace
                // skip log, NO success log (would flood at ~60 Hz). The mode
                // gate above is the "motherlode not active" branch — silent
                // by design; only the calibration-null branch is the
                // "silent fallback worth surfacing" case.
                var skippedArea = _areaCalibration?.CurrentScene?.MapAssetKey ?? "<unknown>";
                LogCalibrationFallback(skippedArea, "MotherlodeMarkerPixels", missReason ?? "no_overlay_cal");
                MithrilMeters.LegolasCalibration.ProjectionSkipped.Add(1,
                    new KeyValuePair<string, object?>("consumer", "motherlode_markers"),
                    new KeyValuePair<string, object?>("area", skippedArea));
                return Array.Empty<OverlayPixel>();
            }

            // mithril#1095: layer-2 composition — resolve live MapViewFix.
            // If no fix is available yet, return empty (refuse to render stale pixels).
            var markerArea = _areaCalibration?.CurrentScene?.MapAssetKey;
            if (string.IsNullOrEmpty(markerArea)) return Array.Empty<OverlayPixel>();
            var markerFix = _liveView?.GetCurrent(markerArea);
            if (markerFix is null)
            {
                LogCalibrationFallback(markerArea, "MotherlodeMarkerPixels", "no_live_fix");
                return Array.Empty<OverlayPixel>();
            }

            List<OverlayPixel>? list = null;
            foreach (var s in _motherlode.Snapshot().Surveys)
                if (!s.Collected && s.SolvedWorld is { } w)
                {
                    // mithril#1095: layer-2 composition — project through canonical
                    // calibration then apply live fix.
                    (list ??= new()).Add(cal.Value.ToLiveOverlay(w, markerFix.Value));
                }
            return list ?? (IReadOnlyList<OverlayPixel>)Array.Empty<OverlayPixel>();
        }
    }

    /// <summary>
    /// #506: dashed tolerance ring on the overlay (calibration-gated). Empty when
    /// uncalibrated — use <see cref="MotherlodeGuidancePhrase"/> instead.
    /// </summary>
    public MotherlodeGuidanceCircle? MotherlodeGuidanceOverlay
    {
        get
        {
            if (_session.Mode != SessionMode.Motherlode || _motherlode is null)
                return null;
            // mithril#1096: route through the shared composed-cal resolver so a
            // texture-frame-only record draws the guidance ring via composition.
            var (cal, _, missReason) = ResolveOverlayCal();
            if (cal is null)
            {
                // #1093 §5.3: per-frame getter — meter + first-time-Trace
                // skip log, NO success log. See MotherlodeMarkerPixels above
                // for the rationale; identical shape, distinct consumer tag.
                var skippedArea = _areaCalibration?.CurrentScene?.MapAssetKey ?? "<unknown>";
                LogCalibrationFallback(skippedArea, "MotherlodeGuidanceOverlay", missReason ?? "no_overlay_cal");
                MithrilMeters.LegolasCalibration.ProjectionSkipped.Add(1,
                    new KeyValuePair<string, object?>("consumer", "motherlode_guidance"),
                    new KeyValuePair<string, object?>("area", skippedArea));
                return null;
            }

            var next = _motherlode.Snapshot().NextSpot;
            if (next is null) return null;

            // mithril#1095: layer-2 composition — resolve live MapViewFix.
            // If no fix is available yet, return null (refuse to render stale ring).
            var guidanceArea = _areaCalibration?.CurrentScene?.MapAssetKey;
            if (string.IsNullOrEmpty(guidanceArea)) return null;
            var guidanceFix = _liveView?.GetCurrent(guidanceArea);
            if (guidanceFix is null)
            {
                LogCalibrationFallback(guidanceArea, "MotherlodeGuidanceOverlay", "no_live_fix");
                return null;
            }

            // mithril#1095: project center through layer-2 composition; scale
            // the radius using the live ViewScale instead of the retired zoomFactor.
            var center = cal.Value.ToLiveOverlay(next.SuggestedWorld, guidanceFix.Value);
            var radiusPx = next.ToleranceRadiusMetres * cal.Value.Scale * guidanceFix.Value.ViewScale;
            return new MotherlodeGuidanceCircle(center, radiusPx, _brushes.RouteLine.Color);
        }
    }

    /// <summary>
    /// #506: relative phrase for the guided next spot (~80 m NE of …). Works
    /// without calibration; shown in the wizard when the overlay ring cannot.
    /// </summary>
    public string? MotherlodeGuidancePhrase =>
        _session.Mode == SessionMode.Motherlode && _motherlode is not null
            ? _motherlode.Snapshot().NextSpot?.RelativePhrase
            : null;

    private void NotifyMotherlodeGuidanceChanged()
    {
        OnPropertyChanged(nameof(MotherlodeGuidanceOverlay));
        OnPropertyChanged(nameof(MotherlodeGuidancePhrase));
        // #835 step 4: same set of triggers (motherlode Changed, calibration
        // Changed, mode flip, zoom slider) refreshes the marker pipeline so
        // pins + guidance ring stay in sync with the on-screen state.
        RefreshMotherlodeMarkers();
    }

    /// <summary>
    /// Short staleness label for the Survey player-GPS, e.g.
    /// <c>"You — zone-in, 4m ago"</c>, or <c>"You — set manually"</c> for the
    /// #476 Option&#160;C override. Empty outside Survey mode or when no
    /// anchor has resolved. The auto signal is sparse (zone-in / teleport
    /// only); this exists so the UI never implies the marker is live.
    /// </summary>
    public string PlayerAnchorStatus
    {
        get
        {
            if (_session.Mode != SessionMode.Survey || !_session.SurveyPlayerPixel.HasValue)
                return string.Empty;
            if (_session.SurveyPlayerIsPinned)
                return _session.SurveyPlayerMeasuredAt is { } pat
                    ? $"You — pinned, {AgoText(pat, DateTimeOffset.UtcNow)}"
                    : "You — pinned";
            if (_session.SurveyPlayerIsManual)
                return "You — set manually";
            return _session.SurveyPlayerMeasuredAt is { } at && _session.SurveyPlayerSource is { } src
                ? FormatAnchorStatus(at, src, DateTimeOffset.UtcNow)
                : string.Empty;
        }
    }

    public bool IsPlayerAnchorStatusVisible => !string.IsNullOrEmpty(PlayerAnchorStatus);

    /// <summary>
    /// Pure staleness formatter (testable without a clock dependency). Source
    /// names the freshness class — <c>Spawn</c> is the zone-in/login anchor
    /// (freshest, the typical Optimize-time state), <c>Movement</c> a sparse
    /// teleport. The age is "as of <paramref name="now"/>"; it grows between
    /// the sparse fixes, which is the honest signal that the player has likely
    /// walked away from it.
    /// </summary>
    public static string FormatAnchorStatus(DateTimeOffset measuredAt, PositionSource source, DateTimeOffset now)
    {
        var kind = source == PositionSource.Spawn ? "zone-in" : "teleport";
        return $"You — {kind}, {AgoText(measuredAt, now)}";
    }

    /// <summary>Shared "how stale" wording for the anchor labels (auto +
    /// #497 pinned). Clamped at zero so a slightly-future stamp reads
    /// "just now".</summary>
    private static string AgoText(DateTimeOffset measuredAt, DateTimeOffset now)
    {
        var age = now - measuredAt;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return age.TotalSeconds < 60
            ? "just now"
            : age.TotalMinutes < 60
                ? $"{(int)age.TotalMinutes}m ago"
                : $"{(int)age.TotalHours}h ago";
    }

    public bool ShowBearingWedges
    {
        get => _session.ShowBearingWedges;
        set => _session.ShowBearingWedges = value;
    }

    [ObservableProperty]
    private IReadOnlyList<OverlayPixel> _routePoints = Array.Empty<OverlayPixel>();

    /// <summary>
    /// Two-point polyline drawn on top of the static route line: from the
    /// most-recently-collected pin — or, before the first collection, the
    /// player's projected GPS anchor (#476) — to the current
    /// <see cref="SurveyItemViewModel.IsActiveTarget"/> pin. The GPS is sparse
    /// (zone-in / teleport only), so once the player is walking the route the
    /// last-collected pin is the better proxy for "where they are now"; the
    /// anchor only seeds the very first segment. Empty when there's no active
    /// target, or before the first collection in an uncalibrated area.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<OverlayPixel> _activeSegmentPoints = Array.Empty<OverlayPixel>();

    /// <summary>
    /// Record the player's position from a map click. #454 retired this for
    /// Survey (absolute placement needs no anchor); it survives <b>for
    /// Motherlode</b>, whose triangulation reads
    /// <see cref="SessionState.PlayerPosition"/> via
    /// <c>MotherlodeViewModel.RecordPlayerPosition</c>. No Survey FSM
    /// involvement any more.
    /// </summary>
    [RelayCommand]
    public void SetPlayerPosition(OverlayPixel where)
    {
        _session.PlayerPosition = where;
        _session.HasPlayerPosition = true;
        _projector.SetOrigin(where);
    }

    [RelayCommand]
    public void HandleMapClick(OverlayPixel where)
    {
        // Motherlode: the click records the player position for triangulation.
        if (_session.Mode == SessionMode.Motherlode)
        {
            SetPlayerPosition(where);
            return;
        }

        // Survey: placement is automatic + absolute (ProcessMapFx), so a click
        // normally does nothing. The one exception is the #476 Option C
        // manual-override detour: while SettingPosition, the click is the
        // stale-anchor correction. Record it and return to the parked phase.
        if (IsSettingPosition)
        {
            RecordManualPosition(where);
            _surveyFlow.ConfirmPosition();
        }
    }

    // The slider value is treated as pixel radius for predictable on-screen sizing —
    // multiplying by projector.Scale caused the pin to visibly shrink/grow after
    // every refit, which felt like a bug to the user.
    public double PinRadius => _settings?.SurveyPinRadiusMetres ?? 8.0;
    public double PinDiameter => PinRadius * 2;

    /// <summary>
    /// Drag/nudge a pin to a new pixel. #454: pins are absolute, so this is a
    /// purely local correction of where this one marker draws — it no longer
    /// drives a projector Refit (the relative-calibration model is retired).
    /// </summary>
    [RelayCommand]
    public void CorrectSurvey(CorrectionArgs args)
    {
        var vm = args.Survey;
        vm.UpdateModel(vm.Model with { ManualOverride = args.NewPixel });
        RebuildRouteGeometry();
    }

    [RelayCommand]
    private void OptimizeRoute()
    {
        var points = new List<OverlayPixel>();
        var indices = new List<int>();
        for (var i = 0; i < Surveys.Count; i++)
        {
            var s = Surveys[i];
            if (s.Collected || s.Skipped) continue;
            if (!s.EffectivePixel.HasValue) continue;
            points.Add(s.EffectivePixel.Value);
            indices.Add(i);
        }
        if (points.Count == 0) return;

        // #476: start the tour from the player's projected GPS when one has
        // resolved (calibrated area + a tracker fix) — "nearest node to me
        // first", parity with Motherlode. Falls back to the first uncollected
        // pin when there's no anchor (uncalibrated area / no fix yet), which
        // is the #454 behaviour. `start` is a separate origin, not a member of
        // `points`, so `order`/`indices` are unaffected by the choice.
        var start = _session.SurveyPlayerPixel ?? points[0];
        var order = _optimizer.Optimize(start, points);
        for (var i = 0; i < Surveys.Count; i++)
        {
            Surveys[i].UpdateModel(Surveys[i].Model with { RouteOrder = null });
        }
        for (var i = 0; i < order.Count; i++)
        {
            var src = indices[order[i]];
            Surveys[src].UpdateModel(Surveys[src].Model with { RouteOrder = i });
        }
        RebuildRouteGeometry();
        _surveyFlow.OptimizeRoute();
    }

    private const double WedgeHalfAngleRadians = Math.PI / 8; // 22.5 degrees

    private void RebuildAllWedges()
    {
        foreach (var s in Surveys) RebuildWedgeFor(s);
    }

    private void RebuildWedgeFor(SurveyItemViewModel s)
    {
        // Wedges only render in Motherlode mode. Survey mode's 4-DOF refit
        // (PR #130) lands pins essentially pixel-perfect, so the bearing arc
        // adds no information — the optimised route + the placed pins are
        // sufficient and precise. Motherlode triangulation has no comparable
        // refit, so the bearing arc still narrows the search there.
        if (!_session.ShowBearingWedges
            || _session.Mode != SessionMode.Motherlode
            || s.IsCorrected
            || s.Collected
            || s.Skipped
            || s.Offset.Magnitude < 1e-6)
        {
            s.WedgeArc = null;
            return;
        }

        var distancePx = s.Offset.Magnitude * _projector.Scale;
        if (distancePx < 4)
        {
            s.WedgeArc = null;
            return;
        }

        var bearingOffset = Math.Atan2(s.Offset.East, s.Offset.North);
        var bearing = bearingOffset + _projector.RotationRadians;

        // Raw inputs only; the D2D renderer constructs the arc each frame.
        s.WedgeArc = new WedgeArc(
            Origin: PlayerPosition,
            BearingRadians: bearing,
            HalfAngleRadians: WedgeHalfAngleRadians,
            DistancePx: distancePx);
    }

    private void RebuildRouteGeometry()
    {
        if (!_session.ShowRouteLines)
        {
            RoutePoints = Array.Empty<OverlayPixel>();
            ActiveSegmentPoints = Array.Empty<OverlayPixel>();
            return;
        }

        var ordered = Surveys
            .Where(s => s.RouteOrder.HasValue && s.EffectivePixel.HasValue)
            .OrderBy(s => s.RouteOrder!.Value)
            .ToList();

        // #454: no player anchor — the route is just the ordered pins.
        var points = new List<OverlayPixel>(ordered.Count);
        foreach (var s in ordered) points.Add(s.EffectivePixel!.Value);
        RoutePoints = points;

        RebuildActiveSegment();
    }

    private void RebuildActiveSegment()
    {
        if (!_session.ShowRouteLines)
        {
            ActiveSegmentPoints = Array.Empty<OverlayPixel>();
            return;
        }

        var active = Surveys.FirstOrDefault(s => s.IsActiveTarget);
        if (active is null || !active.EffectivePixel.HasValue)
        {
            ActiveSegmentPoints = Array.Empty<OverlayPixel>();
            return;
        }

        // The live segment runs from the most-recently-collected pin (best
        // available "where the player is now" proxy once they're walking the
        // route) to the active target. #476: before the first collection,
        // start from the player's projected GPS if one resolved — restores the
        // "from you → first node" guidance segment at run start. With neither
        // (uncalibrated area / no fix, nothing collected) there's no start
        // point, so just highlight the target (the #454 fallback).
        var lastCollected = Surveys
            .Where(s => s.Collected && s.RouteOrder.HasValue && s.EffectivePixel.HasValue)
            .OrderByDescending(s => s.RouteOrder!.Value)
            .FirstOrDefault();

        OverlayPixel? start = lastCollected?.EffectivePixel ?? _session.SurveyPlayerPixel;
        ActiveSegmentPoints = start is { } s0
            ? new[] { s0, active.EffectivePixel.Value }
            : Array.Empty<OverlayPixel>();
    }
}

public sealed record CorrectionArgs(SurveyItemViewModel Survey, OverlayPixel NewPixel);

/// <summary>
/// Outcome of <see cref="MapOverlayViewModel.ResolveSurveyAnchor"/> — the
/// winning Survey anchor written onto the session. <see cref="Cleared"/> is
/// the "no anchor" result (pixel null, all flags false). A <c>null</c>
/// resolution (not this) means "leave the current anchor unchanged".
/// </summary>
public readonly record struct SurveyAnchorResolution(
    OverlayPixel? Pixel,
    DateTimeOffset? MeasuredAt,
    PositionSource? Source,
    bool IsManual,
    bool IsPinned)
{
    public static readonly SurveyAnchorResolution Cleared =
        new(null, null, null, false, false);
}
