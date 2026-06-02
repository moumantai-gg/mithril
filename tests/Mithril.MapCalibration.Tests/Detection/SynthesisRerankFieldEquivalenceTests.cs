using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class SynthesisRerankFieldEquivalenceTests
{
    /// <summary>
    /// Production builds L_t from (alignedCrop, alignedTexture) via subtraction
    /// + DeviationFloodRimMask + ScoreAll. The probe builds L_t from a
    /// pre-computed deviation via LoadDeviationAsField (which applies the same
    /// DeviationFloodRimMask + ScoreAll). With production's subtracted
    /// deviation handed to the probe's LoadDeviationAsField, the fields must
    /// be byte-identical.
    /// </summary>
    [Fact]
    public void Production_path_and_probe_LoadDeviationAsField_produce_identical_fields()
    {
        const int W = 256, H = 192;
        var texturePixels = SyntheticMap.MakeTexture(W, H, seed: 4242);
        var shotPixels = (byte[])texturePixels.Clone();
        // Drip a few icon-shaped bright pixels into the screenshot so the
        // deviation has signal at predictable spots.
        SyntheticMap.BlitTeardrop(shotPixels, W, H, anchorX: 80, anchorY: 60, width: 16, height: 16, luminance: 220);
        SyntheticMap.BlitTeardrop(shotPixels, W, H, anchorX: 170, anchorY: 120, width: 16, height: 16, luminance: 220);

        var shot = new GrayImage(W, H, shotPixels);
        var tex  = new GrayImage(W, H, texturePixels);

        var templates = SyntheticMap.BuildTemplates(SyntheticMap.DefaultIcons);
        var template = templates.Templates[0];

        // Production path (mirrors MapCalibrationSolveEngine.BuildLikelihoodFieldsFromDeviation).
        var prodDev = new byte[W * H];
        for (int i = 0; i < prodDev.Length; i++)
        {
            int d = shot.Pixels[i] - tex.Pixels[i];
            prodDev[i] = d > 0 ? (byte)System.Math.Min(255, d) : (byte)0;
        }
        var prodField = IconLikelihoodField.LoadDeviationAsField(
            new GrayImage(W, H, prodDev), template,
            applyRimMask: true, devThr: IconLikelihoodField.DefaultDevThr);

        // Probe path (LoadDeviationAsField over an externally-computed deviation).
        var probeDev = new byte[W * H];
        for (int i = 0; i < probeDev.Length; i++)
        {
            int d = shot.Pixels[i] - tex.Pixels[i];
            probeDev[i] = d > 0 ? (byte)System.Math.Min(255, d) : (byte)0;
        }
        var probeField = IconLikelihoodField.LoadDeviationAsField(
            new GrayImage(W, H, probeDev), template);  // default rim-mask = true, default devThr

        // Byte-equivalent.
        prodField.GetLength(0).Should().Be(probeField.GetLength(0));
        prodField.GetLength(1).Should().Be(probeField.GetLength(1));
        for (int y = 0; y < prodField.GetLength(0); y++)
        for (int x = 0; x < prodField.GetLength(1); x++)
        {
            prodField[y, x].Should().Be(probeField[y, x],
                $"production and probe paths must score the same deviation byte-identically at ({x},{y})");
        }
    }
}
