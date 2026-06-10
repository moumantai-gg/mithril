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

    /// <summary>
    /// PG ships icon sprites at native resolution (~256 px) but renders map icons at
    /// ~16 px on-screen. Production must rescale templates before sliding NCC, or
    /// L_t comes out mostly-zero (mithril#1022). With a native-res template + a
    /// pinned RenderSizePx, the production path's L_t must equal the field built
    /// from the same template manually rescaled to the same render size.
    /// </summary>
    [Fact]
    public void Production_rescales_native_resolution_templates_before_scoring()
    {
        const int W = 600, H = 400;
        const int RenderSizePx = 16;

        var texturePixels = SyntheticMap.MakeTexture(W, H, seed: 1022);
        var shotPixels = (byte[])texturePixels.Clone();
        // A few icon-shaped bright spots in the screenshot so the deviation has
        // predictable signal across the field.
        SyntheticMap.BlitTeardrop(shotPixels, W, H, anchorX: 120, anchorY: 90,  width: 16, height: 16, luminance: 220);
        SyntheticMap.BlitTeardrop(shotPixels, W, H, anchorX: 320, anchorY: 200, width: 16, height: 16, luminance: 220);
        SyntheticMap.BlitTeardrop(shotPixels, W, H, anchorX: 480, anchorY: 310, width: 16, height: 16, luminance: 220);

        var shot = new GrayImage(W, H, shotPixels);
        var tex  = new GrayImage(W, H, texturePixels);

        // Native-resolution template: 256x245 (matches the user's icon cache shape
        // for landmark_telepad in #1022). Exceeds IconRenderScaler.ScaleSearchThresholdPx = 64,
        // so RenderSized engages and (with pinnedSize) deterministically rescales to 16 px.
        const int NativeW = 256, NativeH = 245;
        var grayBytes  = SyntheticMap.MakeTexture(NativeW, NativeH, seed: 4242);
        var alphaBytes = new byte[NativeW * NativeH];
        for (int i = 0; i < alphaBytes.Length; i++) alphaBytes[i] = 255;
        var nativeTemplate = new IconTemplate(
            Name: "landmark_telepad_native",
            LandmarkType: "TeleportationPlatform",
            PivotX: 0.5, PivotY: 0.5,
            Gray:  new GrayImage(NativeW, NativeH, grayBytes),
            Alpha: new GrayImage(NativeW, NativeH, alphaBytes));
        var templates = new IconTemplateSet(new[] { nativeTemplate });

        // Path A — production path under test.
        // mithril#1123: BuildLikelihoodFieldsFromDeviation is now an instance method
        // (the synthesis-J rim-mask sink reads _logger). Construct a no-op engine
        // — the helper doesn't touch _detector / _gate / _options for this method.
        var engine = new MapCalibrationSolveEngine(
            detector: new DeviationBlobCalibrationDetector(),
            gate: new CalibrationConfidenceGate());
        var prodFields = engine.BuildLikelihoodFieldsFromDeviation(
            shot, tex, templates,
            typeFloor: 0.0,
            renderSizePx: RenderSizePx,
            rotate180: false,
            hooks: null);
        prodFields.Should().ContainKey("TeleportationPlatform");
        var prodField = prodFields["TeleportationPlatform"];

        // Path B — manual rescale + LoadDeviationAsField (mirrors the probe path).
        var rescaled = IconRenderScaler.RenderSized(shot, templates.Templates, threshold: 0.0, pinnedSize: RenderSizePx);
        rescaled.Should().HaveCount(1);
        var refTemplate = rescaled[0];

        var devBytes = new byte[W * H];
        for (int i = 0; i < devBytes.Length; i++)
        {
            int d = shot.Pixels[i] - tex.Pixels[i];
            devBytes[i] = d > 0 ? (byte)System.Math.Min(255, d) : (byte)0;
        }
        var devImage = new GrayImage(W, H, devBytes);
        var refField = IconLikelihoodField.LoadDeviationAsField(
            devImage, refTemplate,
            applyRimMask: true,
            devThr: IconLikelihoodField.DefaultDevThr);

        // Byte-equivalent.
        prodField.GetLength(0).Should().Be(refField.GetLength(0));
        prodField.GetLength(1).Should().Be(refField.GetLength(1));
        for (int y = 0; y < prodField.GetLength(0); y++)
        for (int x = 0; x < prodField.GetLength(1); x++)
        {
            prodField[y, x].Should().Be(refField[y, x],
                $"production path must rescale native-res templates so L_t matches a rescaled-template build at ({x},{y})");
        }
    }
}
