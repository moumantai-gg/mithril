using Mithril.MapCalibration;

namespace Mithril.Overlay.Tests.Fakes;

/// <summary>
/// Minimal test double for <see cref="IMapCalibrationService"/>. Maps every
/// world point to a pixel via <c>(x, z)</c> (i.e. identity transform). Areas
/// whose <see cref="MapSceneRef.MapAssetKey"/> appears in
/// <see cref="CalibratedAreas"/> are calibrated; all others return null from
/// both <c>WorldToOverlay</c> and <c>IsCalibrated</c>.
///
/// <para>mithril#1041: typed <see cref="MapSceneRef"/> replaces the bare
/// area-key string parameter. <see cref="OverlayWindowService.DriveSceneForTest"/>
/// synthesises a scene as <c>new MapSceneRef(areaKey, null, areaKey)</c>, so
/// tests still <c>CalibratedAreas.Add("A")</c> to opt a single key in (the
/// MapAssetKey doubles as the synth area key in that path).</para>
/// </summary>
internal sealed class FakeMapCalibrationService : IMapCalibrationService
{
    public HashSet<string> CalibratedAreas { get; } = new(StringComparer.Ordinal);

    /// <summary>Optional override: project world (x, z) -&gt; overlay pixel for a calibrated area.
    /// Default is identity (x -&gt; X, z -&gt; Y). Returning null from the override
    /// short-circuits the per-marker projection (exercises the null-skip
    /// branch in <c>OverlayWindowService.ProjectMarkers</c>).</summary>
    public Func<MapSceneRef, WorldCoord, double, OverlayPixel?>? Projector { get; set; }

    /// <summary>
    /// mithril#1081: hookable per-scene overlay-cal provider. When set, overrides the
    /// default <see cref="CalibratedAreas"/>-based stub that returns a default
    /// <see cref="WorldToOverlayCalibration"/>. Use this to inject custom cals
    /// (specific Scale/Zoom combos, or null to simulate uncalibrated).
    /// </summary>
    public Func<MapSceneRef, WorldToOverlayCalibration?>? OverlayCalForScene { get; set; }

    /// <summary>
    /// mithril#1081: hookable per-scene texture-cal provider. When set, the
    /// <see cref="GetTextureCalibration"/> call returns the hook's result instead
    /// of null. Use this to simulate a texture-frame-only calibration record.
    /// </summary>
    public Func<MapSceneRef, WorldToTextureCalibration?>? TextureCalForScene { get; set; }

    public bool IsCalibrated(MapSceneRef scene) => CalibratedAreas.Contains(scene.MapAssetKey);

    public TexturePixel? WorldToTexture(MapSceneRef scene, WorldCoord world, double currentZoom) => null;
    public WorldCoord? TextureToWorld(MapSceneRef scene, TexturePixel pixel, double currentZoom) => null;
    public OverlayPixel? WorldToOverlay(MapSceneRef scene, WorldCoord world, double currentZoom)
    {
        if (!IsCalibrated(scene)) return null;
        return Projector is { } p ? p(scene, world, currentZoom) : new OverlayPixel(world.X, world.Z);
    }
    public WorldCoord? OverlayToWorld(MapSceneRef scene, OverlayPixel pixel, double currentZoom) => null;
    public WorldToTextureCalibration? GetTextureCalibration(MapSceneRef scene)
        => TextureCalForScene?.Invoke(scene);
    public WorldToOverlayCalibration? GetOverlayCalibration(MapSceneRef scene)
    {
        if (OverlayCalForScene is { } hook) return hook(scene);
        return CalibratedAreas.Contains(scene.MapAssetKey)
            ? new WorldToOverlayCalibration(
                OriginX: 0, OriginY: 0, Scale: 1.0,
                RotationRadians: 0, MirrorNorth: false)
            : null;
    }
    public AreaCalibration? GetCalibration(MapSceneRef scene) => null;
    public IReadOnlyDictionary<string, AreaCalibration> AllCalibrations { get; } = new Dictionary<string, AreaCalibration>();
    public IReadOnlyList<AreaCalibration> GetAllSources(MapSceneRef scene) => Array.Empty<AreaCalibration>();
    public void SaveUserRefinement(MapSceneRef scene, AreaCalibration calibration) { }
    public void ClearUserRefinement(MapSceneRef scene) { }
    public void DeleteUserRefinement(MapSceneRef scene, CalibrationFrame frame) =>
        throw new NotSupportedException("Test fake does not implement DeleteUserRefinement.");
    public event EventHandler<MapSceneRef>? Changed { add { } remove { } }
}
