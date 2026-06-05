using Mithril.MapCalibration;

namespace Mithril.Overlay.Tests.Fakes;

/// <summary>
/// Minimal test double for <see cref="IMapCalibrationService"/>. Maps every
/// world point to a pixel via <c>(x, z)</c> (i.e. identity transform). Areas
/// whose <see cref="MapSceneRef.MapAssetKey"/> appears in
/// <see cref="CalibratedAreas"/> are calibrated; all others return null from
/// both <c>WorldToWindow</c> and <c>IsCalibrated</c>.
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

    /// <summary>Optional override: project world (x, z) -&gt; pixel for a calibrated area.
    /// Default is identity (x -&gt; X, z -&gt; Y). Returning null from the override
    /// short-circuits the per-marker projection (exercises the null-skip
    /// branch in <c>OverlayWindowService.ProjectMarkers</c>).</summary>
    public Func<MapSceneRef, WorldCoord, double, PixelPoint?>? Projector { get; set; }

    public bool IsCalibrated(MapSceneRef scene) => CalibratedAreas.Contains(scene.MapAssetKey);

    public PixelPoint? WorldToWindow(MapSceneRef scene, WorldCoord world, double currentZoom)
    {
        if (!IsCalibrated(scene)) return null;
        return Projector is { } p ? p(scene, world, currentZoom) : new PixelPoint(world.X, world.Z);
    }

    public WorldCoord? WindowToWorld(MapSceneRef scene, PixelPoint pixel, double currentZoom) => null;
    public TexturePixel? WorldToTexture(MapSceneRef scene, WorldCoord world, double currentZoom) => null;
    public WorldCoord? TextureToWorld(MapSceneRef scene, TexturePixel pixel, double currentZoom) => null;
    public OverlayPixel? WorldToOverlay(MapSceneRef scene, WorldCoord world, double currentZoom)
    {
        if (!IsCalibrated(scene)) return null;
        if (Projector is { } p)
        {
            var px = p(scene, world, currentZoom);
            return px is { } v ? new OverlayPixel(v.X, v.Y) : null;
        }
        return new OverlayPixel(world.X, world.Z);
    }
    public WorldCoord? OverlayToWorld(MapSceneRef scene, OverlayPixel pixel, double currentZoom) => null;
    public WorldToTextureCalibration? GetTextureCalibration(MapSceneRef scene) => null;
    public AreaCalibration? GetCalibration(MapSceneRef scene) => null;
    public IReadOnlyDictionary<string, AreaCalibration> AllCalibrations { get; } = new Dictionary<string, AreaCalibration>();
    public IReadOnlyList<AreaCalibration> GetAllSources(MapSceneRef scene) => Array.Empty<AreaCalibration>();
    public void SaveUserRefinement(MapSceneRef scene, AreaCalibration calibration) { }
    public void ClearUserRefinement(MapSceneRef scene) { }
    public event EventHandler<MapSceneRef>? Changed { add { } remove { } }
}
