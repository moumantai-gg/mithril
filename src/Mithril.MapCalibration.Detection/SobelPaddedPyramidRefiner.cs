using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection.Internal;
using OpenCvSharp;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// <see cref="IMapRegionRefiner"/> using Sobel gradient magnitude + zero
/// padding (default 100 px) + 3-level Gaussian pyramid + matchTemplate
/// (CCoeffNormed) + parabolic scale + sub-pixel translation refinement. The
/// mithril#1061 fallback for sparse-interior maps where
/// <see cref="FeatureMatchingRefiner"/> (ORB+Lowe) produces fewer than 4 Lowe
/// survivors.
///
/// <para><b>Algorithm.</b> See
/// <c>docs/planning/map-calibration-sparse-locate-fallback-1061/spec.md</c> §2.
/// All knobs (scale ladder bounds, pad, min scaled dims, NCC floor) come from
/// <see cref="MapCalibrationLocateOptions"/> so the user can tune without
/// recompile (the options round-trip via <c>map-calibration-locate.json</c>).</para>
///
/// <para><b>Gate.</b> Refined NCC &lt; <see cref="MapCalibrationLocateOptions.FallbackNccFloor"/>
/// → <see cref="MapRegionRefineResult"/> with null <c>AcceptedRect</c>; the
/// <c>RawFitRect</c> + <c>Metrics</c> are still populated so the bundle and the
/// engine's reason copy are self-triaging.</para>
///
/// <para><b>Fail-soft.</b> An <see cref="OpenCVException"/> from any OpenCv
/// call returns <see cref="MapRegionRefineResult.None"/> (matches the FM
/// refiner's safe-degrade contract).</para>
/// </summary>
public sealed class SobelPaddedPyramidRefiner : IMapRegionRefiner
{
    private readonly MapCalibrationLocateOptions _options;
    private readonly ILogger? _logger;

    public SobelPaddedPyramidRefiner(
        MapCalibrationLocateOptions options,
        ILogger<SobelPaddedPyramidRefiner>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public MapRegionRefineResult Refine(GrayImage capturedGray, GrayImage baseTexture)
    {
        try
        {
            return RefineCore(capturedGray, baseTexture);
        }
        catch (OpenCVException ex)
        {
            _logger?.LogWarning(ex, "Sobel-padded-pyramid locate: OpenCV failure. Safe-degrade.");
            return MapRegionRefineResult.None;
        }
    }

    private MapRegionRefineResult RefineCore(GrayImage capturedGray, GrayImage baseTexture)
    {
        int pad = _options.FallbackPadPx;
        double scaleMin = _options.ScaleMin;
        double scaleMax = _options.ScaleMax;
        double scaleStep = _options.ScaleStep;
        int minDimFull = _options.MinScaledDim;
        int minDimHalf = _options.MinScaledDimHalf;
        int minDimCoarse = _options.MinScaledDimCoarse;

        using var capMat = ToMat8U(capturedGray);
        using var texMat = ToMat8U(baseTexture);
        using var capSobel = SobelMagnitudeHelpers.SobelMagnitude8U(capMat);
        using var texSobel = SobelMagnitudeHelpers.SobelMagnitude8U(texMat);
        using var capPadded = new Mat();
        Cv2.CopyMakeBorder(capSobel, capPadded, pad, pad, pad, pad,
            BorderTypes.Constant, Scalar.All(0));

        using var capL1 = new Mat(); Cv2.PyrDown(capPadded, capL1);
        using var capL2 = new Mat(); Cv2.PyrDown(capL1, capL2);
        using var texL1 = new Mat(); Cv2.PyrDown(texSobel, texL1);
        using var texL2 = new Mat(); Cv2.PyrDown(texL1, texL2);

        // Stage 1: full scale ladder at quarter resolution.
        if (!TryFullLadder(capL2, texL2, minDimCoarse, scaleMin, scaleMax, scaleStep, out double l2Scale))
            return MapRegionRefineResult.None;

        // Stage 2: narrow ladder at half resolution centred on L2 winner.
        if (!TryNarrowLadder(capL1, texL1, l2Scale, minDimHalf, scaleStep, out double l1Scale))
            return MapRegionRefineResult.None;

        // Stage 3: narrow ladder at full resolution centred on L1 winner.
        var fineLadder = NarrowLadderWithLoc(capPadded, texSobel, l1Scale, minDimFull, scaleStep);
        if (fineLadder.Count == 0)
            return MapRegionRefineResult.None;

        int fineIdx = ArgMax(fineLadder);
        var fineWinner = fineLadder[fineIdx];
        double refinedScale = fineWinner.Scale;
        double refinedTx = fineWinner.Loc.X - pad;
        double refinedTy = fineWinner.Loc.Y - pad;
        double refinedNcc = fineWinner.Score;

        // Stage 4: parabolic scale refinement around the fine winner, then sub-pixel
        // translation refinement on the re-matched response map at the refined scale.
        if (fineIdx > 0 && fineIdx < fineLadder.Count - 1)
        {
            double y1 = fineLadder[fineIdx - 1].Score;
            double y2 = fineLadder[fineIdx].Score;
            double y3 = fineLadder[fineIdx + 1].Score;
            double denom = y1 - 2 * y2 + y3;
            if (denom < -1e-9)
            {
                double subStep = 0.5 * (y1 - y3) / denom;
                if (Math.Abs(subStep) <= 1.0)
                {
                    double candidate = fineWinner.Scale + scaleStep * subStep;
                    int sw = (int)Math.Round(texSobel.Width * candidate);
                    int sh = (int)Math.Round(texSobel.Height * candidate);
                    if (sw >= minDimFull && sh >= minDimFull
                        && sw <= capPadded.Width && sh <= capPadded.Height)
                    {
                        using var scaled = new Mat();
                        Cv2.Resize(texSobel, scaled, new Size(sw, sh),
                            interpolation: InterpolationFlags.Area);
                        using var result = new Mat();
                        Cv2.MatchTemplate(capPadded, scaled, result, TemplateMatchModes.CCoeffNormed);
                        Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
                        var (sdx, sdy) = SobelMagnitudeHelpers.RefineLocationSubPixel(result, maxLoc);
                        refinedScale = candidate;
                        refinedTx = maxLoc.X + sdx - pad;
                        refinedTy = maxLoc.Y + sdy - pad;
                        refinedNcc = maxVal;
                    }
                }
            }
        }

        int originX = (int)Math.Round(refinedTx);
        int originY = (int)Math.Round(refinedTy);
        int width = (int)Math.Round(texSobel.Width * refinedScale);
        int height = (int)Math.Round(texSobel.Height * refinedScale);

        var rawFit = new MapRect(
            OriginX: originX, OriginY: originY,
            Width: width, Height: height,
            TextureWidth: baseTexture.Width,
            TextureHeight: baseTexture.Height);

        var metrics = new LocateMetrics(
            InlierCount: 0, CandidateCount: 0, InlierRatio: 0,
            Scale: refinedScale, RotationDegrees: 0, Mirror: false,
            Tx: refinedTx, Ty: refinedTy, ResidualPixels: 0,
            Provenance: LocateProvenance.SobelPaddedPyramid,
            Confidence: refinedNcc);

        if (refinedNcc < _options.FallbackNccFloor)
        {
            _logger?.LogInformation(
                "Sobel-padded-pyramid locate: rejected — NCC={Ncc:0.000} < floor={Floor:0.000} "
                + "(scale={Scale:0.000}, tx={Tx:0.0}, ty={Ty:0.0}).",
                refinedNcc, _options.FallbackNccFloor, refinedScale, refinedTx, refinedTy);
            return new MapRegionRefineResult(
                AcceptedRect: null, RawFitRect: rawFit, Metrics: metrics);
        }

        _logger?.LogInformation(
            "Sobel-padded-pyramid locate: accepted — NCC={Ncc:0.000}, scale={Scale:0.000}, "
            + "tx={Tx:0.0}, ty={Ty:0.0}.",
            refinedNcc, refinedScale, refinedTx, refinedTy);
        return new MapRegionRefineResult(
            AcceptedRect: rawFit, RawFitRect: rawFit, Metrics: metrics);
    }

    private static bool TryFullLadder(
        Mat cap, Mat tex, int minDim,
        double scaleMin, double scaleMax, double scaleStep,
        out double bestScale)
    {
        bestScale = 0;
        var ladder = new List<(double S, double Score)>(64);
        for (double s = scaleMin; s <= scaleMax + 1e-6; s += scaleStep)
        {
            int sw = (int)Math.Round(tex.Width * s);
            int sh = (int)Math.Round(tex.Height * s);
            if (sw < minDim || sh < minDim || sw > cap.Width || sh > cap.Height) continue;
            using var scaled = new Mat();
            Cv2.Resize(tex, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
            using var result = new Mat();
            Cv2.MatchTemplate(cap, scaled, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out _);
            ladder.Add((s, maxVal));
        }
        if (ladder.Count == 0) return false;
        int idx = 0;
        for (int i = 1; i < ladder.Count; i++)
            if (ladder[i].Score > ladder[idx].Score) idx = i;
        bestScale = ladder[idx].S;
        return true;
    }

    private static bool TryNarrowLadder(
        Mat cap, Mat tex, double centreScale, int minDim, double scaleStep,
        out double bestScale)
    {
        bestScale = 0;
        var ladder = new List<(double S, double Score)>(8);
        for (double s = centreScale - 2 * scaleStep; s <= centreScale + 2 * scaleStep + 1e-6; s += scaleStep)
        {
            if (s <= 0) continue;
            int sw = (int)Math.Round(tex.Width * s);
            int sh = (int)Math.Round(tex.Height * s);
            if (sw < minDim || sh < minDim || sw > cap.Width || sh > cap.Height) continue;
            using var scaled = new Mat();
            Cv2.Resize(tex, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
            using var result = new Mat();
            Cv2.MatchTemplate(cap, scaled, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out _);
            ladder.Add((s, maxVal));
        }
        if (ladder.Count == 0) return false;
        int idx = 0;
        for (int i = 1; i < ladder.Count; i++)
            if (ladder[i].Score > ladder[idx].Score) idx = i;
        bestScale = ladder[idx].S;
        return true;
    }

    private static List<(double Scale, double Score, Point Loc)> NarrowLadderWithLoc(
        Mat cap, Mat tex, double centreScale, int minDim, double scaleStep)
    {
        var ladder = new List<(double Scale, double Score, Point Loc)>(8);
        for (double s = centreScale - 2 * scaleStep; s <= centreScale + 2 * scaleStep + 1e-6; s += scaleStep)
        {
            if (s <= 0) continue;
            int sw = (int)Math.Round(tex.Width * s);
            int sh = (int)Math.Round(tex.Height * s);
            if (sw < minDim || sh < minDim || sw > cap.Width || sh > cap.Height) continue;
            using var scaled = new Mat();
            Cv2.Resize(tex, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
            using var result = new Mat();
            Cv2.MatchTemplate(cap, scaled, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
            ladder.Add((s, maxVal, maxLoc));
        }
        return ladder;
    }

    private static int ArgMax(List<(double Scale, double Score, Point Loc)> ladder)
    {
        int idx = 0;
        for (int i = 1; i < ladder.Count; i++)
            if (ladder[i].Score > ladder[idx].Score) idx = i;
        return idx;
    }

    private static Mat ToMat8U(GrayImage g)
        => Mat.FromPixelData(g.Height, g.Width, MatType.CV_8UC1, g.Pixels).Clone();
}
