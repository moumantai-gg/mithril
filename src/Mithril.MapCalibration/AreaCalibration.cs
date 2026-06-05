namespace Mithril.MapCalibration;

/// <summary>
/// A solved, persistable similarity transform for one Project&#160;Gorgon area:
/// the projector <see cref="Scale"/> (pixels per metre), <see cref="RotationRadians"/>,
/// and pixel <see cref="OriginX"/>/<see cref="OriginY"/> that map a world coord
/// to the 1:1 map overlay. Derived once from &#8805;2 known landmark/NPC reference
/// clicks (see <see cref="LandmarkCalibrationSolver"/>) and reused across
/// sessions because landmarks/NPCs don't move &#8212; keyed by area in
/// <see cref="IMapCalibrationService"/>.
///
/// <para><see cref="ResidualPixels"/> is the RMS pixel error of the fit across
/// the reference points; a large value means the references were placed
/// inconsistently (or the map was at a different zoom) and the calibration
/// should be redone, OR the consumer should render the projection with an
/// "approximate location" affordance. The non-affine ceiling in PG's map
/// renderer is documented at
/// <see href="https://github.com/moumantai-gg/mithril/wiki/Legolas-Calibration-Findings">
/// Legolas-Calibration-Findings</see>.</para>
/// </summary>
public sealed record AreaCalibration(
    double Scale,
    double RotationRadians,
    double OriginX,
    double OriginY,
    int ReferenceCount,
    double ResidualPixels)
{
    /// <summary>
    /// Which world-axis&#8594;compass handedness the solver chose: when true,
    /// world North = &#8722;Z (a reflection of the +Z convention). A similarity
    /// transform cannot absorb a reflection, so this MUST be carried to
    /// re-project raw world coords. Default false (the +Z convention).
    /// </summary>
    public bool MirrorNorth { get; init; }

    /// <summary>
    /// The in-game map zoom the user was at when this was solved (read off the
    /// game UI &#8212; Mithril can't see it). <see cref="Scale"/> is px-per-unit
    /// at THIS zoom; pixels-per-metre scales linearly with zoom, so a projection
    /// at a different current zoom must be scaled by
    /// <c>currentZoom / CalibrationZoom</c>. Default <c>1.0</c>.
    /// </summary>
    public double CalibrationZoom { get; init; } = 1.0;

    /// <summary>
    /// Where this transform was sourced from (<see cref="CalibrationSource.BundledBaseline"/>
    /// / <see cref="CalibrationSource.CommunitySync"/> /
    /// <see cref="CalibrationSource.UserRefinement"/>). Defaults to
    /// <see cref="CalibrationSource.UserRefinement"/> so lifted records produced
    /// by <see cref="LandmarkCalibrationSolver.Solve"/> (always a user-driven
    /// solve) round-trip correctly without explicit assignment.
    /// </summary>
    public CalibrationSource Source { get; init; } = CalibrationSource.UserRefinement;

    /// <summary>
    /// Schema version for this persisted record. Bump alongside any shape
    /// change. Default 1.
    /// </summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Which pixel frame the projection outputs into &#8212;
    /// <see cref="CalibrationFrame.Texture"/> for AutoCalibration/RANSAC fits
    /// (and bundled-baseline anchors), <see cref="CalibrationFrame.Overlay"/>
    /// for Legolas-walkthrough fits. Persisted on Schema 2+ records
    /// (mithril#1076); Schema-1 records infer this at load time from
    /// <see cref="Source"/> per the table in
    /// <c>docs/planning/calibration-1076-pixel-frame-typing/spec.md</c> §7.2.
    /// Defaults to <see cref="CalibrationFrame.Texture"/> for new in-memory
    /// constructions (the safer default for compute paths that hand records to
    /// AutoCal's drift-check); fresh writes always set this explicitly.
    ///
    /// <para>Annotated <see cref="JsonIgnoreCondition.Never"/> so the context-wide
    /// <see cref="JsonIgnoreCondition.WhenWritingDefault"/> rule does NOT drop
    /// the value when it equals the type default (<see cref="CalibrationFrame.Texture"/>).
    /// The spec §7.1 JSON shape requires <c>frame</c> on every Schema-2 write so
    /// a load-time consumer can distinguish a Schema-2 default-Texture record
    /// from a Schema-1 record that needs Source-based inference.</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public CalibrationFrame Frame { get; init; } = CalibrationFrame.Texture;

    /// <summary>
    /// Absolute world&#8594;overlay-pixel projection. Maps a raw area-local
    /// world coordinate to a pixel on the 1:1 map overlay using the full solved
    /// transform (origin + scale + rotation + the <see cref="MirrorNorth"/>
    /// handedness reflection). The parameterless overload uses <see cref="Scale"/>
    /// verbatim and is therefore exact only at <see cref="CalibrationZoom"/>;
    /// prefer the <see cref="WorldToWindow(WorldCoord, double)"/> overload from
    /// any call site that knows the live in-game zoom.
    /// </summary>
    public PixelPoint WorldToWindow(WorldCoord world) =>
        WorldToWindow(world, CalibrationZoom);

    /// <summary>
    /// Zoom-aware absolute world&#8594;overlay-pixel projection. Scales
    /// <see cref="Scale"/> by <c>currentZoom / CalibrationZoom</c>; the origin
    /// is zoom-invariant under the no-pan assumption (the pixel that renders
    /// world (0,0,0) depends on pan, not on zoom), so only the metric scale
    /// changes when the user adjusts PG's map zoom between calibrate and use.
    ///
    /// <para>Defensive: a non-positive <paramref name="currentZoom"/> or
    /// <see cref="CalibrationZoom"/> falls back to a factor of <c>1.0</c>
    /// (uses <see cref="Scale"/> verbatim) so a malformed value can't crash
    /// the per-frame projector.</para>
    /// </summary>
    public PixelPoint WorldToWindow(WorldCoord world, double currentZoom)
    {
        var effScale = Scale * ZoomFactor(currentZoom);
        var east = world.X;
        var north = MirrorNorth ? -world.Z : world.Z;
        var cos = Math.Cos(RotationRadians);
        var sin = Math.Sin(RotationRadians);
        var rotE = east * cos + north * sin;
        var rotN = -east * sin + north * cos;
        return new PixelPoint(OriginX + effScale * rotE, OriginY - effScale * rotN);
    }

    /// <summary>
    /// Inverse of <see cref="WorldToWindow(WorldCoord, double)"/>: maps a pixel
    /// on the overlay back to a world coordinate. Required by Gwaihir's
    /// "click the map to drop a pin" UX (#830 §3a) and by any other consumer
    /// that authors a POI at click-time and stores it as world coords.
    ///
    /// <para>The world-Y elevation cannot be recovered from a 2D pixel; the
    /// returned <see cref="WorldCoord.Y"/> is always zero. Map projection
    /// already ignores Y, so callers that round-trip
    /// <c>WindowToWorld(WorldToWindow(p))</c> get back the same projected
    /// pixel regardless.</para>
    ///
    /// <para>Returns null only when <see cref="Scale"/> is degenerate (
    /// <c>&#8804; 0</c>), which a valid solver output never produces.</para>
    /// </summary>
    public WorldCoord? WindowToWorld(PixelPoint pixel, double currentZoom)
    {
        var effScale = Scale * ZoomFactor(currentZoom);
        if (effScale <= 1e-9) return null;

        // Invert: rotE = (px - originX) / effScale; rotN = -(py - originY) / effScale.
        var rotE = (pixel.X - OriginX) / effScale;
        var rotN = -(pixel.Y - OriginY) / effScale;

        // Inverse rotation: east = rotE*cos - rotN*sin; north = rotE*sin + rotN*cos.
        var cos = Math.Cos(RotationRadians);
        var sin = Math.Sin(RotationRadians);
        var east = rotE * cos - rotN * sin;
        var north = rotE * sin + rotN * cos;

        var worldX = east;
        var worldZ = MirrorNorth ? -north : north;
        return new WorldCoord(worldX, 0, worldZ);
    }

    /// <summary>
    /// Parameterless inverse (uses <see cref="CalibrationZoom"/>). Sibling of
    /// <see cref="WorldToWindow(WorldCoord)"/>.
    /// </summary>
    public WorldCoord? WindowToWorld(PixelPoint pixel) =>
        WindowToWorld(pixel, CalibrationZoom);

    private double ZoomFactor(double currentZoom) =>
        (currentZoom > 1e-6 && CalibrationZoom > 1e-6)
            ? currentZoom / CalibrationZoom
            : 1.0;
}
