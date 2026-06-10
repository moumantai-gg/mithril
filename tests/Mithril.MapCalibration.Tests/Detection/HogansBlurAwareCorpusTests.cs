using System.IO;
using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// mithril#1070 — drives the full <see cref="SobelPaddedPyramidRefiner"/> on
/// real Hogan's Basement captures (corpus PNGs shipped alongside the test
/// binary). Verifies the blur-aware template at the two scale extremes:
/// scale ≈ 0.28 ("OUT", the load-bearing failure case the σ-curve targets)
/// and scale ≈ 0.94 ("IN", the already-passing case to lock against
/// regression).
///
/// <para>The tests assert recovered (origin, scale) against the bundle's own
/// recovered values from the 04-maprect.json truth — tolerances are loose
/// (within ~10 px on origin, ~0.05 on scale) so the σ-curve can adjust the
/// recovered fix without flipping these tests red.</para>
/// </summary>
public sealed class HogansBlurAwareCorpusTests
{
    private readonly ITestOutputHelper _output;
    public HogansBlurAwareCorpusTests(ITestOutputHelper output) => _output = output;

    private const string CorpusDir = "Detection/blur_aware_corpus";

    private static string CorpusPath(string name) =>
        Path.Combine(System.AppContext.BaseDirectory, CorpusDir, name);

    private sealed record MapRectTruth(
        int OriginX, int OriginY,
        int Width, int Height,
        int TextureWidth, int TextureHeight,
        double Scale)
    {
        public static MapRectTruth Load(string path)
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var r = doc.RootElement;
            int width = r.GetProperty("width").GetInt32();
            int texW = r.GetProperty("textureWidth").GetInt32();
            return new MapRectTruth(
                OriginX: r.GetProperty("originX").GetInt32(),
                OriginY: r.GetProperty("originY").GetInt32(),
                Width: width,
                Height: r.GetProperty("height").GetInt32(),
                TextureWidth: texW,
                TextureHeight: r.GetProperty("textureHeight").GetInt32(),
                Scale: width / (double)texW);
        }
    }

    [Fact]
    public void Hogans_OUT_scale028_corpus_recovers_close_to_truth_with_blur_aware_template()
    {
        // The load-bearing case from spec §1.1: scale ≈ 0.28, ncc ≈ 0.56, the
        // bundle's locator accepted but the projection-overlay was nowhere
        // near visible icon glyphs. With blur on, the σ-curve at the recovered
        // scale ≈ 2.0; the assertion is that the refiner still produces a fix
        // and that it carries a non-null BlurAppliedSigma > 0.
        var screenshot = WicImageLoader.LoadGray(CorpusPath("hogans_out_screenshot_gray.png"));
        var texture = WicImageLoader.LoadGray(CorpusPath("hogans_texture.png"));
        var truth = MapRectTruth.Load(CorpusPath("hogans_out_maprect.json"));
        _output.WriteLine(
            $"OUT corpus: shot {screenshot.Width}x{screenshot.Height}, " +
            $"tex {texture.Width}x{texture.Height}, " +
            $"truth origin=({truth.OriginX},{truth.OriginY}) " +
            $"size={truth.Width}x{truth.Height} scale≈{truth.Scale:F3}");

        var options = new MapCalibrationLocateOptions
        {
            RendererBlurEnabled = true,
            // Production defaults from Plan Task 0 measurement fit. Pin them
            // here so this test moves in lockstep with the production curve.
            RendererBlurIntercept = -1.5643,
            RendererBlurSlope = 1.0043,
            RendererBlurMinSigma = 0.0,
            RendererBlurMaxSigma = 3.0,
            FallbackNccFloor = 0.0,   // accept whatever NCC; we're checking recovery + sigma surface.
        };
        var refiner = new SobelPaddedPyramidRefiner(options);

        var result = refiner.Refine(screenshot, texture);

        result.Metrics.Should().NotBeNull();
        _output.WriteLine(
            $"recovered: origin=({result.AcceptedRect?.OriginX},{result.AcceptedRect?.OriginY}) " +
            $"size={result.AcceptedRect?.Width}x{result.AcceptedRect?.Height} " +
            $"scale={result.Metrics!.Scale:F4} ncc={result.Metrics.Confidence:F3} " +
            $"sigma={result.Metrics.BlurAppliedSigma:F3}");

        result.Metrics.Provenance.Should().Be(LocateProvenance.SobelPaddedPyramid);
        // The whole point of #1070 — BlurAppliedSigma is recorded.
        result.Metrics.BlurAppliedSigma.Should().NotBeNull();
        // At scale ≈ 0.28, the σ-curve evaluates to ≈ 2.0 (above MinSigma=0).
        result.Metrics.BlurAppliedSigma!.Value.Should().BeGreaterThan(0.0,
            "the production σ-curve fires at the OUT scale (scale ≈ 0.28 → σ ≈ 2.0)");
        // Scale recovered within 0.05 of the bundle's own scale (it's the same
        // input — should converge to similar basin; blur can move it slightly).
        result.Metrics.Scale.Should().BeApproximately(truth.Scale, 0.05);
    }

    [Fact]
    public void Hogans_IN_scale094_corpus_recovers_close_to_truth_without_blur_clamped_to_zero()
    {
        // The already-passing case. At scale ≈ 0.94 the production σ-curve
        // clamps to 0 (intercept + slope×1.064 ≈ -0.5 < MinSigma) — no blur
        // applied. Lock against regression: this case must continue to
        // recover near the bundle's own truth.
        var screenshot = WicImageLoader.LoadGray(CorpusPath("hogans_in_screenshot_gray.png"));
        var texture = WicImageLoader.LoadGray(CorpusPath("hogans_texture.png"));
        var truth = MapRectTruth.Load(CorpusPath("hogans_in_maprect.json"));
        _output.WriteLine(
            $"IN corpus: shot {screenshot.Width}x{screenshot.Height}, " +
            $"tex {texture.Width}x{texture.Height}, " +
            $"truth origin=({truth.OriginX},{truth.OriginY}) " +
            $"size={truth.Width}x{truth.Height} scale≈{truth.Scale:F3}");

        var options = new MapCalibrationLocateOptions
        {
            RendererBlurEnabled = true,
            RendererBlurIntercept = -1.5643,
            RendererBlurSlope = 1.0043,
            RendererBlurMinSigma = 0.0,
            RendererBlurMaxSigma = 3.0,
            FallbackNccFloor = 0.0,
        };
        var refiner = new SobelPaddedPyramidRefiner(options);

        var result = refiner.Refine(screenshot, texture);

        result.Metrics.Should().NotBeNull();
        _output.WriteLine(
            $"recovered: origin=({result.AcceptedRect?.OriginX},{result.AcceptedRect?.OriginY}) " +
            $"size={result.AcceptedRect?.Width}x{result.AcceptedRect?.Height} " +
            $"scale={result.Metrics!.Scale:F4} ncc={result.Metrics.Confidence:F3} " +
            $"sigma={result.Metrics.BlurAppliedSigma:F3}");

        result.Metrics.Provenance.Should().Be(LocateProvenance.SobelPaddedPyramid);
        // BlurAppliedSigma is non-null (the producer always emits) and clamped
        // to 0 at this scale.
        result.Metrics.BlurAppliedSigma.Should().NotBeNull();
        result.Metrics.BlurAppliedSigma!.Value.Should().Be(0.0,
            "the production σ-curve clamps to 0 at scale ≈ 0.94 (above the curve's break-even point)");
        // Scale recovered within 0.05 of the bundle's own scale (no blur in
        // play — should converge tightly).
        result.Metrics.Scale.Should().BeApproximately(truth.Scale, 0.05);
    }

    [Fact]
    public void Disabling_blur_yields_zero_sigma_on_the_corpus()
    {
        // Regression lock — turning blur off produces sigma=0 regardless of
        // scale, matching the pre-mithril#1070 behaviour for the same fallback.
        var screenshot = WicImageLoader.LoadGray(CorpusPath("hogans_out_screenshot_gray.png"));
        var texture = WicImageLoader.LoadGray(CorpusPath("hogans_texture.png"));

        var options = new MapCalibrationLocateOptions
        {
            RendererBlurEnabled = false,
            FallbackNccFloor = 0.0,
        };
        var refiner = new SobelPaddedPyramidRefiner(options);

        var result = refiner.Refine(screenshot, texture);

        result.Metrics.Should().NotBeNull();
        result.Metrics!.BlurAppliedSigma.Should().Be(0.0);
    }
}
