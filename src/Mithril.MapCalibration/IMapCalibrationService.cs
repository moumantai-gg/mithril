namespace Mithril.MapCalibration;

/// <summary>
/// Shared infra for per-scene world&#8596;pixel projection. Owns the catalogue
/// of solved <see cref="AreaCalibration"/> transforms (one per Unity asset key,
/// e.g. <c>"Map_AreaSerbule"</c>) and arbitrates between three anchor sources:
///
/// <list type="number">
/// <item><b>User refinement</b> (highest precedence when residual is "good")
/// &#8212; what Legolas's calibration walkthrough produces, persisted via
/// <see cref="SaveUserRefinement"/>.</item>
/// <item><b>Community sync</b> (reserved slot, future) &#8212; aggregated
/// community-contributed transforms.</item>
/// <item><b>Bundled baseline</b> (fallback) &#8212; hand-authored anchors
/// shipped with Mithril.</item>
/// </list>
///
/// <para>A high-residual user refinement is bypassed in favour of a usable
/// baseline so a bad walkthrough doesn't displace a known-good shipped anchor.
/// See <see cref="AreaCalibration.Source"/> for the source tag carried on the
/// active transform; see <see cref="GetAllSources"/> for the debug-side view of
/// every candidate.</para>
/// </summary>
/// <remarks>
/// Every public method takes a typed <see cref="MapSceneRef"/> (mithril#1041 —
/// promotes the type from projection identifier to universal calibration
/// identity). The impl reads <see cref="MapSceneRef.MapAssetKey"/> for the
/// inner dictionary lookup; <see cref="MapSceneRef.ParentAreaKey"/> and
/// <see cref="MapSceneRef.SceneFriendlyName"/> are along for the ride. The
/// callers' typed parameter prevents the "did I pass area or asset?" footgun
/// that bare-string parameters left in place.
/// </remarks>
public interface IMapCalibrationService
{
    /// <summary>True when an anchor source has produced a transform for the scene.</summary>
    bool IsCalibrated(MapSceneRef scene);

    /// <summary>
    /// #1076 frame-explicit projection: world → base-texture-pixel. Returns
    /// null when no texture-frame calibration exists for the scene. Used by
    /// AutoCalibration / drift-check where the comparison is bound to the base
    /// texture's pixel space.
    /// </summary>
    TexturePixel? WorldToTexture(MapSceneRef scene, WorldCoord world, double currentZoom);

    /// <summary>#1076 inverse of <see cref="WorldToTexture"/>.</summary>
    WorldCoord? TextureToWorld(MapSceneRef scene, TexturePixel pixel, double currentZoom);

    /// <summary>
    /// #1076 frame-explicit projection: world → overlay-pixel. Returns null
    /// when no overlay-frame calibration exists for the scene. Used by Legolas
    /// overlay rendering.
    /// </summary>
    OverlayPixel? WorldToOverlay(MapSceneRef scene, WorldCoord world, double currentZoom);

    /// <summary>#1076 inverse of <see cref="WorldToOverlay"/>.</summary>
    WorldCoord? OverlayToWorld(MapSceneRef scene, OverlayPixel pixel, double currentZoom);

    /// <summary>
    /// #1076 raw-struct accessor for the active texture-frame calibration.
    /// Returns null when no texture-frame source has fit this scene — which is
    /// the load-bearing signal for callers like AutoCalibration's drift check
    /// to refuse honestly rather than running texture-bound arithmetic against
    /// a non-texture record (spec §2.4 / §13 P.1b).
    ///
    /// <para>Use <see cref="WorldToTexture"/> for one-shot projection; this
    /// method is for callers that need the existence answer ("does the scene
    /// have a usable texture-frame record?") and / or the struct itself for
    /// downstream composition.</para>
    /// </summary>
    WorldToTextureCalibration? GetTextureCalibration(MapSceneRef scene);

    /// <summary>
    /// #1076 raw-struct accessor for the active overlay-frame calibration.
    /// Returns null when no overlay-frame source has fit this scene — the
    /// symmetric counterpart to <see cref="GetTextureCalibration"/> for
    /// overlay-bound callers (Legolas rendering / wizard ghosts). Use
    /// <see cref="WorldToOverlay"/> for one-shot projection; this method is
    /// for callers that need the existence answer ("does the scene have a
    /// usable overlay-frame record?") and / or the struct itself for
    /// downstream composition.
    /// </summary>
    WorldToOverlayCalibration? GetOverlayCalibration(MapSceneRef scene);

    /// <summary>
    /// The active calibration record for a scene (or null if uncalibrated).
    /// Consumers needing the residual + reference count for an "approximate
    /// location" chip read it here.
    /// </summary>
    AreaCalibration? GetCalibration(MapSceneRef scene);

    /// <summary>
    /// All currently-active calibrations, keyed by <see cref="MapSceneRef.MapAssetKey"/>
    /// (the persistence horizon — the store knows only the asset key; for parent-area
    /// resolution use <see cref="ISceneAssetCache"/>). Reflects the stacked-source
    /// decision: each value is the source that won for its scene. Lets a debug
    /// surface (Palantir) audit the stacking outcome.
    /// </summary>
    IReadOnlyDictionary<string, AreaCalibration> AllCalibrations { get; }

    /// <summary>
    /// Every candidate calibration for a scene, regardless of which one won.
    /// Each record carries its own <see cref="AreaCalibration.Source"/> and
    /// <see cref="AreaCalibration.ResidualPixels"/>. Used by debug surfaces
    /// that want to compare e.g. "baseline author fit it to 3.2 px on their
    /// install" vs "you fit it to 7.8 px on yours". Empty when no source has
    /// supplied a transform for the scene.
    /// </summary>
    IReadOnlyList<AreaCalibration> GetAllSources(MapSceneRef scene);

    /// <summary>
    /// Apply a per-user refinement (what Legolas's <c>PinCalibrationCoordinator</c>
    /// produces at the end of the Drop/Pair walkthrough — and what
    /// <c>AutoCalibrationEngine</c> persists on auto-solve). Persists; raises
    /// <see cref="Changed"/>; flows into the stacked transform per the
    /// precedence rules in <see cref="IMapCalibrationService"/>'s remarks.
    /// </summary>
    void SaveUserRefinement(MapSceneRef scene, AreaCalibration calibration);

    /// <summary>Drop a per-user refinement for a scene (revert to baseline / community).</summary>
    void ClearUserRefinement(MapSceneRef scene);

    /// <summary>
    /// Raised when the active transform changes for any scene. Payload = the
    /// changed scene (composite, not just the asset key — the writer has the
    /// full identity in hand at the raise site).
    ///
    /// <para><b>Threading contract:</b> delivered <em>synchronously on the
    /// thread that performed the write</em>. The writer may be any thread
    /// (wizard on the UI dispatcher, hosted services on the ThreadPool,
    /// community-sync background fetcher, etc.). UI subscribers that touch
    /// WPF state from the handler MUST marshal back onto the dispatcher
    /// themselves; this service does not own a dispatcher.</para>
    /// </summary>
    event EventHandler<MapSceneRef>? Changed;
}
