using System;
using System.Diagnostics;
using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Xunit;
using Xunit.Abstractions;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class TextureRegistrationRefinerTests
{
    private readonly ITestOutputHelper _output;

    public TextureRegistrationRefinerTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Refine_locates_an_embedded_texture()
    {
        var tex = SyntheticMap.NoisyTexture(seed: 3, w: 64, h: 64);
        var frame = SyntheticMap.PasteInto(tex, canvasW: 160, canvasH: 120, atX: 40, atY: 30);
        var result = new TextureRegistrationRefiner().Refine(frame, tex, minScore: 0.5);
        result.AcceptedRect.Should().NotBeNull();
        result.AcceptedRect!.OriginX.Should().BeCloseTo(40, 3);
        result.AcceptedRect.OriginY.Should().BeCloseTo(30, 3);
    }

    /// <summary>
    /// Observability seam: when the coarse score falls below the engine's threshold,
    /// the refiner must still surface what the locator found — the bundle/log
    /// downstream wants the score and origin even on a "couldn't locate" outcome,
    /// so future close-miss vs catastrophic-miss is self-triaging.
    /// </summary>
    [Fact]
    public void Refine_surfaces_BestCoarseRect_when_below_minScore()
    {
        var tex = SyntheticMap.NoisyTexture(seed: 3, w: 64, h: 64);
        var frame = SyntheticMap.PasteInto(tex, canvasW: 160, canvasH: 120, atX: 40, atY: 30);
        // minScore > 1.0 forces rejection without engineering a deliberately-poor
        // input; the embedded texture's match still scores ~1.0.
        var result = new TextureRegistrationRefiner().Refine(frame, tex, minScore: 1.5);

        result.AcceptedRect.Should().BeNull("score is below the impossible threshold");
        // PR-1 transitional: BestCoarseRect is the [Obsolete] alias for RawFitRect under
        // the feature-matching-locate rename. The refiner under test still populates it
        // via the 2-arg ctor; PR-3 rewrites these assertions onto RawFitRect.
#pragma warning disable CS0618 // BestCoarseRect: alias removed in PR-3
        result.BestCoarseRect.Should().NotBeNull("the locator did find a best rung — surface it");
        result.BestCoarseRect!.AutoDetectScore.Should().NotBeNull();
        result.BestCoarseRect.AutoDetectScore!.Value.Should().BeGreaterThan(0.5);
        result.BestCoarseRect.OriginX.Should().BeCloseTo(40, 3);
        result.BestCoarseRect.OriginY.Should().BeCloseTo(30, 3);
#pragma warning restore CS0618
    }

    /// <summary>
    /// #966 seam guard. The structural fix lives in <see cref="TextureRegistrationRefiner.Refine"/>:
    /// it must route through the DOWNSAMPLING <see cref="MapRectLocator.AutoDetect(GrayImage, GrayImage, double, int)"/>
    /// overload, not the native 3-arg one. The existing perf test exercises
    /// <c>AutoDetect</c> DIRECTLY, and the other refiner test feeds &lt;384px inputs where
    /// downsampling is a no-op — so a regression reverting <c>TextureRegistrationRefiner.cs</c>
    /// to the native overload would pass the whole suite undetected. This test drives the
    /// PRODUCTION SEAM (<c>new TextureRegistrationRefiner().Refine(...)</c>) at live
    /// resolution to pin that the seam itself takes the downsampling path.
    /// </summary>
    [Fact]
    public void Refine_through_the_seam_downsamples_at_live_resolution()
    {
        // Live base texture (~2048×2033) — LARGER than the capture, the regime that hung.
        const int tw = 2048, th = 2033;
        var texture = SyntheticMap.StructuredTexture(seed: 4242, w: tw, h: th);

        // Live capture ~1257×1049 with the map zoomed all the way out so the whole
        // texture renders into it. The texture is ~square, so the render is HEIGHT-bound
        // by the 1049 capture; size it to fit fully (no clipping) at a known origin.
        const int captureW = 1257, captureH = 1049;
        const int padX = 70, padY = 55;
        int rh = captureH - padY - 40;                  // height-bound render
        int rw = (int)Math.Round((double)rh * tw / th); // preserve texture aspect
        var rendered = ImageOps.Resize(texture, rw, rh);

        var shot = new byte[captureW * captureH];
        Array.Fill(shot, (byte)40);
        for (int y = 0; y < rh && (y + padY) < captureH; y++)
            Buffer.BlockCopy(rendered.Pixels, y * rw, shot, (y + padY) * captureW + padX, rw);
        var capture = new GrayImage(captureW, captureH, shot);

        var sw = Stopwatch.StartNew();
        var result = new TextureRegistrationRefiner().Refine(capture, texture, minScore: 0.3);
        sw.Stop();

        _output.WriteLine($"seam Refine at live resolution: {sw.ElapsedMilliseconds} ms");

        // CORRECTNESS: the embedded texture is located, the result comes back in
        // FULL-capture coordinates (origin/size on the order of the 1257×1049 capture,
        // NOT the 384px working size), and the texture dims are the FULL texture — never
        // the working-resolution copy. The ~4× capture box-average leaves ≲ one working
        // pixel (≈4 full px) of origin quantisation plus the discrete-rung scale slop.
        result.AcceptedRect.Should().NotBeNull("the embedded texture must be locatable through the seam");
        var rect = result.AcceptedRect!;
        rect.OriginX.Should().BeCloseTo(padX, 35);
        rect.OriginY.Should().BeCloseTo(padY, 35);
        rect.Width.Should().BeGreaterThan(MapRectLocator.DefaultWorkingLongEdgePx,
            "the rect must be unscaled back to full-capture pixels, not left at working resolution");
        rect.TextureWidth.Should().Be(tw);
        rect.TextureHeight.Should().Be(th);

        // SPEED (structural tripwire): this measures the WHOLE seam — the 384px working-res
        // NCC ladder PLUS the new #978 ECC sub-pixel refine (Cv2.FindTransformECC, 200 iters /
        // 1e-6 / gaussFilt 5). On real captures ECC correlates well and converges in well under
        // a second (≈330–640 ms, issue #978 data); on THIS synthetic structured-noise input it
        // correlates poorly and runs near its 200-iteration cap, adding several seconds. The
        // coarse ladder alone is a few seconds in Debug (measured ≈3.8 s isolated, ≈4.1 s under
        // the full parallel suite on a 16-core box); with ECC on top the contended total reaches
        // ≈11.4 s. None of that is a production perf concern — it's an artefact of the synthetic
        // input, and real-frame ECC stays sub-second.
        //
        // What this assertion still PROVES is purely structural: that the seam takes the #966
        // DOWNSAMPLING path. If the seam reverted to the native 3-arg AutoDetect overload it
        // would run the FULL-resolution ladder (~1–2e12 multiply-adds, MINUTES — ≥120 s observed
        // live for #966) BEFORE ECC ever runs, so the revert floor is still ~120 s+. The 28 s
        // budget sits ~2.5× above the observed ≈11.4 s contended working-res + ECC cost (so it
        // never flakes on a loaded CI box) and still ~4.3× BELOW the ≥120 s native-ladder
        // regression — a comfortable, unambiguous gap. PASSING therefore proves the seam ran at
        // working resolution, not native, and would trip on a revert of TextureRegistrationRefiner.cs.
        sw.ElapsedMilliseconds.Should().BeLessThan(28_000,
            "the seam must take the #966 downsampling path; the native ladder would blow this budget by orders of magnitude (≥120 s)");
    }
}
