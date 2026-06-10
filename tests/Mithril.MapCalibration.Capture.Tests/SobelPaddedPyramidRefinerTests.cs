using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class SobelPaddedPyramidRefinerTests
{
    private static SobelPaddedPyramidRefiner BuildRefiner(MapCalibrationLocateOptions? opts = null)
        => new(opts ?? new MapCalibrationLocateOptions());

    [Fact]
    public void Recovers_translation_when_capture_is_a_pasted_crop_at_known_origin()
    {
        // RichNoise has the high-frequency content Sobel-magnitude needs to lock
        // onto. PasteInto places the texture at (192, 100) inside a larger gray
        // background — the refiner must recover that origin within ±2 px.
        var texture = TestPatterns.RichNoise(width: 256, height: 256);
        var screenshot = TestPatterns.PasteInto(
            background: TestPatterns.UniformGray(640, 480, 128),
            foreground: texture,
            originX: 192, originY: 100);

        var result = BuildRefiner().Refine(screenshot, texture);

        result.AcceptedRect.Should().NotBeNull();
        result.AcceptedRect!.OriginX.Should().BeCloseTo(192, 2);
        result.AcceptedRect.OriginY.Should().BeCloseTo(100, 2);
        result.Metrics!.Provenance.Should().Be(LocateProvenance.SobelPaddedPyramid);
        result.Metrics.Confidence.Should().NotBeNull();
        result.Metrics.Confidence!.Value.Should().BeGreaterThan(0.5);
    }

    [Fact]
    public void Recovers_half_scale_when_capture_is_a_downsampled_view()
    {
        // Downsampling RichNoise via bilinear resize blurs the gradients and
        // halves the Sobel response amplitude relative to the original-resolution
        // texture, so absolute NCC stays modest even when the algorithm finds the
        // right basin. The gate-floor case is exercised by the other tests; here
        // we just want to assert SCALE recovery, so push FallbackNccFloor to 0
        // and read Scale off Metrics regardless of the NCC value.
        var texture = TestPatterns.RichNoise(width: 512, height: 512);
        var halved = TestPatterns.Resize(texture, 256, 256);
        var screenshot = TestPatterns.PasteInto(
            background: TestPatterns.UniformGray(640, 480, 128),
            foreground: halved,
            originX: 100, originY: 80);

        var refiner = BuildRefiner(new MapCalibrationLocateOptions { FallbackNccFloor = 0.0 });
        var result = refiner.Refine(screenshot, texture);

        result.AcceptedRect.Should().NotBeNull();
        result.Metrics!.Scale.Should().BeApproximately(0.5, 0.05);
        result.Metrics.Provenance.Should().Be(LocateProvenance.SobelPaddedPyramid);
    }

    [Fact]
    public void Rejects_when_inputs_are_unrelated_uniform_noise()
    {
        // Two independent RichNoise patches — no structural overlap.
        // NCC peak should sit below the default floor (0.20).
        //
        // mithril#1070: disables the blur-aware template path explicitly so
        // this regression-lock continues to exercise the floor-mechanism only.
        // With blur on at the production σ-curve, a heavy σ on small-template
        // rungs (at very low scale) can smooth the unrelated-noise response
        // enough to push the peak above 0.20 — that's expected (the gain over
        // mithril#1070's target case), but it's orthogonal to "is the floor
        // gate wired correctly?" — the question this test asks.
        var texture = TestPatterns.RichNoise(width: 256, height: 256, seed: 1);
        var screenshot = TestPatterns.RichNoise(width: 640, height: 480, seed: 2);

        var result = BuildRefiner(new MapCalibrationLocateOptions
        {
            RendererBlurEnabled = false,
        }).Refine(screenshot, texture);

        result.AcceptedRect.Should().BeNull(
            "unrelated noise → NCC below floor → engine surfaces low-confidence reject");
        result.RawFitRect.Should().NotBeNull("raw fit is recorded even on rejection");
        result.Metrics!.Provenance.Should().Be(LocateProvenance.SobelPaddedPyramid);
        result.Metrics.Confidence.Should().NotBeNull();
        result.Metrics.Confidence!.Value.Should().BeLessThan(0.20);
    }

    [Fact]
    public void Recovers_HogansKeep_223119_truth_from_corpus_bundle()
    {
        // mithril#1061: corpus regression — locks the converged algorithm against
        // drift on the canonical round-5 HogansKeep bundle. Truth per
        // @arthur-conde's GIMP alignment: (originX=126, originY=35, scale=0.7227).
        // The round-5 spike measured (127.5, 35.8, 0.720) at NCC 0.680, 134 ms.
        //
        // Capture + base texture both contain PG art (in-game UI screenshot +
        // decoded asset bytes); cannot be checked in. The fixture loader pulls
        // both from the developer's local %LocalAppData%/Mithril/{diagnostics,assets}/
        // when available. On a clean checkout the corpus is absent and the test
        // early-returns (xUnit v2 has no Assert.Skip without adding a package);
        // the regression still locks-in for anyone with the bundle locally,
        // notably the user when triaging fallback drift in this exact basement.
        //
        // See Fixtures/HogansKeep223119/README.md for how to populate the corpus
        // on a developer machine.
        if (!HogansKeepCorpusFixture.IsAvailable)
        {
            return;
        }

        var capture = HogansKeepCorpusFixture.LoadCapture();
        var texture = HogansKeepCorpusFixture.LoadTexture();
        texture.Should().NotBeNull("CachedBaseTextureProvider should resolve the locally-present texture");

        var refiner = BuildRefiner();
        var result = refiner.Refine(capture, texture!);

        result.AcceptedRect.Should().NotBeNull(
            "the converged sobel-padded-pyramid algorithm recovers this corpus bundle with NCC > 0.40");
        result.Metrics!.Provenance.Should().Be(LocateProvenance.SobelPaddedPyramid);
        result.Metrics.Confidence.Should().NotBeNull();
        result.Metrics.Confidence!.Value.Should().BeGreaterThan(0.40);

        // (originX, originY) recovered within ±2 px of GIMP truth.
        result.AcceptedRect!.OriginX.Should().BeInRange(124, 128);
        result.AcceptedRect.OriginY.Should().BeInRange(33, 37);

        // Scale recovered within ±0.005 of truth 0.7227.
        result.Metrics.Scale.Should().BeApproximately(0.7227, 0.005);
    }

    [Fact]
    public void Accepts_with_lowered_floor_when_only_a_weak_fit_exists()
    {
        // Same unrelated-noise scenario, but with the floor pushed to 0 — the refiner
        // accepts whatever the response map's best location is. This exercises the
        // confidence-floor knob (FallbackNccFloor) so a regression that hardcodes the
        // threshold would surface here.
        var texture = TestPatterns.RichNoise(width: 256, height: 256, seed: 1);
        var screenshot = TestPatterns.RichNoise(width: 640, height: 480, seed: 2);
        var refiner = BuildRefiner(new MapCalibrationLocateOptions { FallbackNccFloor = 0.0 });

        var result = refiner.Refine(screenshot, texture);

        result.AcceptedRect.Should().NotBeNull();
    }
}
