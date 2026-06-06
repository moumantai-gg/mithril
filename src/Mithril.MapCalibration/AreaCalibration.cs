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
    /// change. Default 3 (skip 2 to mark the no-CalibrationZoom invariant
    /// unambiguously; Schema 2 was never shipped).
    /// </summary>
    public int SchemaVersion { get; init; } = 3;

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
    /// SHA-256 (lowercase hex) of the base texture this calibration was solved
    /// against — same digest the sidecar's MapTextureManifest carries and the
    /// CanonicalAssetHashGate checks. Stamped at AutoCal-solve time
    /// (mithril#1081) and on bundled-baseline rows at commit time. Identifies
    /// WHICH texture the math is bound to; the overlay derives the texture's
    /// pixel dimensions by looking this up via
    /// <see cref="IMapTextureDimensions"/>. Null on records persisted before
    /// #1081 → unrenderable on the overlay (drift-check unaffected — it doesn't
    /// need dims). Overlay-frame records leave this null; they don't compose
    /// against a texture.
    /// </summary>
    public string? PixelSha256 { get; init; }
}
