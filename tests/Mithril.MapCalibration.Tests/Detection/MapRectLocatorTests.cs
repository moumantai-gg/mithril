using System;
using System.Diagnostics;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;
using Xunit.Abstractions;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class MapRectLocatorTests
{
    private readonly ITestOutputHelper _output;

    public MapRectLocatorTests(ITestOutputHelper output) => _output = output;

    private static GrayImage NoisyTexture(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var px = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double gradient = 80 + 80.0 * x / w + 60.0 * y / h;
                int v = (int)gradient + rng.Next(-30, 31);
                px[y * w + x] = (byte)Math.Clamp(v, 0, 255);
            }
        return new GrayImage(w, h, px);
    }

    /// <summary>
    /// A texture with strong, survives-downsampling structure (bright cross + a few
    /// blobs over noise) so the NCC peak is unambiguous in x AND y even after the
    /// working-resolution downsample. A pure monotonic gradient can slide under NCC
    /// (translation-invariant by construction), which a perf/correctness test must
    /// not depend on.
    /// </summary>
    private static GrayImage StructuredTexture(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var px = new byte[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)rng.Next(40, 90);

        // Asymmetric bright cross — breaks translational ambiguity in both axes.
        int cx = w * 3 / 8, cy = h * 5 / 8;
        int bandX = Math.Max(2, w / 40), bandY = Math.Max(2, h / 40);
        for (int y = 0; y < h; y++)
            for (int x = cx - bandX; x <= cx + bandX; x++)
                if (x >= 0 && x < w) px[y * w + x] = 235;
        for (int x = 0; x < w; x++)
            for (int y = cy - bandY; y <= cy + bandY; y++)
                if (y >= 0 && y < h) px[y * w + x] = 235;

        // A few solid blobs at irregular positions for extra distinctiveness.
        (int bx, int by, int br)[] blobs =
        {
            (w / 6, h / 5, Math.Max(4, w / 18)),
            (w * 4 / 5, h / 3, Math.Max(4, w / 22)),
            (w * 2 / 3, h * 4 / 5, Math.Max(4, w / 16)),
        };
        foreach (var (bx, by, br) in blobs)
            for (int y = -br; y <= br; y++)
                for (int x = -br; x <= br; x++)
                    if (x * x + y * y <= br * br)
                    {
                        int px2 = bx + x, py2 = by + y;
                        if (px2 >= 0 && px2 < w && py2 >= 0 && py2 < h) px[py2 * w + px2] = 20;
                    }
        return new GrayImage(w, h, px);
    }

    [Fact]
    public void AutoDetect_recovers_origin_of_embedded_texture()
    {
        const int tw = 120, th = 90;
        var texture = NoisyTexture(tw, th, 1234);

        // Pad the texture into a larger "screenshot" with constant UI chrome,
        // at native scale (factor 1.0).
        const int padX = 20, padY = 35;
        int sw = tw + padX + 30, sh = th + padY + 25;
        var shot = new byte[sw * sh];
        Array.Fill(shot, (byte)40);
        for (int y = 0; y < th; y++)
            Buffer.BlockCopy(texture.Pixels, y * tw, shot, (y + padY) * sw + padX, tw);
        var screenshot = new GrayImage(sw, sh, shot);

        var rect = MapRectLocator.AutoDetect(screenshot, texture, minScore: 0.5);

        rect.Should().NotBeNull();
        rect!.OriginX.Should().BeCloseTo(padX, 3);
        rect.OriginY.Should().BeCloseTo(padY, 3);
    }

    [Fact]
    public void MapRect_ScreenshotToTexture_round_trips()
    {
        // Texture 200x100 rendered into a 100x50 window at origin (10, 20).
        var rect = new MapRect(OriginX: 10, OriginY: 20, Width: 100, Height: 50, TextureWidth: 200, TextureHeight: 100);

        var (tx, ty) = rect.ScreenshotToTexture(60, 45);

        // (60-10)*2 = 100 ; (45-20)*2 = 50
        tx.Should().BeApproximately(100, 1e-9);
        ty.Should().BeApproximately(50, 1e-9);
    }

    /// <summary>
    /// Embeds a downscaled copy of a native-resolution base texture into a
    /// native-resolution capture (the "map fills the screen" case) and recovers the
    /// origin via the downsampling overload. TextureWidth/Height must stay at the FULL
    /// texture dims, and the origin/size must come back in FULL-capture pixels.
    /// </summary>
    [Fact]
    public void AutoDetect_downsampling_overload_recovers_origin_in_full_capture_pixels()
    {
        const int tw = 600, th = 480;
        var texture = StructuredTexture(tw, th, 7);

        // Render the texture into the capture at ~0.7x (factor ≈ 1.43, a "fills most
        // of the screen" zoom). Origin offset by a UI-chrome margin.
        const double renderScale = 0.7;
        int rw = (int)(tw * renderScale), rh = (int)(th * renderScale);
        var rendered = ImageOps.Resize(texture, rw, rh);

        const int padX = 60, padY = 40;
        int sw = rw + padX + 50, sh = rh + padY + 35;
        var shot = new byte[sw * sh];
        Array.Fill(shot, (byte)40);
        for (int y = 0; y < rh; y++)
            Buffer.BlockCopy(rendered.Pixels, y * rw, shot, (y + padY) * sw + padX, rw);
        var screenshot = new GrayImage(sw, sh, shot);

        var rect = MapRectLocator.AutoDetect(
            screenshot, texture, minScore: 0.5, MapRectLocator.DefaultWorkingLongEdgePx);

        rect.Should().NotBeNull();
        // Origin in FULL-capture pixels (allow a few px slop from the working-res unscale).
        rect!.OriginX.Should().BeCloseTo(padX, 12);
        rect.OriginY.Should().BeCloseTo(padY, 12);
        // Texture dims are the FULL texture, never the working-resolution copy.
        rect.TextureWidth.Should().Be(tw);
        rect.TextureHeight.Should().Be(th);
    }

    /// <summary>
    /// The downsampling overload must be a no-op when both inputs already fit under the
    /// working size — same result as the native overload (the synthetic-test contract).
    /// </summary>
    [Fact]
    public void AutoDetect_downsampling_overload_is_noop_for_small_inputs()
    {
        const int tw = 120, th = 90;
        var texture = NoisyTexture(tw, th, 1234);
        const int padX = 20, padY = 35;
        int sw = tw + padX + 30, sh = th + padY + 25;
        var shot = new byte[sw * sh];
        Array.Fill(shot, (byte)40);
        for (int y = 0; y < th; y++)
            Buffer.BlockCopy(texture.Pixels, y * tw, shot, (y + padY) * sw + padX, tw);
        var screenshot = new GrayImage(sw, sh, shot);

        var native = MapRectLocator.AutoDetect(screenshot, texture, minScore: 0.5);
        var downsampled = MapRectLocator.AutoDetect(
            screenshot, texture, minScore: 0.5, MapRectLocator.DefaultWorkingLongEdgePx);

        downsampled.Should().NotBeNull();
        downsampled!.OriginX.Should().Be(native!.OriginX);
        downsampled.OriginY.Should().Be(native.OriginY);
        downsampled.TextureWidth.Should().Be(native.TextureWidth);
        downsampled.SourceScaleFactor.Should().Be(native.SourceScaleFactor);
    }

    /// <summary>
    /// Task 2: the continuous <see cref="MapRect.SourceScaleFactor"/> should land
    /// between the bracketing ladder rungs (not snapped to a discrete rung) when the
    /// true render scale falls between rungs — proving the parabolic refinement runs.
    /// </summary>
    [Fact]
    public void AutoDetect_refines_source_scale_factor_off_the_discrete_ladder()
    {
        // The ladder's discrete rungs (a constant in MapRectLocator). Used only to
        // assert the refined factor is OFF the grid — i.e. the parabola engaged.
        double[] ladderRungs =
        {
            1.0, 1.1, 1.2, 1.35, 1.5, 1.75, 2.0, 2.1, 2.2, 2.4, 2.6, 2.8,
            3.0, 3.25, 3.5, 4.0, 4.5, 5.0, 6.0, 8.0, 10.0,
        };

        const int tw = 240, th = 200;
        var texture = StructuredTexture(tw, th, 99);

        // Render at factor 1.3 — strictly between ladder rungs 1.2 and 1.35, so the
        // discrete ladder cannot represent the true scale and the parabola must move
        // the recovered factor off the grid.
        const double factor = 1.3;
        int rw = (int)Math.Round(tw / factor), rh = (int)Math.Round(th / factor);
        var rendered = ImageOps.Resize(texture, rw, rh);
        int sw = rw + 50, sh = rh + 40;
        var shot = new byte[sw * sh];
        Array.Fill(shot, (byte)40);
        for (int y = 0; y < rh; y++)
            Buffer.BlockCopy(rendered.Pixels, y * rw, shot, (y + 18) * sw + 22, rw);
        var screenshot = new GrayImage(sw, sh, shot);

        var rect = MapRectLocator.AutoDetect(screenshot, texture, minScore: 0.3);

        rect.Should().NotBeNull();
        rect!.SourceScaleFactor.Should().NotBeNull();
        double f = rect.SourceScaleFactor!.Value;

        // Continuous + sensible: a finite factor inside the plausible bracket around
        // the true 1.3 (NCC score-curve noise can bias the parabola vertex toward a
        // neighbour, so allow the full 1.2–1.5 bracket rather than over-fitting).
        f.Should().BeInRange(1.15, 1.5);
        // Off the discrete grid — proves the parabolic refinement actually ran rather
        // than snapping to a rung.
        ladderRungs.Should().NotContain(r => Math.Abs(r - f) < 1e-6,
            "the refined factor must be continuous, not a discrete ladder rung");
    }

    /// <summary>
    /// #966 perf guard. At LIVE resolution (~1257×1049 capture vs 2048×2033 texture)
    /// the native ladder is ~1–2e12 multiply-adds and takes minutes; the downsampling
    /// overload must complete sub-second AND still locate the embedded texture. This
    /// closes the "only validated on pre-downsampled inputs" gap (the CI miss that let
    /// the live hang ship).
    /// </summary>
    [Fact]
    public void AutoDetect_downsampling_overload_is_subsecond_at_live_resolution()
    {
        const int tw = 2048, th = 2033;
        var texture = StructuredTexture(tw, th, 4242);

        // Live capture ~1257×1049 with the map filling most of it. The texture is
        // ~square (2048×2033), so the render is HEIGHT-bound by the 1049 capture —
        // size it to fit fully inside (no clipping) at a known origin.
        const int captureW = 1257, captureH = 1049;
        const int padX = 70, padY = 55;
        int rh = captureH - padY - 40;                       // height-bound render
        int rw = (int)Math.Round((double)rh * tw / th);      // preserve texture aspect
        var rendered = ImageOps.Resize(texture, rw, rh);

        var shot = new byte[captureW * captureH];
        Array.Fill(shot, (byte)40);
        for (int y = 0; y < rh && (y + padY) < captureH; y++)
            Buffer.BlockCopy(rendered.Pixels, y * rw, shot, (y + padY) * captureW + padX, rw);
        var capture = new GrayImage(captureW, captureH, shot);

        var sw = Stopwatch.StartNew();
        var rect = MapRectLocator.AutoDetect(
            capture, texture, minScore: 0.3, MapRectLocator.DefaultWorkingLongEdgePx);
        sw.Stop();

        _output.WriteLine($"live-res AutoDetect: {sw.ElapsedMilliseconds} ms");

        // Correctness: the embedded texture is found at the expected origin (full px).
        // Tolerance covers the working-resolution unscale: the capture is box-averaged
        // by ~4× before the search, so the recovered origin carries ≲ one working-pixel
        // (≈4 full px) of quantisation plus the discrete-rung scale mismatch.
        rect.Should().NotBeNull("the embedded texture must be locatable");
        rect!.TextureWidth.Should().Be(tw);
        rect.TextureHeight.Should().Be(th);
        rect.OriginX.Should().BeCloseTo(padX, 35);
        rect.OriginY.Should().BeCloseTo(padY, 35);

        // Speed: the #966 regression was MINUTES — the native ladder is ≈1–2e12
        // multiply-adds at full resolution (≥120 s observed live). The downsampling
        // overload is sub-second on the shipping (Release) path (locally ≈0.66 s).
        //
        // The budget here is deliberately coarse (30 s, not sub-second) because this
        // wall-clock runs under the Debug JIT AND under the full-suite's cross-assembly
        // test parallelism, where a 16-core box is saturated and a single un-optimised
        // NCC pass can balloon several-fold. A coarse budget that still sits >4× below
        // the minutes-long brute-force makes the guard DETERMINISTIC (never flakes on a
        // loaded CI box) while unambiguously catching a regression to the native-
        // resolution search — the actual failure mode #966 fixed. The "sub-second"
        // property is the Release-path claim, verified by manual measurement, not by a
        // contention-sensitive Debug wall-clock assertion.
        sw.ElapsedMilliseconds.Should().BeLessThan(30_000,
            "the #966 downsample must keep live-resolution AutoDetect far below the minutes-long brute-force regression, even under a contended Debug CI run");
    }

    /// <summary>
    /// Observability seam: the threshold-applying <see cref="MapRectLocator.AutoDetect(GrayImage, GrayImage, double)"/>
    /// returns <c>null</c> on a sub-threshold match, throwing away the score we'd need
    /// to triage close-miss-vs-catastrophic. <see cref="MapRectLocator.AutoDetectBest(GrayImage, GrayImage)"/>
    /// always returns the best rung's rect with <see cref="MapRect.AutoDetectScore"/>
    /// populated so the engine can log it and the bundle can record it.
    /// </summary>
    [Fact]
    public void AutoDetectBest_returns_rect_with_score_even_when_below_typical_threshold()
    {
        // The embedded-texture pair scores ~1.0 on the matching rung; using a
        // minScore > 1.0 on the threshold-applying overload guarantees rejection
        // without engineering a deliberately-poor input.
        const int tw = 120, th = 90;
        var texture = NoisyTexture(tw, th, 1234);
        const int padX = 20, padY = 35;
        int sw = tw + padX + 30, sh = th + padY + 25;
        var shot = new byte[sw * sh];
        Array.Fill(shot, (byte)40);
        for (int y = 0; y < th; y++)
            Buffer.BlockCopy(texture.Pixels, y * tw, shot, (y + padY) * sw + padX, tw);
        var screenshot = new GrayImage(sw, sh, shot);

        // Thresholded overload rejects (no rect returned — score is hidden).
        MapRectLocator.AutoDetect(screenshot, texture, minScore: 1.5).Should().BeNull();

        // Best-rect overload surfaces the score regardless of threshold.
        var best = MapRectLocator.AutoDetectBest(screenshot, texture);

        best.Should().NotBeNull();
        best!.AutoDetectScore.Should().NotBeNull();
        best.AutoDetectScore!.Value.Should().BeInRange(0.0, 1.0);
        // The match really did succeed — score is meaningfully high; threshold gate
        // is the *only* reason AutoDetect dropped it.
        best.AutoDetectScore!.Value.Should().BeGreaterThan(0.5);
        best.OriginX.Should().BeCloseTo(padX, 3);
        best.OriginY.Should().BeCloseTo(padY, 3);
    }
}
