using System;
using System.Collections.Generic;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture.Diagnostics;

/// <summary>
/// Per-attempt mutable data carrier. Populated by AutoCalibrationEngine as the
/// pipeline progresses; consumed by ICalibrationAttemptBundleSink.Write at the
/// end of the attempt (success, gate-reject, exception, or cancellation).
/// </summary>
public sealed class CalibrationAttemptContext
{
    public CalibrationAttemptContext(string area, DateTimeOffset startedUtc)
    {
        Area = area;
        StartedUtc = startedUtc;
    }

    public string Area { get; }
    public DateTimeOffset StartedUtc { get; }

    // Filled by the engine as it goes. All nullable — sink writes what it has.
    public CapturedFrame? RawCapture { get; set; }
    public GrayImage? GrayCapture { get; set; }
    public GrayImage? BaseTextureResampled { get; set; }
    public MapRect? MapRect { get; set; }
    /// <summary>
    /// The locator's raw fit rect — populated whenever the refiner produced any fit
    /// (gate-pass-or-not). Set on both accept and <c>rejected-map-not-located</c>
    /// outcomes so the bundle/log makes future close-miss vs catastrophic-mismatch
    /// self-triaging. Replaces the pre-PR-3 <c>LocatorBestRect</c>.
    /// </summary>
    public MapRect? LocatorRawFit { get; set; }

    /// <summary>
    /// The locator's FM-style metrics — populated whenever the refiner produced any
    /// fit (gate-pass-or-not). Carries inlier count/ratio + recovered similarity
    /// transform parameters + median residual. Null under the in-tree NCC refiner
    /// (which doesn't produce these); populated once PR-4 swaps to the FM refiner.
    /// </summary>
    public LocateMetrics? LocatorMetrics { get; set; }
    public GrayImage? AlignedCrop { get; set; }
    public GrayImage? AlignedTexture { get; set; }
    public IReadOnlyList<LandmarkReference>? References { get; set; }
    public IReadOnlyList<TypedDetection>? Detections { get; set; }
    public CalibrationSolveResult? Result { get; set; }

    /// <summary>
    /// Per-blob × per-template NCC observations from the deviation-blob detector
    /// (mithril#1121). Populated by AutoCalibrationEngine when the engine wires
    /// <see cref="DetectionRequest.BlobScoreSink"/> for the attempt. <c>null</c>
    /// (default) when the diagnostic sink wasn't wired or when the underlying
    /// detector doesn't emit (whole-image fallback path); empty when the wiring
    /// fired but the deviation map produced zero blobs.
    /// </summary>
    public IReadOnlyList<BlobTemplateScore>? BlobTemplateScores { get; set; }

    // mithril#1123: per-stage observations from the deviation-blob detector
    // pipeline. Populated by AutoCalibrationEngine when it wires
    // DetectionRequest.Diagnostics for the attempt; up to two records per
    // orientation pass (and per (orientation, pipeline) for the rim mask).
    // All four assigned even when empty so the bundle sink distinguishes
    // "diagnostic wiring missing" from "diagnostic ran, found nothing."
    /// <summary>
    /// Per-orientation deviation stats + fg-initial bool[] (mithril#1123).
    /// Up to two records (×orientation).
    /// </summary>
    public IReadOnlyList<DeviationSnapshot>? DeviationSnapshots { get; set; }

    /// <summary>
    /// Per-(orientation, pipeline) rim mask bool[] (mithril#1123).
    /// pipeline ∈ {"blob_detection", "synthesis_j"}; up to four records
    /// (×orientation × pipeline).
    /// </summary>
    public IReadOnlyList<RimMaskSnapshot>? RimMaskSnapshots { get; set; }

    /// <summary>
    /// Per-orientation morph-close output bool[] (mithril#1123). Up to two
    /// records (×orientation).
    /// </summary>
    public IReadOnlyList<MorphSnapshot>? MorphSnapshots { get; set; }

    /// <summary>
    /// Per-blob classification across ALL comps — not just Icons — including
    /// Noise/Fog/Structure verdicts (mithril#1123). Volume on Hogan's-shaped
    /// inputs is ~25-50 records per orientation pass.
    /// </summary>
    public IReadOnlyList<BlobClassification>? BlobClassifications { get; set; }

    /// <summary>
    /// mithril#1116: the OR-combined deviation mask the engine fed into
    /// <c>DetectionRequest.DeviationMask</c> for this attempt — floor-boundary
    /// alpha-edge band (texture-derived) OR fog-of-war chrome (screenshot-derived),
    /// sampled at the aligned crop dimensions (so width × height equal
    /// <see cref="AlignedCrop"/>'s). Null when the engine ran with
    /// <see cref="MapCalibrationDetectorOptions.DeviationMaskingEnabled"/> false,
    /// when both upstream sources were null (no alpha + fog disabled), or when no
    /// crop existed yet (pre-locate reject). When non-null the
    /// <see cref="FilesystemCalibrationAttemptBundleSink"/> writes it to
    /// <c>07a-deviation-mask.png</c> and the JSON pins
    /// <see cref="AttemptFilesJson.DeviationMask"/> = the artifact name.
    /// </summary>
    public GrayImage? DeviationMaskImage { get; set; }

    // mithril#1163 Phase 1: per-attempt scene-class classification + the
    // SceneCalibrationProfile the engine resolved for this attempt. Populated
    // by AutoCalibrationEngine after the deviation-mask block (the same
    // alpha-coverage scan that drives the boundary mask also classifies the
    // scene). All three nullable so an attempt that skipped the mask block
    // (pre-locate reject, DeviationMaskingEnabled=false, boundary cache
    // unwired) emits absence in the bundle rather than the misleading
    // "Outdoor + Outdoor BlobOptions" — a reader investigating "why did this
    // Indoor scene reject?" can distinguish "we didn't resolve a class" from
    // "we resolved Outdoor and ran with Outdoor gates" (mithril#1168 review).
    /// <summary>
    /// mithril#1163 Phase 1: scene class for this attempt, derived from the
    /// base texture's alpha-coverage fraction (Outdoor when fraction ≥
    /// <see cref="MapCalibrationDetectorOptions.SceneClassOpaqueFractionThreshold"/>).
    /// Null when the scene wasn't classified (mask block skipped) — the bundle
    /// emits absence which readers treat as "Outdoor by safe-degrade".
    /// </summary>
    public SceneClass? SceneClass { get; set; }

    /// <summary>
    /// mithril#1163 Phase 1: measured opaque-pixel fraction (alpha ≥ 128 /
    /// total) for the base texture. Null when the scene wasn't classified
    /// (mask block skipped). Diagnostic-only — drives the bundle JSON's
    /// <c>sceneClassOpaqueFraction</c> field per spec §5.6.
    /// </summary>
    public double? SceneClassOpaqueFraction { get; set; }

    /// <summary>
    /// mithril#1163 Phase 1: the SceneCalibrationProfile the engine resolved
    /// for this attempt — Outdoor (today's universal constants) or Indoor
    /// (relaxed classifier shape gates per the
    /// <c>indoor-recall-merge-fix-candidates.md</c> measurement). Null when
    /// the scene wasn't classified (mask block skipped); the bundle emits
    /// absence which readers treat as "Outdoor profile by safe-degrade".
    /// </summary>
    public SceneCalibrationProfile? Profile { get; set; }

    // Outcome is set explicitly by the engine — either at each Fail() site, at
    // the end of the success path, or in the catch (exception → "error").
    public string Outcome { get; set; } = "unknown";
    public string? ExceptionInfo { get; set; }
}
