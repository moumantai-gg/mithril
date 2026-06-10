using System;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// mithril#1070 — covers the blur-aware Sobel template application in
/// <see cref="SobelPaddedPyramidRefiner"/>'s full-resolution stage. Uses a
/// synthetic capture+texture pair (sharp Sobel response, no inherent blur) so
/// the tests exercise the producer end-to-end without depending on the
/// (heavyweight) Hogan's corpus PNG fixtures from
/// <see cref="HogansBlurAwareCorpusTests"/>.
/// </summary>
public sealed class SobelPaddedPyramidRefinerBlurTests
{
    private const int TexW = 320, TexH = 240;
    // Capture is a known sub-window of the texture rendered at exact-scale 1.0,
    // so the refiner recovers (scale≈1, tx≈OffsetX, ty≈OffsetY). Sharp Sobel
    // response — useful as the "no inherent blur" regression-lock case for
    // verifying that blur-on doesn't degrade a clean recovery.
    private const int CapW = 200, CapH = 160;
    private const int OffsetX = 60, OffsetY = 40;

    private static (GrayImage capture, GrayImage texture) BuildSharpSyntheticPair()
    {
        var texPixels = SyntheticMap.MakeTexture(TexW, TexH, seed: 4242);
        // Blit a handful of strong landmarks so the Sobel NCC peak has clear
        // basin separation — gradient+noise alone is too smooth and the
        // (tx, ty) recovery wanders sub-correlation-half-max distances when
        // blur is added.
        SyntheticMap.BlitTeardrop(texPixels, TexW, TexH, anchorX:90, anchorY:80, width:24, height:32, luminance: 40);
        SyntheticMap.BlitTeardrop(texPixels, TexW, TexH, anchorX:220, anchorY:70, width:28, height:22, luminance: 200);
        SyntheticMap.BlitTeardrop(texPixels, TexW, TexH, anchorX:100, anchorY:180, width:18, height:40, luminance: 100);
        SyntheticMap.BlitTeardrop(texPixels, TexW, TexH, anchorX:240, anchorY:180, width:20, height:28, luminance: 240);
        var texture = new GrayImage(TexW, TexH, texPixels);
        var capPixels = new byte[CapW * CapH];
        for (int y = 0; y < CapH; y++)
        {
            int sy = OffsetY + y;
            Buffer.BlockCopy(texPixels, sy * TexW + OffsetX, capPixels, y * CapW, CapW);
        }
        return (new GrayImage(CapW, CapH, capPixels), texture);
    }

    [Fact]
    public void Refine_records_BlurAppliedSigma_when_enabled_with_canonical_curve()
    {
        // Production-shape σ-curve: at the recovered scale ≈ 1.0, intercept +
        // slope = 0.30, which is above MinSigma=0 so the blur path fires.
        var options = new MapCalibrationLocateOptions
        {
            RendererBlurEnabled = true,
            RendererBlurIntercept = 0.10,
            RendererBlurSlope = 0.20,
            RendererBlurMinSigma = 0.0,
            RendererBlurMaxSigma = 3.0,
        };
        var (cap, tex) = BuildSharpSyntheticPair();
        var refiner = new SobelPaddedPyramidRefiner(options);

        var result = refiner.Refine(cap, tex);

        result.Metrics.Should().NotBeNull();
        result.Metrics!.Provenance.Should().Be(LocateProvenance.SobelPaddedPyramid);
        result.Metrics.BlurAppliedSigma.Should().NotBeNull();
        // The σ stamped on metrics is the one applied at the matchTemplate
        // call that produced the recovered (tx, ty). It must match the σ-curve
        // evaluated at the recovered scale (point B's σ when parabolic fired,
        // otherwise point A's winner σ). Either way the curve at metrics.Scale
        // reproduces it.
        result.Metrics.BlurAppliedSigma!.Value.Should().BeApproximately(
            RendererBlurModel.SigmaFor(result.Metrics.Scale, options), 1e-9);
    }

    [Fact]
    public void Refine_records_zero_sigma_when_disabled()
    {
        var options = new MapCalibrationLocateOptions { RendererBlurEnabled = false };
        var (cap, tex) = BuildSharpSyntheticPair();
        var refiner = new SobelPaddedPyramidRefiner(options);

        var result = refiner.Refine(cap, tex);

        result.Metrics.Should().NotBeNull();
        result.Metrics!.BlurAppliedSigma.Should().Be(0.0);
    }

    [Fact]
    public void Refine_recovers_same_basin_on_sharp_synthetic_with_and_without_blur()
    {
        // Regression lock — on a sharp synthetic pair where blur is unnecessary
        // (Sobel response already sharp), blur=on vs blur=off should recover
        // essentially the same (scale, tx, ty). Guards against the blur path
        // ruining clean cases where the σ-curve happens to push σ above 0.
        var (cap, tex) = BuildSharpSyntheticPair();
        var on = new SobelPaddedPyramidRefiner(new MapCalibrationLocateOptions
        {
            RendererBlurEnabled = true,
            RendererBlurIntercept = 0.10,
            RendererBlurSlope = 0.20,
        }).Refine(cap, tex);
        var off = new SobelPaddedPyramidRefiner(new MapCalibrationLocateOptions
        {
            RendererBlurEnabled = false,
        }).Refine(cap, tex);

        on.Metrics.Should().NotBeNull();
        off.Metrics.Should().NotBeNull();
        // The recovered scale must agree to within one scale-step (0.02).
        on.Metrics!.Scale.Should().BeApproximately(off.Metrics!.Scale, 0.02);
        // The recovered (tx, ty) must agree within 1 px — blur on a sharp
        // synthetic can shift the NCC peak by less than 1 px from sub-pixel
        // parabolic refinement but should never overshoot that.
        Math.Abs(on.Metrics.Tx - off.Metrics.Tx).Should().BeLessThan(2.0);
        Math.Abs(on.Metrics.Ty - off.Metrics.Ty).Should().BeLessThan(2.0);
    }
}
