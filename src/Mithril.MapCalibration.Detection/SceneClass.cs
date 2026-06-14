namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Classification of a PG map scene by its base-texture alpha-coverage shape
/// (mithril#1155 spec §5.2 — the post-spike Phase-1 scaffolding for
/// <see cref="SceneCalibrationProfile"/> selection).
///
/// <para>The axis the engine has been quietly leaning on <c>null</c> for:
/// outdoor maps render against a fully-opaque base texture
/// (<c>opaqueFraction ≥ 0.95</c>); indoor maps have alpha=0 over the off-map
/// regions, which produces a structurally different deviation field at the
/// detector layer. Knobs that tune cleanly outdoors (the gate-study
/// sweet-spot per mithril#897) admit floor-texture noise as Icon-class blobs
/// indoors; the Indoor profile relaxes the classifier shape gates per the
/// [`indoor-recall-merge-fix-candidates.md`](../../../docs/planning/calibration-1155-scene-class-profile/measurements/indoor-recall-merge-fix-candidates.md)
/// measurement.</para>
///
/// <para>Resolution source: <c>FloorBoundaryMaskCache.GetSceneClass</c>
/// computes <c>opaqueFraction = count(alpha &gt;= 128) / (textureWidth *
/// textureHeight)</c> from the same alpha buffer the boundary mask already
/// loads — no extra IO. Threshold comes from
/// <see cref="MapCalibrationDetectorOptions.SceneClassOpaqueFractionThreshold"/>
/// (default 0.95). Fail-soft: when alpha is unavailable, <see cref="Outdoor"/>
/// is the safe default (preserves pre-#1163 behaviour byte-identically).</para>
/// </summary>
public enum SceneClass
{
    /// <summary>
    /// Fully-opaque base texture (alpha ≥ 128 over ≥ 95 % of the texture).
    /// Default for any scene whose alpha can't be classified — the Outdoor
    /// profile carries today's universal constants, so safe-degrade is
    /// byte-identical to pre-#1163.
    /// </summary>
    Outdoor = 0,

    /// <summary>
    /// Substantially-transparent base texture (alpha &lt; 128 over &gt; 5 %
    /// of the texture). Indoor scenes (Hogan's Keep Basement, GoblinDungeon,
    /// etc.) render against alpha=0 off-map regions, so the deviation field
    /// has structurally different connectivity than Outdoor's grass-vs-icon
    /// signal. The Indoor profile relaxes <c>MaxAspect</c> and
    /// <c>MinSolidity</c> per the Phase-2 measurement.
    /// </summary>
    Indoor = 1,
}
