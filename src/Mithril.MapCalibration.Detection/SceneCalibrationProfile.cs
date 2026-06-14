namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Per-scene-class detection-parameter bundle (mithril#1155 spec §5.1 — the
/// post-spike Phase-1 scaffolding for Indoor / Outdoor divergence).
///
/// <para>For v1, only <see cref="BlobOptions"/> diverges between profiles:
/// the
/// [`indoor-recall-stage-attribution.md`](../../../docs/planning/calibration-1155-scene-class-profile/measurements/indoor-recall-stage-attribution.md)
/// audit identified the classifier shape gates (<c>MaxAspect</c>,
/// <c>MinSolidity</c>) as the load-bearing reason real-icon blobs fail
/// classification indoors; the
/// [`indoor-recall-merge-fix-candidates.md`](../../../docs/planning/calibration-1155-scene-class-profile/measurements/indoor-recall-merge-fix-candidates.md)
/// measurement showed that <c>LowNcc</c> / <c>RenderSizePx</c> / morph
/// <c>closeRadius</c> / deviation kernel <c>win</c> do NOT need to diverge for
/// the v1 recovery (the audit's T3 hypothesis was partially falsified). Future
/// phases (per the plan's Phase 4 / Phase 5) extend this record with extra
/// fields as those divergences land.</para>
///
/// <para><b>Outdoor profile</b> is today's universal constants verbatim — the
/// gate-study sweet-spot from mithril#897 (<c>MaxAspect = 2.5</c>,
/// <c>MinSolidity = 0.35</c>, etc.). Any scene whose alpha can't be classified
/// gets Outdoor by fail-soft default, so the change is byte-identical to
/// pre-#1163 behaviour for Outdoor captures.</para>
///
/// <para><b>Indoor profile</b> relaxes <c>MaxAspect 2.5 → 2.7</c> (recovers
/// IconE — observed aspect 2.56 in two distinct Hogan's bundles, 0.06 above
/// the production ceiling, structurally repeating across captures) and
/// <c>MinSolidity 0.35 → 0.30</c> (recovers IconD — observed solidity 0.31 in
/// the canonical 06-13 bundle). Combined recovery: +2 real-icon blobs reach
/// Icon class on the canonical bundle (total 3/6). Falls short of the audit's
/// "≥ 4 RANSAC floor" by one — the remaining gap is the B+C merge problem,
/// which the
/// [`indoor-recall-merge-fix-candidates.md`](../../../docs/planning/calibration-1155-scene-class-profile/measurements/indoor-recall-merge-fix-candidates.md)
/// measurement showed isn't reachable via (win, closeRadius) tuning. Phase 2.5
/// (morph-open) is the candidate follow-up if v1's 3/6 isn't enough.</para>
///
/// <para><b>Carrier shape rationale.</b> The record is intentionally minimal
/// (one field) to keep v1 PR review tractable. Spec §5.1 / plan §Phase 1 list
/// additional fields (<c>RenderSizePx</c>, <c>LowNcc</c>, <c>TypeFloor</c>,
/// <c>RansacInlierPx</c>, synthesis-J mode + formulas) — those land alongside
/// their actual divergence in the corresponding phase, NOT speculatively here
/// (per the project memory's "no speculative guards" pattern: fields that
/// don't move v1 behaviour shouldn't appear in the v1 carrier).</para>
/// </summary>
/// <param name="SceneClass">
/// Which scene class this profile targets. Used at the dispatcher layer
/// (AutoCalibrationEngine resolves the SceneClass per attempt, picks the
/// matching profile) — kept as a discriminator field so a future registry
/// pattern (per spec §6.b — if a third class arises) doesn't need a struct
/// shape change.
/// </param>
/// <param name="BlobOptions">
/// Classifier shape thresholds passed into
/// <see cref="DeviationBlobDetector.DetectIconBlobs"/>. Outdoor carries the
/// gate-study sweet-spot; Indoor relaxes <c>MaxAspect</c> and
/// <c>MinSolidity</c>.
/// </param>
public readonly record struct SceneCalibrationProfile(
    SceneClass SceneClass,
    BlobOptions BlobOptions)
{
    /// <summary>
    /// Outdoor profile — today's universal constants verbatim. The
    /// <see cref="BlobOptions"/> values match the gate-study sweet-spot quoted
    /// in <c>AutoCalibrationEngine.cs:61-62</c>: <c>MinArea=12, MaxIconArea=900,
    /// MinSolidity=0.35, MaxAspect=2.5, MinPeak=0.7</c>. Any scene whose alpha
    /// can't be classified gets this profile via the fail-soft default at the
    /// classifier seam.
    /// </summary>
    public static SceneCalibrationProfile Outdoor { get; } = new(
        SceneClass: SceneClass.Outdoor,
        BlobOptions: new BlobOptions(
            MinArea: 12, MaxIconArea: 900,
            MinSolidity: 0.35, MaxAspect: 2.5, MinPeak: 0.7));

    /// <summary>
    /// Indoor profile — relaxed classifier shape gates per the Phase-2
    /// measurement. <c>MaxAspect 2.5 → 2.7</c> recovers the systematic
    /// aspect-2.56 PG glyph that reproduces across distinct Hogan's bundles;
    /// <c>MinSolidity 0.35 → 0.30</c> recovers the borderline IconD-style
    /// blob. Other knobs unchanged from Outdoor — the measurement showed
    /// <c>LowNcc</c> / kernel <c>win</c> / morph <c>closeRadius</c> divergence
    /// doesn't move v1 recall (T3 partially falsified).
    /// </summary>
    public static SceneCalibrationProfile Indoor { get; } = new(
        SceneClass: SceneClass.Indoor,
        BlobOptions: new BlobOptions(
            MinArea: 12, MaxIconArea: 900,
            MinSolidity: 0.30, MaxAspect: 2.7, MinPeak: 0.7));

    /// <summary>
    /// Returns the canonical profile for <paramref name="sceneClass"/>. The
    /// dispatch is intentionally a switch on a 2-arm enum (per the project
    /// memory's "switch-as-registry smell" pattern, this stays a switch until a
    /// third arm lands — at which point the registry refactor is the right
    /// move). Outdoor is the safe-degrade default for unknown enum values.
    /// </summary>
    public static SceneCalibrationProfile For(SceneClass sceneClass) => sceneClass switch
    {
        SceneClass.Indoor => Indoor,
        SceneClass.Outdoor => Outdoor,
        _ => Outdoor,
    };
}
