using Legolas.Domain;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;
using Mithril.Shared.Diagnostics.Telemetry;
using Mithril.Shared.Reference;
using Mithril.Shared.Settings;

namespace Legolas.Services;

public interface IAreaCalibrationService
{
    /// <summary>Composite scene identity for the current calibration scope
    /// (parent area + sub-zone friendly name + Unity asset key), or null if
    /// no scene is active yet. mithril#1041 — replaces the prior
    /// <c>CurrentAreaKey</c> string with the typed composite the rest of the
    /// calibration stack now consumes.</summary>
    MapSceneRef? CurrentScene { get; }

    /// <summary>Friendly name of the current area (as seen in the chat banner), or null.</summary>
    string? CurrentAreaFriendlyName { get; }

    /// <summary>True when the current scene has a persisted <see cref="AreaCalibration"/> applied.</summary>
    bool IsCurrentAreaCalibrated { get; }

    /// <summary>Persisted calibration for the current scene, if any.</summary>
    AreaCalibration? CurrentCalibration { get; }

    /// <summary>
    /// #1076 frame-typed view of the current scene's overlay-frame calibration.
    /// Returns null when no overlay-frame record exists (uncalibrated, or only
    /// a texture-frame record is present and Legolas can't yet compose it onto
    /// the overlay — see spec §6 "AutoCal release blocker"). Consumers project
    /// world coords directly into <see cref="OverlayPixel"/> through this
    /// struct rather than going through a frame-erased projection (the #1076
    /// migration retired the untyped entry point).
    /// </summary>
    WorldToOverlayCalibration? CurrentOverlayCalibration { get; }

    /// <summary>
    /// Landmark + NPC reference points in the current area, with parseable
    /// positions, ordered NPCs-then-landmarks (NPCs are the dense recognizable
    /// set). Empty when no current area or no reference data loaded.
    /// </summary>
    IReadOnlyList<CalibrationReference> CurrentAreaReferences { get; }

    /// <summary>
    /// Every known area (from reference data), sorted by friendly name — the
    /// source for the manual area picker. Lets the user calibrate without
    /// waiting for a live <c>Entering Area:</c> banner (e.g. Mithril was started
    /// after they were already in the area).
    /// </summary>
    IReadOnlyList<AreaEntry> AllAreas { get; }

    /// <summary>Raised (CurrentScene changed or calibration (re)applied) so UI can refresh.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Set the current scene by typed composite. The Arda-driven
    /// <c>PlayerLogIngestionService</c> calls this when it receives a
    /// <c>MapAssetChanged</c> domain event (mithril#1041 — per-scene
    /// granularity is strictly-more-informative than the prior
    /// <c>AreaChanged</c> path for aggregator areas). Also used by the
    /// manual area-picker UI via <c>AreaCalibrationService.MapSceneRefForDirectlyRegisteredArea</c>.
    /// </summary>
    void SelectScene(MapSceneRef scene);

    /// <summary>
    /// Solve a calibration from user-placed reference clicks (a world point
    /// paired with the pixel the user clicked it at) for the current area,
    /// persist it, and apply the resulting transform via the shared
    /// <see cref="IMapCalibrationService"/>. Returns the solver output
    /// verbatim (the calibration the user just produced from their clicks),
    /// or null if it couldn't be solved (no current area, &lt;2 non-degenerate
    /// references).
    ///
    /// <para><b>Return-value contract:</b> the returned record is what the
    /// SOLVER produced &#8212; it reflects how good the user's clicks fit,
    /// not what's effectively rendered on the map. When stacking precedence
    /// kicks in (e.g. the user's residual exceeds the "good" threshold and a
    /// usable bundled baseline exists), <see cref="IMapCalibrationService.GetCalibration"/>
    /// returns the baseline as the effective transform while this method
    /// still returns the user's bad solve. The wizard relies on this so it
    /// can surface "residual high &#8212; redo for a tighter fit" instead of
    /// silently swapping in a different number. Callers that need the
    /// effective transform &#8212; not the solve quality &#8212; query the
    /// shared service.</para>
    /// </summary>
    AreaCalibration? CalibrateCurrentArea(
        IReadOnlyList<(WorldCoord World, OverlayPixel Pixel)> placements,
        double calibrationZoom = 1.0);

    /// <summary>Drop the current area's persisted calibration (forces a recalibrate).</summary>
    void ClearCurrentAreaCalibration();

    /// <summary>
    /// Fed every survey/treasure reading by the log pipeline. Re-raised as
    /// <see cref="SurveyObserved"/> so the calibration window's test mode can
    /// project it and show projected-vs-actual. A no-op for everyone else.
    /// </summary>
    void NoteSurvey(string name, MetreOffset offset);

    /// <summary>Raised for each <see cref="NoteSurvey"/> — the test-mode hook.</summary>
    event EventHandler<CalibrationSurveyObservation>? SurveyObserved;

    // Pin ingestion is no longer Legolas-owned: the map-pin lifecycle is
    // handled by the Arda pipeline (MapPinAdded/MapPinRemoved domain events),
    // which calibration consumers subscribe to directly. The old
    // NotePinAdded/PinAdded relay (#454) was removed with that promotion.
}

/// <summary>
/// Owns the per-area calibration lifecycle: scene-key handoff (from the
/// Arda <c>MapAssetChanged</c> domain event bridge in
/// <c>PlayerLogIngestionService</c> or the manual area-picker UI) &#8594;
/// apply persisted <see cref="AreaCalibration"/> on entry, and the
/// solve/persist path the calibration window drives. Reference points
/// come from <see cref="IReferenceDataService"/> (landmarks + NPCs with a
/// parseable <c>Pos</c>), which is the same engine-unit world frame the game
/// positions the player in (verified 2026-05-18).
///
/// <para>The chat-log <c>Entering Area:</c> banner path was retired in #605 —
/// per #531, Arda's <c>IAreaState</c> already exposes the same signal
/// authoritatively from Player.log's <c>LOADING LEVEL</c> line.</para>
///
/// <para>mithril#1041: per-scene migration. <c>CurrentScene</c> is the
/// typed <see cref="MapSceneRef"/>; the legacy <c>LegolasSettings.AreaCalibrations</c>
/// dual-write/clear is retired (D6) — every solved calibration lands in the
/// shared <see cref="IMapCalibrationService"/> alone. The settings field itself
/// stays <c>[Obsolete]</c> for one release cycle so existing on-disk data is
/// preserved across the upgrade.</para>
/// </summary>
public sealed class AreaCalibrationService : IAreaCalibrationService
{
    private readonly IReferenceDataService _refData;
    private readonly ICoordinateProjector _projector;
    private readonly IMapCalibrationService _mapCal;
    private readonly ILogger? _logger;

    private IReadOnlyList<CalibrationReference> _currentRefs = Array.Empty<CalibrationReference>();

    public AreaCalibrationService(
        IReferenceDataService refData,
        ICoordinateProjector projector,
        IMapCalibrationService mapCal,
        ILogger? logger = null)
    {
        _refData = refData;
        _projector = projector;
        _mapCal = mapCal;
        _logger = logger;

        // Re-apply the projector when the active calibration changes from a
        // source we don't own (e.g. a community-sync update lands for the
        // current area). Stacked-source precedence is honoured by GetCalibration.
        _mapCal.Changed += OnMapCalChanged;
    }

    public MapSceneRef? CurrentScene { get; private set; }
    public string? CurrentAreaFriendlyName { get; private set; }

    public bool IsCurrentAreaCalibrated =>
        CurrentScene is { } scene && _mapCal.IsCalibrated(scene);

    public AreaCalibration? CurrentCalibration =>
        CurrentScene is { } scene ? _mapCal.GetCalibration(scene) : null;

    public WorldToOverlayCalibration? CurrentOverlayCalibration =>
        CurrentScene is { } scene ? _mapCal.GetOverlayCalibration(scene) : null;

    public IReadOnlyList<CalibrationReference> CurrentAreaReferences => _currentRefs;

    private IReadOnlyList<AreaEntry>? _allAreas;
    public IReadOnlyList<AreaEntry> AllAreas =>
        _allAreas ??= _refData.Areas.Values
            .OrderBy(a => a.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public event EventHandler? Changed;

    public void SelectScene(MapSceneRef scene)
    {
        if (string.IsNullOrWhiteSpace(scene.ParentAreaKey)) return;

        using var act = MithrilActivitySources.LegolasCalibration.StartActivity("calibration.area.select_scene");
        act?.SetTag("scene.asset_key", scene.MapAssetKey);
        act?.SetTag("scene.parent_area_key", scene.ParentAreaKey);

        CurrentScene = scene;
        CurrentAreaFriendlyName = _refData.Areas.TryGetValue(scene.ParentAreaKey, out var entry)
            ? entry.FriendlyName
            : scene.ParentAreaKey;
        _currentRefs = BuildReferences(scene.ParentAreaKey);

        act?.SetTag("refs_count", _currentRefs.Count);

        var calibration = _mapCal.GetCalibration(scene);
        if (calibration is { } cal)
        {
            _projector.ApplyCalibration(cal);
            act?.SetTag("cal.applied", true);
            act?.SetTag("cal.source", cal.Source.ToString());
            act?.SetTag("cal.residual_px", cal.ResidualPixels);
            _logger?.LogInformation(
                "SelectScene → {MapAssetKey} (parent={ParentArea}, friendly={SceneFriendlyName}): {RefCount} refs, cal {CalState} (source={Source}, residual={Residual:0.00}px, frame={Frame}).",
                scene.MapAssetKey, scene.ParentAreaKey, CurrentAreaFriendlyName, _currentRefs.Count,
                "applied", cal.Source, cal.ResidualPixels, cal.Frame);
        }
        else
        {
            act?.SetTag("cal.applied", false);
            _logger?.LogInformation(
                "SelectScene → {MapAssetKey} (parent={ParentArea}, friendly={SceneFriendlyName}): {RefCount} refs, cal {CalState}.",
                scene.MapAssetKey, scene.ParentAreaKey, CurrentAreaFriendlyName, _currentRefs.Count,
                "none");
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Bridge for the manual area-picker (areas.json-shaped): synthesize a
    /// <see cref="MapSceneRef"/> for a directly-registered area. The asset key
    /// follows the <c>Map_</c> + areas.json-key convention. Returns the bare
    /// area key as MapAssetKey for unrecognised areas (no calibration will be
    /// found, but the picker still gives feedback).
    ///
    /// <para>This is a one-line bridge: the picker is areas.json-shaped, and
    /// the directly-registered area set (#1041 spec §5.6) covers the 12 areas
    /// the picker offers. Aggregator sub-zones (Hogan's Basement etc.) are
    /// out-of-scope for the picker — they consume the SceneAssetCache via the
    /// follow-up wizard sub-zone picker (D8).</para>
    /// </summary>
    public static MapSceneRef MapSceneRefForDirectlyRegisteredArea(string areaKey) =>
        new(ParentAreaKey: areaKey, SceneFriendlyName: null, MapAssetKey: "Map_" + areaKey);

    private void OnMapCalChanged(object? sender, MapSceneRef payload)
    {
        // #1041 fix: compare by MapAssetKey (asset-key the store keys on), not
        // by ParentAreaKey. The pre-#1041 path compared the engine-emitted
        // Map_<X> against the bare CurrentAreaKey and dropped every event.
        if (CurrentScene is not { } current) return;
        if (!string.Equals(payload.MapAssetKey, current.MapAssetKey, StringComparison.Ordinal))
        {
            _logger?.LogTrace(
                "OnMapCalChanged({PayloadKey}): dropped, current scene is {CurrentKey}.",
                payload.MapAssetKey, current.MapAssetKey);
            return;
        }
        if (_mapCal.GetCalibration(current) is { } calibration)
        {
            _projector.ApplyCalibration(calibration);
            _logger?.LogTrace(
                "OnMapCalChanged({MapAssetKey}): re-applied cal (source={Source}, residual={Residual:0.00}px, frame={Frame}).",
                current.MapAssetKey, calibration.Source, calibration.ResidualPixels, calibration.Frame);
        }
        else
        {
            _logger?.LogTrace(
                "OnMapCalChanged({MapAssetKey}): matched but no cal returned (cleared?).",
                current.MapAssetKey);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public AreaCalibration? CalibrateCurrentArea(
        IReadOnlyList<(WorldCoord World, OverlayPixel Pixel)> placements,
        double calibrationZoom = 1.0)
    {
        using var act = MithrilActivitySources.LegolasCalibration.StartActivity("calibration.area.calibrate_current");
        var placementCount = placements?.Count ?? 0;
        act?.SetTag("placements", placementCount);

        if (CurrentScene is not { } scene || placements is null || placements.Count < 2)
        {
            act?.SetTag("outcome", "refused");
            _logger?.LogInformation(
                "CalibrateCurrentArea: refused — no current scene or <2 placements ({PlacementCount} given).",
                placementCount);
            return null;
        }

        act?.SetTag("scene.asset_key", scene.MapAssetKey);

        var refs = placements
            .Select(p => new LandmarkCalibrationSolver.Reference(p.World.X, p.World.Z, p.Pixel.X, p.Pixel.Y))
            .ToList();

        var solved = LandmarkCalibrationSolver.Solve(refs);
        if (solved is null)
        {
            act?.SetTag("outcome", "no_fit");
            _logger?.LogWarning(
                "CalibrateCurrentArea({MapAssetKey}): solver returned no fit for {PlacementCount} placements.",
                scene.MapAssetKey, placementCount);
            return null;
        }
        // Stamp the zoom the user solved at (solver is zoom-agnostic — it just
        // fits the clicked pixels). > 0 guard so a bad value can't poison the
        // later currentZoom/CalibrationZoom division.
        //
        // mithril#1078: explicit Frame=Overlay. The wizard fits (WorldCoord, OverlayPixel)
        // pairs the user clicks on the overlay window; the resulting OriginX/Scale/etc.
        // are in overlay-pixel units. Without this stamp, AreaCalibration.Frame would
        // take its default of Texture and the picker would mis-frame this record. The
        // defensive Source stamp in UserRefinementStore.Save preserves Frame verbatim.
        var calibration = solved with
        {
            CalibrationZoom = calibrationZoom > 1e-6 ? calibrationZoom : 1.0,
            Frame = CalibrationFrame.Overlay,
        };

        // mithril#1041: single write path — every solved calibration (manual
        // wizard + auto-capture) lands in the shared IMapCalibrationService.
        // The legacy LegolasSettings.AreaCalibrations dual-write was retired
        // (D6) — the model justification for treating manual fits as special
        // was ruled out (legolas_calibration_findings).
        _mapCal.SaveUserRefinement(scene, calibration);

        act?.SetTag("outcome", "solved");
        act?.SetTag("cal.residual_px", calibration.ResidualPixels);
        _logger?.LogInformation(
            "CalibrateCurrentArea({MapAssetKey}): solved {PlacementCount} placements at zoom={Zoom}; residual={Residual:0.00}px frame=Overlay refs={Refs}.",
            scene.MapAssetKey, placementCount, calibration.CalibrationZoom,
            calibration.ResidualPixels, calibration.ReferenceCount);

        // SaveUserRefinement raises IMapCalibrationService.Changed; our
        // OnMapCalChanged handler reads GetCalibration (which respects stacking
        // precedence) and applies whichever calibration won to the projector.
        // The return value here is the SOLVER OUTPUT verbatim — what the user
        // just produced from their clicks — not the effective transform. The
        // wizard surfaces it as "how good was your solve" (residual + scale
        // for the redo/proceed gate), and consumers that need "what's actually
        // rendered" read IMapCalibrationService.GetCalibration directly. These
        // are two different questions: a high-residual user solve with a
        // usable baseline present returns the user's bad fit (so the wizard
        // says "redo for a tighter fit") while the map renders the baseline
        // (correct rendering — the user's solve lost precedence). See the
        // IAreaCalibrationService.CalibrateCurrentArea contract below.
        return calibration;
    }

    public event EventHandler<CalibrationSurveyObservation>? SurveyObserved;

    public void NoteSurvey(string name, MetreOffset offset) =>
        SurveyObserved?.Invoke(this, new CalibrationSurveyObservation(name, offset));

    public void ClearCurrentAreaCalibration()
    {
        if (CurrentScene is not { } scene) return;
        // mithril#1041: single clear path — every retire lands in the shared
        // IMapCalibrationService. The legacy LegolasSettings.AreaCalibrations
        // dual-clear was retired (D6).
        //
        // ClearUserRefinement raises mapCal.Changed → OnMapCalChanged
        // re-broadcasts our Changed; do not raise Changed directly to avoid
        // double-delivery.
        _logger?.LogInformation(
            "ClearCurrentAreaCalibration({MapAssetKey}): user requested clear; re-broadcast via mapCal.Changed.",
            scene.MapAssetKey);
        _mapCal.ClearUserRefinement(scene);
    }

    private IReadOnlyList<CalibrationReference> BuildReferences(string areaKey)
    {
        var list = new List<CalibrationReference>();

        // NPCs first — dense, named, labelled on the in-game map.
        foreach (var npc in _refData.NpcsByInternalName.Values)
        {
            if (!string.Equals(npc.AreaName, areaKey, StringComparison.Ordinal)) continue;
            if (WorldCoord.TryParse(npc.Pos) is not { } w) continue;
            list.Add(new CalibrationReference(npc.Name ?? "(unnamed NPC)", "NPC", w));
        }

        // Landmarks — sparse same-format supplement.
        if (_refData.Landmarks.TryGetValue(areaKey, out var landmarks))
        {
            foreach (var lm in landmarks)
            {
                if (WorldCoord.TryParse(lm.Loc) is not { } w) continue;
                var kind = string.IsNullOrEmpty(lm.Type) ? "Landmark" : lm.Type!;
                list.Add(new CalibrationReference(lm.Name ?? "(unnamed landmark)", kind, w));
            }
        }

        return list;
    }
}
