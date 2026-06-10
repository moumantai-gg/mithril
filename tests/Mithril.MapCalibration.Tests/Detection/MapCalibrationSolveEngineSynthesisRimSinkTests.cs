using System;
using System.Collections.Generic;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// mithril#1123: the synthesis-J pipeline (L_t builder) is the second caller of
/// <c>DeviationFloodRimMask.Build</c>. Without these tests, a future regression
/// in the rim-mask-lift-out (Task 5) or a missing wrap (Task 6) would leave the
/// synth-J rim emission silently broken — the same failure mode #1116 hit with
/// the blob-detection pipeline.
/// </summary>
public sealed class MapCalibrationSolveEngineSynthesisRimSinkTests
{
    private const int W = 256, H = 192;

    private static (GrayImage Shot, GrayImage Tex) BuildPair()
    {
        var texturePixels = SyntheticMap.MakeTexture(W, H, seed: 4242);
        var shotPixels = (byte[])texturePixels.Clone();
        // A few bright spots so the deviation has predictable signal.
        SyntheticMap.BlitTeardrop(shotPixels, W, H, anchorX: 80, anchorY: 60, width: 16, height: 16, luminance: 220);
        SyntheticMap.BlitTeardrop(shotPixels, W, H, anchorX: 170, anchorY: 120, width: 16, height: 16, luminance: 220);
        return (new GrayImage(W, H, shotPixels), new GrayImage(W, H, texturePixels));
    }

    /// <summary>
    /// Driving <c>BuildLikelihoodFieldsFromDeviation</c> directly with hooks
    /// wired produces ONE <c>RimMaskSnapshot</c> with
    /// <c>Pipeline == "synthesis_j"</c>. Confirms the lifted rim mask emits at
    /// orchestrator level (once per orientation), not inside the per-template
    /// loop (which would emit N duplicates).
    /// </summary>
    [Fact]
    public void BuildLikelihoodFieldsFromDeviation_emits_synthesis_j_pipeline_tag()
    {
        var (shot, tex) = BuildPair();
        var templates = SyntheticMap.BuildTemplates(SyntheticMap.DefaultIcons);
        var rimSnaps = new List<RimMaskSnapshot>();
        var hooks = new DetectionDiagnosticHooks(
            OnDeviation: null,
            OnRimMask: rimSnaps.Add,
            OnMorph: null,
            OnBlobClassified: null);

        var engine = new MapCalibrationSolveEngine(
            detector: new DeviationBlobCalibrationDetector(),
            gate: new CalibrationConfidenceGate());

        engine.BuildLikelihoodFieldsFromDeviation(
            shot, tex, templates,
            typeFloor: 0.0,
            renderSizePx: null,
            rotate180: false,
            hooks: hooks);

        rimSnaps.Should().HaveCount(1, "rim mask is computed once per orientation; this call drives one");
        var snap = rimSnaps[0];
        snap.Pipeline.Should().Be(RimMaskPipeline.SynthesisJ);
        snap.RimMaskBuffer.Length.Should().Be(W * H);
        // mithril#1125: synthesis-J has no fg-pre/fg-post concept — null in memory
        // (the bundle DTO projects to the -1 wire sentinel for backwards-compat).
        snap.FgInputCount.Should().BeNull();
        snap.FgSurvivorCount.Should().BeNull();
        snap.Rotate180.Should().BeFalse();
    }

    /// <summary>
    /// Driving with <c>rotate180: true</c> tags the snapshot accordingly — the
    /// orientation flag passes through to the record so the SolveEngine's
    /// orientation-loop emits both records distinguishably.
    /// </summary>
    [Fact]
    public void BuildLikelihoodFieldsFromDeviation_propagates_rotate180_flag()
    {
        var (shot, tex) = BuildPair();
        var templates = SyntheticMap.BuildTemplates(SyntheticMap.DefaultIcons);
        var rimSnaps = new List<RimMaskSnapshot>();
        var hooks = new DetectionDiagnosticHooks(null, rimSnaps.Add, null, null);

        var engine = new MapCalibrationSolveEngine(
            detector: new DeviationBlobCalibrationDetector(),
            gate: new CalibrationConfidenceGate());

        engine.BuildLikelihoodFieldsFromDeviation(
            shot, tex, templates,
            typeFloor: 0.0,
            renderSizePx: null,
            rotate180: true,
            hooks: hooks);

        rimSnaps.Should().HaveCount(1);
        rimSnaps[0].Rotate180.Should().BeTrue();
    }

    /// <summary>
    /// Null hooks → zero emission (producer-cost contract). The method still
    /// returns a valid field dictionary — no side-effect on the synthesis path.
    /// </summary>
    [Fact]
    public void BuildLikelihoodFieldsFromDeviation_emits_nothing_when_hooks_null()
    {
        var (shot, tex) = BuildPair();
        var templates = SyntheticMap.BuildTemplates(SyntheticMap.DefaultIcons);

        var engine = new MapCalibrationSolveEngine(
            detector: new DeviationBlobCalibrationDetector(),
            gate: new CalibrationConfidenceGate());

        var fields = engine.BuildLikelihoodFieldsFromDeviation(
            shot, tex, templates,
            typeFloor: 0.0,
            renderSizePx: null,
            rotate180: false,
            hooks: null);

        fields.Should().NotBeEmpty();  // synthesis still ran; no exception thrown
    }

    /// <summary>
    /// Task 6: Solve()'s orientation loop wraps the caller's hooks so each
    /// orientation pass tags its records with the right Rotate180 flag. The
    /// detector emits rotate180=false on every record; the wrap rewrites.
    /// Drives via Solve so the wrap + the detector + the synth-J path all fire.
    /// </summary>
    [Fact]
    public void Solve_wraps_diagnostic_hooks_with_orientation_flag()
    {
        var (shot, tex) = BuildPair();
        var templates = SyntheticMap.BuildTemplates(SyntheticMap.DefaultIcons);

        var deviationSnaps = new List<DeviationSnapshot>();
        var hooks = new DetectionDiagnosticHooks(
            OnDeviation: deviationSnaps.Add,
            OnRimMask: null,
            OnMorph: null,
            OnBlobClassified: null);

        var rect = new MapRect(0, 0, W, H, W, H);
        var opts = new BlobOptions(MinArea: 8, MaxIconArea: 1500,
            MinSolidity: 0.25, MaxAspect: 3.5, MinPeak: 0.5);
        var request = new DetectionRequest(shot, tex, rect, templates,
            RimMaskMode.DeviationFlood, LowNcc: 0.5, TypeFloor: 0.45, BlobOptions: opts)
        {
            Diagnostics = hooks,
        };

        var engine = new MapCalibrationSolveEngine(
            detector: new DeviationBlobCalibrationDetector(),
            gate: new CalibrationConfidenceGate());

        engine.Solve(request, references: Array.Empty<LandmarkReference>());

        // Two orientation passes → two DeviationSnapshot records (one per pass),
        // each tagged with the correct rotate180 flag.
        deviationSnaps.Should().HaveCount(2);
        deviationSnaps.Should().Contain(s => s.Rotate180 == false);
        deviationSnaps.Should().Contain(s => s.Rotate180 == true);
    }
}
