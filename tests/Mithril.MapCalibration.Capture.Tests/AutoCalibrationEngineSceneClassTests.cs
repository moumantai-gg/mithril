using System.Threading;
using FluentAssertions;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// mithril#1163 Phase 1 + Phase 2 — engine-level wiring tests for the
/// SceneClass dispatch. Verify that the right
/// <see cref="SceneCalibrationProfile.BlobOptions"/> reach the solver based on
/// the base texture's alpha-coverage:
/// <list type="bullet">
///   <item>Outdoor alpha (≥ 95 % opaque) → Outdoor profile (today's universal constants)</item>
///   <item>Indoor alpha (&lt; 95 % opaque) → Indoor profile (T1 + T2: MaxAspect 2.7, MinSolidity 0.30)</item>
///   <item>Boundary cache unwired → safe-degrade Outdoor (preserves pre-#1163 byte-identically)</item>
/// </list>
/// Mithril#1168 review feedback: the indirect coverage via
/// <c>IndoorRecallMergeTuningTests</c> bypasses the engine entirely; this
/// suite pins the wiring contract at the engine layer so a future refactor
/// that breaks the dispatch fails CI loudly.
/// </summary>
public sealed class AutoCalibrationEngineSceneClassTests
{
    [Fact]
    public async System.Threading.Tasks.Task Outdoor_alpha_dispatches_Outdoor_BlobOptions_to_solver()
    {
        var harness = new EngineHarness { WireDeviationMaskDeps = true };
        var engine = harness.Engine();
        // BaseTextureProvider is auto-constructed by Engine() — populate alpha
        // AFTER engine creation but BEFORE the run so the boundary cache loads
        // it lazily on the first GetSceneClass call.
        // Outdoor: alpha = 255 everywhere → opaque fraction 1.00 ≥ 0.95 → Outdoor.
        harness.BaseTextureProvider.AlphaByKey[EngineHarness.DefaultMapAsset] = AlphaBuffer(64, 64, opaqueValue: true);

        await engine.TryCalibrateCurrentAreaAsync(CancellationToken.None);

        var blobOpts = harness.Solver.LastRequest!.BlobOptions;
        blobOpts.Should().Be(SceneCalibrationProfile.Outdoor.BlobOptions,
            "Outdoor-classed scene must dispatch today's universal constants — the Outdoor regression battery depends on this.");
        // mithril#1155 Phase 3 — Outdoor profile leaves MinPeakLuma null so the
        // peak-luma pre-filter is a no-op outdoors. This is what makes Outdoor
        // byte-identical-by-construction post-#1155.
        blobOpts.MinPeakLuma.Should().BeNull(
            "Outdoor profile MUST leave MinPeakLuma null so the peak-luma pre-filter is a no-op outdoors — the byte-identical Outdoor invariant depends on this.");
    }

    [Fact]
    public async System.Threading.Tasks.Task Indoor_alpha_dispatches_Indoor_BlobOptions_to_solver()
    {
        var harness = new EngineHarness { WireDeviationMaskDeps = true };
        var engine = harness.Engine();
        // Indoor: alpha = 0 everywhere → opaque fraction 0.00 < 0.95 → Indoor.
        // (All-transparent is a degenerate boundary mask but a well-defined SceneClass.)
        harness.BaseTextureProvider.AlphaByKey[EngineHarness.DefaultMapAsset] = AlphaBuffer(64, 64, opaqueValue: false);

        await engine.TryCalibrateCurrentAreaAsync(CancellationToken.None);

        var blobOpts = harness.Solver.LastRequest!.BlobOptions;
        blobOpts.Should().Be(SceneCalibrationProfile.Indoor.BlobOptions,
            "Indoor-classed scene must dispatch the relaxed T1+T2 gates — the Phase 2 recall lift depends on this.");
        blobOpts.MaxAspect.Should().Be(2.7);
        blobOpts.MinSolidity.Should().Be(0.30);
        // mithril#1155 Phase 3 — Indoor profile carries MinPeakLuma=0.7 so the
        // peak-luma pre-filter rejects floor-noise blobs that survived T1+T2's
        // relaxed shape gates. The engine MUST thread this value to the solver.
        blobOpts.MinPeakLuma.Should().Be(0.7,
            "Indoor profile MUST dispatch MinPeakLuma=0.7 — Phase 3 noise suppression depends on the threshold reaching the solver.");
    }

    /// <summary>
    /// mithril#1155 Phase 3 — the engine MUST thread the raw BGRA crop into
    /// <see cref="DetectionRequest.RawBgra"/> for the Indoor profile path, since
    /// the peak-luma pre-filter no-ops without it (both <c>MinPeakLuma</c> AND
    /// <c>rawBgra</c> gates have to be open). Without this wiring, Phase 3 is
    /// silently disabled even though the Indoor profile carries the threshold —
    /// the kind of "looks wired but isn't" gap mithril#1107's LiveMapViewService
    /// shipped with, called out in CLAUDE.md's Instrumentation contract.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task Engine_threads_raw_BGRA_crop_into_DetectionRequest()
    {
        var harness = new EngineHarness { WireDeviationMaskDeps = true };
        var engine = harness.Engine();
        // Indoor alpha → Indoor profile (the path that actually USES RawBgra).
        harness.BaseTextureProvider.AlphaByKey[EngineHarness.DefaultMapAsset] = AlphaBuffer(64, 64, opaqueValue: false);

        await engine.TryCalibrateCurrentAreaAsync(CancellationToken.None);

        var request = harness.Solver.LastRequest!;
        request.RawBgra.Should().NotBeNull(
            "engine must thread the BGRA crop so the peak-luma pre-filter (Indoor MinPeakLuma=0.7) can actually fire — without this wiring Phase 3 is silently a no-op.");
        // The cropped BGRA must match the gray crop's dims × 4 bytes/pixel.
        // Engine's TryCropBgra clamps to the resolved MapRect, which the test
        // harness configures to span the captured frame; both crops cover the
        // same rect so their length relationship is exact.
        request.RawBgra!.Length.Should().Be(
            request.Screenshot.Width * request.Screenshot.Height * 4,
            "BGRA crop dims must match the gray crop's dims × 4 bytes/pixel so blob pixel-indices land at the right offsets.");
    }

    [Fact]
    public async System.Threading.Tasks.Task Boundary_cache_unwired_safe_degrades_to_Outdoor_BlobOptions()
    {
        // Without WireDeviationMaskDeps the engine ctor leaves the boundary
        // cache null — mirrors the pre-#1163 test graphs that never opted in.
        // The dispatch falls through to Outdoor by safe-degrade, preserving
        // byte-identical behaviour for those graphs.
        var harness = new EngineHarness { WireDeviationMaskDeps = false };
        var engine = harness.Engine();

        await engine.TryCalibrateCurrentAreaAsync(CancellationToken.None);

        var blobOpts = harness.Solver.LastRequest!.BlobOptions;
        blobOpts.Should().Be(SceneCalibrationProfile.Outdoor.BlobOptions,
            "boundary cache unwired must safe-degrade to Outdoor — pre-#1163 graphs depend on byte-identical pre-existing behaviour.");
    }

    private static GrayImage AlphaBuffer(int w, int h, bool opaqueValue)
    {
        var p = new byte[w * h];
        if (opaqueValue) { for (int i = 0; i < p.Length; i++) p[i] = 255; }
        return new GrayImage(w, h, p);
    }
}
