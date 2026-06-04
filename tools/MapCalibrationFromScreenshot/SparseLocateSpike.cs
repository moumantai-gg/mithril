// mithril#1061 spike — comparison of locate-stage algorithms on sparse-interior
// maps. Reproduces the 3 failed Map_GoblinDungeon attempts from 2026-06-03 and
// runs them against a panel of OpenCV algorithms (core + contrib). Eltibule
// 06:14 accept is included as a control: any algorithm that produces a sane
// answer for GoblinDungeon while also matching the production answer for
// Eltibule is a real candidate; one that nails GoblinDungeon but breaks the
// outdoor control is not.
//
// Invocation: `dotnet run --project tools/MapCalibrationFromScreenshot -- --phase sparse-locate-spike`
//
// No CLI args: all inputs are hardcoded paths under %LocalAppData%/Mithril/.
// Results print to stdout; per-algorithm overlay PNGs land in
// %TEMP%/sparse-locate-spike/. Spike file — delete with the package references
// when the comparison is no longer needed.

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using OpenCvSharp;
using OpenCvSharp.XImgProc;

namespace Mithril.Tools.MapCalibrationFromScreenshot;

internal static class SparseLocateSpike
{
    private const double LoweRatio = 0.75;
    private const double ScaleMin = 0.20;
    private const double ScaleMax = 1.20;
    private const double ScaleStep = 0.02;

    public static int Run()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var calibRoot = Path.Combine(localApp, "Mithril", "diagnostics", "calibration");
        var assetsDir = Path.Combine(localApp, "Mithril", "assets");
        var outDir = Path.Combine(Path.GetTempPath(), "sparse-locate-spike");
        Directory.CreateDirectory(outDir);

        var bundles = DiscoverBundles(calibRoot, assetsDir);
        if (bundles.Count == 0)
        {
            Console.WriteLine($"!! no usable bundles under {calibRoot}");
            return 1;
        }
        Console.WriteLine($"discovered {bundles.Count} bundle(s)");

        Console.WriteLine($"=== SparseLocateSpike — {DateTime.UtcNow:O} ===");
        Console.WriteLine($"out dir: {outDir}");
        Console.WriteLine();

        foreach (var b in bundles)
        {
            if (!File.Exists(b.ScreenshotPath))
            {
                Console.WriteLine($"!! missing screenshot for {b.Name}: {b.ScreenshotPath}");
                continue;
            }
            using var capColor = Cv2.ImRead(b.ScreenshotPath, ImreadModes.Color);
            using var capFull = new Mat();
            Cv2.CvtColor(capColor, capFull, ColorConversionCodes.BGR2GRAY);
            using var cap = b.CropRect is Rect r ? new Mat(capFull, ClampRectToMat(r, capFull)) : capFull.Clone();
            using var tex = LoadBaseTextureGray(assetsDir, b.TextureKey);
            if (tex is null || tex.Empty())
            {
                Console.WriteLine($"!! missing/empty base texture for {b.TextureKey}");
                continue;
            }

            Console.WriteLine($"--- {b.Name} ---  capture={cap.Width}x{cap.Height}  texture={tex.Width}x{tex.Height}");
            Console.WriteLine($"  {"alg",-26} {"tx",8} {"ty",8} {"sc",8} {"conf",10} {"ms",7}   note");

            // Round 1 baseline (production parity).
            RunOne("ORB+Lowe",            () => FeatureMatch(cap, tex, FeatureKind.Orb),    b, outDir);
            // Round 1 also-rans — kept for tx/ty/scale comparison vs the new methods.
            RunOne("matchTemplate (edge)",() => TemplateMatchScaleLadder(cap, tex, edgesOnly: true),  b, outDir);
            RunOne("matchTemplate (edge,pyr)", () => TemplateMatchPyramidEdge(cap, tex),              b, outDir);
            // Exploration: Sobel gradient magnitude instead of binary Canny.
            // Smooth continuous-valued inputs may handle renderer-blur asymmetry
            // more gracefully than the gradient-of-binary coincidence.
            RunOne("matchTemplate (sobel,mag)", () => TemplateMatchSobelMagnitude(cap, tex),          b, outDir);
            // Anisotropic refinement on top of the isotropic Sobel winner —
            // tests whether some PG maps actually display with sx != sy.
            RunOne("matchTemplate (sobel,aniso)", () => TemplateMatchSobelAniso(cap, tex),            b, outDir);
            // Extended scale range — when scale > capture/texture fit limit,
            // invert matchTemplate so the capture is the template and the
            // upscaled texture is the image. Necessary for high-zoom captures
            // where the texture is bigger than the screenshot.
            RunOne("matchTemplate (sobel,ext)", () => TemplateMatchSobelExtended(cap, tex),          b, outDir);
            // Padded matchTemplate — pad the capture with zeros so the template
            // can be placed at negative offsets or past the far edge. Handles
            // cases where the displayed texture extends past the captured panel,
            // which standard matchTemplate cannot represent.
            RunOne("matchTemplate (sobel,pad)", () => TemplateMatchSobelPadded(cap, tex),            b, outDir);
            // Exploration: 3-level Gaussian pyramid (full / half / quarter).
            // Tests whether deeper pyramid gives more runtime headroom and/or
            // better basin-of-attraction at moderate scales.
            RunOne("matchTemplate (edge,pyr3)", () => TemplateMatchPyramid3Edge(cap, tex),            b, outDir);
            RunOne("Chamfer (dist-NCC, broken)", () => ChamferMatchScaleLadder(cap, tex),   b, outDir);
            // Round 2 new methods: subset-aware matchers that score correspondences
            // without penalizing un-revealed pixels.
            RunOne("Chamfer (Borgefors)", () => BorgeforsChamfer(cap, tex),                 b, outDir);
            RunOne("GHT (edge points)",   () => GeneralizedHough(cap, tex),                 b, outDir);
            Console.WriteLine();
        }

        Console.WriteLine("=== done ===");
        return 0;
    }

    // ---- harness ----

    private sealed record Bundle(string Name, string ScreenshotPath, string TextureKey, Rect? CropRect);

    // Clamp a crop rect to a Mat's bounds. The production mapRect occasionally
    // overshoots the screenshot by a few pixels (e.g. AreaEltibule 20260602-031130
    // mapRect height extends 13 px below the capture); without this, the
    // Mat(roi) ctor throws OpenCVException.
    private static Rect ClampRectToMat(Rect r, Mat m)
    {
        int x = Math.Max(0, r.X);
        int y = Math.Max(0, r.Y);
        int w = Math.Min(r.Width, m.Cols - x);
        int h = Math.Min(r.Height, m.Rows - y);
        return new Rect(x, y, Math.Max(1, w), Math.Max(1, h));
    }

    // Auto-discover bundles under %LocalAppData%/Mithril/diagnostics/calibration/.
    // Directory naming convention (from CalibrationAttemptContext): the area key
    // is the prefix up to the first '-', the outcome is the trailing
    // '-<outcome>' suffix. We include:
    //   - everything ending in -rejected-map-not-located (these are the
    //     locate-stage failures the spike is built to investigate)
    //   - any -accepted bundle (controls). For these we also emit a second
    //     synthetic bundle cropped to 04-maprect.json — the apples-to-apples
    //     easy-regime check.
    // Bundles whose base texture isn't on disk (sidecar failed) are skipped.
    private static List<Bundle> DiscoverBundles(string calibRoot, string assetsDir)
    {
        var bundles = new List<Bundle>();
        if (!Directory.Exists(calibRoot)) return bundles;

        foreach (var dir in Directory.EnumerateDirectories(calibRoot).OrderBy(d => d))
        {
            var name = Path.GetFileName(dir);
            var screenshotPath = Path.Combine(dir, "02-screenshot-raw.png");
            if (!File.Exists(screenshotPath)) continue;

            // Parse "<areaKey>-<yyyyMMdd>-<HHmmss>-<ms>-<outcome>" with the
            // first 4 dash-separated chunks fixed.
            var parts = name.Split('-');
            if (parts.Length < 5) continue;
            var areaKey = parts[0];
            var outcome = string.Join("-", parts.Skip(4));

            // Filter: only the locate-stage failures + accepted controls.
            bool isInteresting =
                outcome.StartsWith("rejected-map-not-located", StringComparison.Ordinal)
                || outcome == "accepted";
            if (!isInteresting) continue;

            // Verify base texture exists on disk.
            if (!File.Exists(Path.Combine(assetsDir, $"map-texture-{areaKey}.bin")))
                continue;

            // Pretty timestamp from parts[1] + parts[2].
            string label = $"{areaKey}-{parts[1]}-{parts[2]} ({outcome.Substring(0, Math.Min(outcome.Length, 32))})";
            bundles.Add(new Bundle(label, screenshotPath, areaKey, null));

            // Accepted bundles get a second cropped entry from 04-maprect.json.
            if (outcome == "accepted")
            {
                var mapRectPath = Path.Combine(dir, "04-maprect.json");
                if (File.Exists(mapRectPath))
                {
                    try
                    {
                        using var s = File.OpenRead(mapRectPath);
                        var mr = JsonDocument.Parse(s).RootElement;
                        int ox = mr.GetProperty("originX").GetInt32();
                        int oy = mr.GetProperty("originY").GetInt32();
                        int w = mr.GetProperty("width").GetInt32();
                        int h = mr.GetProperty("height").GetInt32();
                        bundles.Add(new Bundle($"{label} [cropped to mapRect]", screenshotPath, areaKey, new Rect(ox, oy, w, h)));
                    }
                    catch { /* ignore malformed maprect */ }
                }
            }
        }
        return bundles;
    }

    private sealed record SpikeResult(
        double? Tx, double? Ty, double? Scale, double Confidence, string Note,
        Mat? Overlay = null, Mat? TransparentEdges = null);

    private static void RunOne(string name, Func<SpikeResult> body, Bundle b, string outDir)
    {
        var sw = Stopwatch.StartNew();
        SpikeResult r;
        try { r = body(); }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"  {name,-26} {"-",8} {"-",8} {"-",8} {"-",10} {sw.ElapsedMilliseconds,7}   EX: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        sw.Stop();
        string tx = r.Tx is double txv ? txv.ToString("0.0") : "-";
        string ty = r.Ty is double tyv ? tyv.ToString("0.0") : "-";
        string sc = r.Scale is double scv ? scv.ToString("0.000") : "-";
        Console.WriteLine($"  {name,-26} {tx,8} {ty,8} {sc,8} {r.Confidence,10:0.0000} {sw.ElapsedMilliseconds,7}   {r.Note}");

        if (r.Overlay is { } ov)
        {
            var safe = b.Name.Replace(':', '-').Replace(' ', '_').Replace('(', '_').Replace(')', '_');
            var alg = name.Replace(' ', '_').Replace('(', '_').Replace(')', '_').Replace('+', '_');
            Cv2.ImWrite(Path.Combine(outDir, $"{safe}__{alg}.png"), ov);
            ov.Dispose();
        }
        if (r.TransparentEdges is { } edges)
        {
            var safe = b.Name.Replace(':', '-').Replace(' ', '_').Replace('(', '_').Replace(')', '_');
            var alg = name.Replace(' ', '_').Replace('(', '_').Replace(')', '_').Replace('+', '_');
            Cv2.ImWrite(Path.Combine(outDir, $"{safe}__{alg}__edges.png"), edges);
            edges.Dispose();
        }
    }

    // ---- base-texture loader (mirrors CachedBaseTextureProvider's deflate format) ----

    private static Mat? LoadBaseTextureGray(string assetsDir, string textureKey)
    {
        var manifestPath = Path.Combine(assetsDir, $"map-texture-{textureKey}.json");
        var blobPath = Path.Combine(assetsDir, $"map-texture-{textureKey}.bin");
        if (!File.Exists(manifestPath) || !File.Exists(blobPath)) return null;

        using var s = File.OpenRead(manifestPath);
        var manifest = JsonDocument.Parse(s).RootElement;
        int w = manifest.GetProperty("width").GetInt32();
        int h = manifest.GetProperty("height").GetInt32();

        using var raw = File.OpenRead(blobPath);
        using var deflate = new DeflateStream(raw, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        deflate.CopyTo(ms);
        var pixels = ms.ToArray();
        if (pixels.Length != w * h) return null;

        var mat = Mat.FromPixelData(h, w, MatType.CV_8UC1, pixels).Clone();
        return mat;
    }

    // ---- feature-match family (ORB / AKAZE / SIFT + Lowe + RANSAC partial-affine) ----

    private enum FeatureKind { Orb, Akaze, Sift }

    private static SpikeResult FeatureMatch(Mat cap, Mat tex, FeatureKind kind)
    {
        Feature2D detector;
        NormTypes norm;
        switch (kind)
        {
            case FeatureKind.Orb:
                detector = ORB.Create(nFeatures: 8000);
                norm = NormTypes.Hamming;
                break;
            case FeatureKind.Akaze:
                detector = AKAZE.Create();
                norm = NormTypes.Hamming;
                break;
            case FeatureKind.Sift:
                // OpenCvSharp 4.10 namespace for SIFT is unclear from the spike sketch;
                // drop it — AKAZE is the main candidate at this tier anyway.
                throw new NotImplementedException("SIFT skipped — AKAZE is the focal candidate");
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        using var capDesc = new Mat();
        using var texDesc = new Mat();
        detector.DetectAndCompute(cap, null, out var capKp, capDesc);
        detector.DetectAndCompute(tex, null, out var texKp, texDesc);
        detector.Dispose();

        if (capDesc.Rows < 2 || texDesc.Rows < 2)
            return new SpikeResult(null, null, null, 0, $"capKp={capKp.Length} texKp={texKp.Length} — too few descriptors");

        using var matcher = new BFMatcher(norm, crossCheck: false);
        var knn = matcher.KnnMatch(capDesc, texDesc, k: 2);
        var survivors = knn
            .Where(p => p.Length == 2 && p[0].Distance < LoweRatio * p[1].Distance)
            .Select(p => p[0])
            .ToArray();

        if (survivors.Length < 4)
            return new SpikeResult(null, null, null, 0,
                $"capKp={capKp.Length} texKp={texKp.Length} survivors={survivors.Length} (need >=4)");

        var srcPts = survivors.Select(m => capKp[m.QueryIdx].Pt).ToArray();
        var dstPts = survivors.Select(m => texKp[m.TrainIdx].Pt).ToArray();
        using var inlierMask = new Mat();
        using var transform = Cv2.EstimateAffinePartial2D(
            InputArray.Create(srcPts), InputArray.Create(dstPts),
            inlierMask, RobustEstimationAlgorithms.RANSAC, 3.0);

        if (transform is null || transform.Empty())
            return new SpikeResult(null, null, null, 0,
                $"survivors={survivors.Length} — RANSAC returned empty transform");

        var idx = inlierMask.GetGenericIndexer<byte>();
        int inliers = 0;
        for (int i = 0; i < inlierMask.Rows; i++) if (idx[i, 0] != 0) inliers++;

        double a = transform.At<double>(0, 0);
        double b = transform.At<double>(0, 1);
        double tx = transform.At<double>(0, 2);
        double ty = transform.At<double>(1, 2);
        double scale = Math.Sqrt(a * a + b * b);
        double rotDeg = Math.Atan2(b, a) * 180.0 / Math.PI;

        double inlierRatio = survivors.Length > 0 ? (double)inliers / survivors.Length : 0;
        return new SpikeResult(tx, ty, scale, inlierRatio,
            $"kp={capKp.Length}/{texKp.Length} surv={survivors.Length} inliers={inliers} rot={rotDeg:0.00}°");
    }

    // ---- sub-pixel translation refinement (2D parabolic on NCC response map) ----
    //
    // Given an NCC response map and the integer peak location from MinMaxLoc,
    // fit a 1D parabola on each axis through the peak's immediate neighbors and
    // return the sub-pixel offset of each parabola's vertex from the integer
    // peak. Final translation = (peakLoc.X + dx, peakLoc.Y + dy).
    //
    // Returns (0, 0) at boundary peaks (no neighbors on one side) and clamps to
    // |dx|, |dy| <= 1.0 to avoid runaway when the curvature is near-zero.
    private static (double dx, double dy) RefineLocationSubPixel(Mat ncc, Point peakLoc)
    {
        int px = peakLoc.X, py = peakLoc.Y;
        if (px <= 0 || py <= 0 || px >= ncc.Cols - 1 || py >= ncc.Rows - 1)
            return (0, 0);
        var idx = ncc.GetGenericIndexer<float>();
        double c = idx[py, px];
        double left = idx[py, px - 1], right = idx[py, px + 1];
        double up = idx[py - 1, px], down = idx[py + 1, px];
        double denomX = left - 2 * c + right;
        double denomY = up - 2 * c + down;
        double dx = denomX < -1e-9 ? 0.5 * (left - right) / denomX : 0;
        double dy = denomY < -1e-9 ? 0.5 * (up - down) / denomY : 0;
        return (Math.Max(-1.0, Math.Min(1.0, dx)), Math.Max(-1.0, Math.Min(1.0, dy)));
    }

    // ---- matchTemplate over scale ladder, optional edge preprocessing ----

    private static SpikeResult TemplateMatchScaleLadder(Mat cap, Mat tex, bool edgesOnly)
    {
        Mat capPrep, texPrep;
        if (edgesOnly)
        {
            capPrep = new Mat(); Cv2.Canny(cap, capPrep, 50, 150);
            texPrep = new Mat(); Cv2.Canny(tex, texPrep, 50, 150);
        }
        else { capPrep = cap; texPrep = tex; }

        // Coarse search collects (scale, NCC) for every evaluated rung so we can
        // do parabolic peak refinement on the winning scale's neighborhood.
        var ladder = new List<(double Scale, double Score, Point Loc)>(64);
        try
        {
            for (double s = ScaleMin; s <= ScaleMax + 1e-6; s += ScaleStep)
            {
                int sw = (int)Math.Round(texPrep.Width * s);
                int sh = (int)Math.Round(texPrep.Height * s);
                if (sw < 20 || sh < 20 || sw > capPrep.Width || sh > capPrep.Height) continue;

                using var scaled = new Mat();
                Cv2.Resize(texPrep, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
                using var result = new Mat();
                Cv2.MatchTemplate(capPrep, scaled, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
                ladder.Add((s, maxVal, maxLoc));
            }
        }
        finally
        {
            if (edgesOnly) { capPrep.Dispose(); texPrep.Dispose(); }
        }

        if (ladder.Count == 0)
            return new SpikeResult(null, null, null, 0, "no valid scale (texture too large at all rungs)");

        // Discrete winner.
        int bestIdx = 0;
        for (int i = 1; i < ladder.Count; i++)
            if (ladder[i].Score > ladder[bestIdx].Score) bestIdx = i;
        var coarse = ladder[bestIdx];

        // Parabolic peak refinement: fit a parabola through (y_{i-1}, y_i, y_{i+1})
        // along the scale axis; vertex offset gives the sub-step scale. Only valid
        // when the winner has neighbors AND the curvature is concave-down (true
        // peak, not edge of the search). Falls back to coarse winner otherwise.
        double refinedScale = coarse.Scale;
        double refinedTx = coarse.Loc.X;
        double refinedTy = coarse.Loc.Y;
        double refinedScore = coarse.Score;
        string refineNote = "discrete";

        if (bestIdx > 0 && bestIdx < ladder.Count - 1)
        {
            double y1 = ladder[bestIdx - 1].Score;
            double y2 = ladder[bestIdx].Score;
            double y3 = ladder[bestIdx + 1].Score;
            double denom = y1 - 2 * y2 + y3;
            if (denom < -1e-9)  // concave-down peak
            {
                double subStep = 0.5 * (y1 - y3) / denom;  // in units of step
                if (Math.Abs(subStep) <= 1.0)
                {
                    refinedScale = coarse.Scale + ScaleStep * subStep;
                    // Re-run matchTemplate at the refined scale to get refined
                    // translation (the discrete winner's loc was computed at a
                    // slightly-wrong scale). 2D parabolic peak interpolation on
                    // the response map's 3x3 neighborhood gives sub-pixel (tx, ty).
                    Mat refinedCap = edgesOnly ? new Mat() : cap;
                    Mat refinedTex = edgesOnly ? new Mat() : tex;
                    if (edgesOnly) { Cv2.Canny(cap, refinedCap, 50, 150); Cv2.Canny(tex, refinedTex, 50, 150); }
                    try
                    {
                        int sw = (int)Math.Round(refinedTex.Width * refinedScale);
                        int sh = (int)Math.Round(refinedTex.Height * refinedScale);
                        if (sw >= 20 && sh >= 20 && sw <= refinedCap.Width && sh <= refinedCap.Height)
                        {
                            using var scaled = new Mat();
                            Cv2.Resize(refinedTex, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
                            using var result = new Mat();
                            Cv2.MatchTemplate(refinedCap, scaled, result, TemplateMatchModes.CCoeffNormed);
                            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
                            var (sdx, sdy) = RefineLocationSubPixel(result, maxLoc);
                            refinedTx = maxLoc.X + sdx;
                            refinedTy = maxLoc.Y + sdy;
                            refinedScore = maxVal;
                            refineNote = $"parabolic ds={subStep:+0.00;-0.00;0.00} subpx=({sdx:+0.00;-0.00;0.00},{sdy:+0.00;-0.00;0.00})";
                        }
                    }
                    finally
                    {
                        if (edgesOnly) { refinedCap.Dispose(); refinedTex.Dispose(); }
                    }
                }
            }
        }

        var overlay = RenderFitOverlay(cap, tex, refinedTx, refinedTy, refinedScale,
            $"matchTemplate({(edgesOnly ? "edge" : "raw")})  ({refinedTx:0.0},{refinedTy:0.0},{refinedScale:0.000})  NCC={refinedScore:0.000}  {refineNote}");
        var mask = RenderTransparentEdgeMask(cap, tex, refinedTx, refinedTy, refinedScale);
        return new SpikeResult(refinedTx, refinedTy, refinedScale, refinedScore,
            $"NCC {refinedScore:0.000} @ scale {refinedScale:0.000}  coarse={coarse.Scale:0.00}  {refineNote}",
            Overlay: overlay, TransparentEdges: mask);
    }

    // ---- matchTemplate(edge) with 2-level pyramid search ----
    //
    // Stage 1: coarse ladder at half resolution (4x cheaper per matchTemplate
    // call). Stage 2: narrow ladder at full resolution centered on the coarse
    // winner. Stage 3: parabolic peak refinement. Stage 4: re-match at refined
    // scale for refined translation. Aims for sub-100ms per locate attempt
    // on production-sized inputs without losing the parabolic-precision
    // recovery.

    private static SpikeResult TemplateMatchPyramidEdge(Mat cap, Mat tex)
    {
        using var capE = new Mat(); Cv2.Canny(cap, capE, 50, 150);
        using var texE = new Mat(); Cv2.Canny(tex, texE, 50, 150);
        using var capHalf = new Mat(); Cv2.PyrDown(capE, capHalf);
        using var texHalf = new Mat(); Cv2.PyrDown(texE, texHalf);

        // Stage 1: coarse ladder at half resolution.
        var ladderCoarse = new List<(double S, double Score)>(64);
        for (double s = ScaleMin; s <= ScaleMax + 1e-6; s += ScaleStep)
        {
            int sw = (int)Math.Round(texHalf.Width * s);
            int sh = (int)Math.Round(texHalf.Height * s);
            if (sw < 10 || sh < 10 || sw > capHalf.Width || sh > capHalf.Height) continue;
            using var scaled = new Mat();
            Cv2.Resize(texHalf, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
            using var result = new Mat();
            Cv2.MatchTemplate(capHalf, scaled, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out _);
            ladderCoarse.Add((s, maxVal));
        }

        if (ladderCoarse.Count == 0)
            return new SpikeResult(null, null, null, 0, "no valid scale at half res");

        int coarseIdx = 0;
        for (int i = 1; i < ladderCoarse.Count; i++)
            if (ladderCoarse[i].Score > ladderCoarse[coarseIdx].Score) coarseIdx = i;
        double coarseScale = ladderCoarse[coarseIdx].S;

        // Stage 2: fine ladder at full resolution centered on coarseScale ±2 steps.
        var ladderFine = new List<(double S, double Score, Point Loc)>(8);
        double fineMin = coarseScale - 2 * ScaleStep;
        double fineMax = coarseScale + 2 * ScaleStep;
        for (double s = fineMin; s <= fineMax + 1e-6; s += ScaleStep)
        {
            if (s <= 0) continue;
            int sw = (int)Math.Round(texE.Width * s);
            int sh = (int)Math.Round(texE.Height * s);
            if (sw < 20 || sh < 20 || sw > capE.Width || sh > capE.Height) continue;
            using var scaled = new Mat();
            Cv2.Resize(texE, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
            using var result = new Mat();
            Cv2.MatchTemplate(capE, scaled, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
            ladderFine.Add((s, maxVal, maxLoc));
        }

        if (ladderFine.Count == 0)
            return new SpikeResult(null, null, null, 0, "no valid scale at full res near coarse winner");

        int fineIdx = 0;
        for (int i = 1; i < ladderFine.Count; i++)
            if (ladderFine[i].Score > ladderFine[fineIdx].Score) fineIdx = i;
        var fineWinner = ladderFine[fineIdx];

        // Stage 3 + 4: parabolic scale refinement + sub-pixel translation
        // refinement via 2D parabolic on the response map at the re-match.
        double refinedScale = fineWinner.S;
        double refinedTx = fineWinner.Loc.X;
        double refinedTy = fineWinner.Loc.Y;
        double refinedScore = fineWinner.Score;
        string refineNote = "discrete";

        if (fineIdx > 0 && fineIdx < ladderFine.Count - 1)
        {
            double y1 = ladderFine[fineIdx - 1].Score;
            double y2 = ladderFine[fineIdx].Score;
            double y3 = ladderFine[fineIdx + 1].Score;
            double denom = y1 - 2 * y2 + y3;
            if (denom < -1e-9)
            {
                double subStep = 0.5 * (y1 - y3) / denom;
                if (Math.Abs(subStep) <= 1.0)
                {
                    refinedScale = fineWinner.S + ScaleStep * subStep;
                    int sw = (int)Math.Round(texE.Width * refinedScale);
                    int sh = (int)Math.Round(texE.Height * refinedScale);
                    if (sw >= 20 && sh >= 20 && sw <= capE.Width && sh <= capE.Height)
                    {
                        using var scaled = new Mat();
                        Cv2.Resize(texE, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
                        using var result = new Mat();
                        Cv2.MatchTemplate(capE, scaled, result, TemplateMatchModes.CCoeffNormed);
                        Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
                        var (sdx, sdy) = RefineLocationSubPixel(result, maxLoc);
                        refinedTx = maxLoc.X + sdx;
                        refinedTy = maxLoc.Y + sdy;
                        refinedScore = maxVal;
                        refineNote = $"parabolic ds={subStep:+0.00;-0.00;0.00} subpx=({sdx:+0.00;-0.00;0.00},{sdy:+0.00;-0.00;0.00})";
                    }
                }
            }
        }

        var overlay = RenderFitOverlay(cap, tex, refinedTx, refinedTy, refinedScale,
            $"matchTemplate(edge,pyr)  ({refinedTx:0.0},{refinedTy:0.0},{refinedScale:0.000})  NCC={refinedScore:0.000}  {refineNote}");
        var mask = RenderTransparentEdgeMask(cap, tex, refinedTx, refinedTy, refinedScale);
        return new SpikeResult(refinedTx, refinedTy, refinedScale, refinedScore,
            $"NCC {refinedScore:0.000} @ scale {refinedScale:0.000}  coarse(half)={coarseScale:0.00}  {refineNote}",
            Overlay: overlay, TransparentEdges: mask);
    }

    // ---- matchTemplate on Sobel gradient magnitude (continuous, not binary) ----
    //
    // Hypothesis: binary Canny edges + Resize(INTER_AREA) produces a gradient
    // artifact that COINCIDENTALLY matches PG's renderer-blurred screenshot
    // edges. Sobel magnitude is continuous from the start — no binary
    // thresholding artifact, no resize-of-binary trap. If Sobel beats Canny
    // across the corpus, the "gradient coincidence" was load-bearing in a way
    // we can do without; if it loses, the coincidence is doing real work.

    private static SpikeResult TemplateMatchSobelMagnitude(Mat cap, Mat tex)
    {
        Mat capPrep = SobelMagnitude8U(cap);
        Mat texPrep = SobelMagnitude8U(tex);

        var ladder = new List<(double Scale, double Score, Point Loc)>(64);
        try
        {
            for (double s = ScaleMin; s <= ScaleMax + 1e-6; s += ScaleStep)
            {
                int sw = (int)Math.Round(texPrep.Width * s);
                int sh = (int)Math.Round(texPrep.Height * s);
                if (sw < 20 || sh < 20 || sw > capPrep.Width || sh > capPrep.Height) continue;
                using var scaled = new Mat();
                Cv2.Resize(texPrep, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
                using var result = new Mat();
                Cv2.MatchTemplate(capPrep, scaled, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
                ladder.Add((s, maxVal, maxLoc));
            }
        }
        finally { capPrep.Dispose(); texPrep.Dispose(); }

        if (ladder.Count == 0)
            return new SpikeResult(null, null, null, 0, "no valid scale");

        int bestIdx = 0;
        for (int i = 1; i < ladder.Count; i++)
            if (ladder[i].Score > ladder[bestIdx].Score) bestIdx = i;
        var coarse = ladder[bestIdx];

        double refinedScale = coarse.Scale;
        double refinedTx = coarse.Loc.X;
        double refinedTy = coarse.Loc.Y;
        double refinedScore = coarse.Score;
        string refineNote = "discrete";

        if (bestIdx > 0 && bestIdx < ladder.Count - 1)
        {
            double y1 = ladder[bestIdx - 1].Score;
            double y2 = ladder[bestIdx].Score;
            double y3 = ladder[bestIdx + 1].Score;
            double denom = y1 - 2 * y2 + y3;
            if (denom < -1e-9)
            {
                double subStep = 0.5 * (y1 - y3) / denom;
                if (Math.Abs(subStep) <= 1.0)
                {
                    refinedScale = coarse.Scale + ScaleStep * subStep;
                    using var refinedCap = SobelMagnitude8U(cap);
                    using var refinedTex = SobelMagnitude8U(tex);
                    int sw = (int)Math.Round(refinedTex.Width * refinedScale);
                    int sh = (int)Math.Round(refinedTex.Height * refinedScale);
                    if (sw >= 20 && sh >= 20 && sw <= refinedCap.Width && sh <= refinedCap.Height)
                    {
                        using var scaled = new Mat();
                        Cv2.Resize(refinedTex, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
                        using var result = new Mat();
                        Cv2.MatchTemplate(refinedCap, scaled, result, TemplateMatchModes.CCoeffNormed);
                        Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
                        var (sdx, sdy) = RefineLocationSubPixel(result, maxLoc);
                        refinedTx = maxLoc.X + sdx;
                        refinedTy = maxLoc.Y + sdy;
                        refinedScore = maxVal;
                        refineNote = $"parabolic ds={subStep:+0.00;-0.00;0.00} subpx=({sdx:+0.00;-0.00;0.00},{sdy:+0.00;-0.00;0.00})";
                    }
                }
            }
        }

        var overlay = RenderFitOverlay(cap, tex, refinedTx, refinedTy, refinedScale,
            $"matchTemplate(sobel,mag)  ({refinedTx:0.0},{refinedTy:0.0},{refinedScale:0.000})  NCC={refinedScore:0.000}  {refineNote}");
        var mask = RenderTransparentEdgeMask(cap, tex, refinedTx, refinedTy, refinedScale);
        return new SpikeResult(refinedTx, refinedTy, refinedScale, refinedScore,
            $"NCC {refinedScore:0.000} @ scale {refinedScale:0.000}  coarse={coarse.Scale:0.00}  {refineNote}",
            Overlay: overlay, TransparentEdges: mask);
    }

    // Sobel gradient magnitude normalized to 8U range [0, 255]. Smooth
    // continuous-valued response; no binary thresholding artifact.
    private static Mat SobelMagnitude8U(Mat src)
    {
        using var gx = new Mat(); Cv2.Sobel(src, gx, MatType.CV_32F, 1, 0, ksize: 3);
        using var gy = new Mat(); Cv2.Sobel(src, gy, MatType.CV_32F, 0, 1, ksize: 3);
        using var mag = new Mat(); Cv2.Magnitude(gx, gy, mag);
        var dst = new Mat();
        Cv2.Normalize(mag, dst, 0, 255, NormTypes.MinMax, MatType.CV_8U);
        return dst;
    }

    // ---- Sobel + padded matchTemplate (template can extend past capture edges) ----
    //
    // Standard matchTemplate(image, template) requires template <= image in both
    // dimensions, so the template's top-left can only land at positions where
    // the whole template fits inside the image. This silently caps the search
    // space when the texture's display position would put part of it past the
    // capture's edge — common at high zoom even when the texture's scaled
    // dimensions are smaller than the capture (HogansKeepBasement-223119:
    // texture 740x740 at scale 0.72, fits within capture 982x741, but the
    // truth position ty=35 puts the bottom at y=775 = 34px past capture).
    //
    // Fix: pad the capture's Sobel magnitude with zeros on all four sides by a
    // generous margin (default 300 px). Then matchTemplate at the padded
    // capture allows template placement at effective offsets in
    // [-pad, cap.W + pad] x [-pad, cap.H + pad]. Recovered (tx_padded, ty_padded)
    // gets pad subtracted to translate back to original capture coords.
    //
    // CCoeffNormed depresses slightly when the template overlaps the zero-pad
    // region, but the peak location is still correct because the in-capture
    // overlap drives the numerator and the zero region only depresses the
    // denominator proportionally.

    private static SpikeResult TemplateMatchSobelPadded(Mat cap, Mat tex)
    {
        const int pad = 300;
        using var capSobel = SobelMagnitude8U(cap);
        using var texSobel = SobelMagnitude8U(tex);
        using var capPadded = new Mat();
        Cv2.CopyMakeBorder(capSobel, capPadded, pad, pad, pad, pad, BorderTypes.Constant, Scalar.All(0));

        var ladder = new List<(double Scale, double Score, Point Loc)>(64);
        for (double s = ScaleMin; s <= ScaleMax + 1e-6; s += ScaleStep)
        {
            int sw = (int)Math.Round(texSobel.Width * s);
            int sh = (int)Math.Round(texSobel.Height * s);
            if (sw < 20 || sh < 20 || sw > capPadded.Width || sh > capPadded.Height) continue;
            using var scaled = new Mat();
            Cv2.Resize(texSobel, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
            using var result = new Mat();
            Cv2.MatchTemplate(capPadded, scaled, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
            ladder.Add((s, maxVal, maxLoc));
        }

        if (ladder.Count == 0)
            return new SpikeResult(null, null, null, 0, "no valid scale (padded)");

        int bestIdx = 0;
        for (int i = 1; i < ladder.Count; i++)
            if (ladder[i].Score > ladder[bestIdx].Score) bestIdx = i;
        var coarse = ladder[bestIdx];

        double refinedScale = coarse.Scale;
        double refinedTx = coarse.Loc.X - pad;
        double refinedTy = coarse.Loc.Y - pad;
        double refinedScore = coarse.Score;
        string refineNote = "discrete";

        if (bestIdx > 0 && bestIdx < ladder.Count - 1)
        {
            double y1 = ladder[bestIdx - 1].Score;
            double y2 = ladder[bestIdx].Score;
            double y3 = ladder[bestIdx + 1].Score;
            double denom = y1 - 2 * y2 + y3;
            if (denom < -1e-9)
            {
                double subStep = 0.5 * (y1 - y3) / denom;
                if (Math.Abs(subStep) <= 1.0)
                {
                    refinedScale = coarse.Scale + ScaleStep * subStep;
                    int sw = (int)Math.Round(texSobel.Width * refinedScale);
                    int sh = (int)Math.Round(texSobel.Height * refinedScale);
                    if (sw >= 20 && sh >= 20 && sw <= capPadded.Width && sh <= capPadded.Height)
                    {
                        using var scaled = new Mat();
                        Cv2.Resize(texSobel, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
                        using var result = new Mat();
                        Cv2.MatchTemplate(capPadded, scaled, result, TemplateMatchModes.CCoeffNormed);
                        Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
                        var (sdx, sdy) = RefineLocationSubPixel(result, maxLoc);
                        refinedTx = (maxLoc.X + sdx) - pad;
                        refinedTy = (maxLoc.Y + sdy) - pad;
                        refinedScore = maxVal;
                        refineNote = $"parabolic ds={subStep:+0.00;-0.00;0.00} subpx=({sdx:+0.00;-0.00;0.00},{sdy:+0.00;-0.00;0.00})";
                    }
                }
            }
        }

        var overlay = RenderFitOverlay(cap, tex, refinedTx, refinedTy, refinedScale,
            $"matchTemplate(sobel,pad)  ({refinedTx:0.0},{refinedTy:0.0},{refinedScale:0.000})  NCC={refinedScore:0.000}  {refineNote}");
        var mask = RenderTransparentEdgeMask(cap, tex, refinedTx, refinedTy, refinedScale);
        return new SpikeResult(refinedTx, refinedTy, refinedScale, refinedScore,
            $"NCC {refinedScore:0.000} @ scale {refinedScale:0.000}  coarse={coarse.Scale:0.00}  {refineNote}",
            Overlay: overlay, TransparentEdges: mask);
    }

    // ---- Sobel + extended scale range with inverted matchTemplate ----
    //
    // The standard matchTemplate scale ladder skips any scale where the
    // resized texture exceeds the capture in either dimension — because
    // matchTemplate requires template <= image. At high zoom (texture
    // displayed larger than the panel), the *correct* answer lives in that
    // skipped region, and the algorithm converges on a wrong local max
    // within the constrained sub-range.
    //
    // Fix: when sw > capW or sh > capH, INVERT — make the capture the
    // template and the upscaled texture the image. matchTemplate now finds
    // where the capture sits inside the scaled texture; the texture's
    // display origin in capture coords is the negation of that location.
    //
    // NCC values from inverted matches are normalized over the capture-sized
    // template instead of the texture-sized one, but CCoeffNormed is bounded
    // [-1, 1] in both directions so peak comparison across directions is
    // empirically OK as a winner-picker (modulo small bias differences).

    private static SpikeResult TemplateMatchSobelExtended(Mat cap, Mat tex)
    {
        using var capSobel = SobelMagnitude8U(cap);
        using var texSobel = SobelMagnitude8U(tex);

        const double extendedScaleMax = 3.0;

        var ladder = new List<(double Scale, double Score, double Tx, double Ty, bool Inverted)>(160);
        for (double s = ScaleMin; s <= extendedScaleMax + 1e-6; s += ScaleStep)
        {
            int sw = (int)Math.Round(texSobel.Width * s);
            int sh = (int)Math.Round(texSobel.Height * s);
            if (sw < 20 || sh < 20) continue;

            using var scaled = new Mat();
            Cv2.Resize(texSobel, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);

            bool fitNormal = sw <= capSobel.Width && sh <= capSobel.Height;
            bool fitInverted = capSobel.Width <= sw && capSobel.Height <= sh;

            if (fitNormal)
            {
                using var result = new Mat();
                Cv2.MatchTemplate(capSobel, scaled, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
                ladder.Add((s, maxVal, maxLoc.X, maxLoc.Y, false));
            }
            else if (fitInverted)
            {
                using var result = new Mat();
                Cv2.MatchTemplate(scaled, capSobel, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
                // maxLoc = where capture's origin sits inside the scaled texture.
                // Texture's display origin in capture coords = -maxLoc.
                ladder.Add((s, maxVal, -maxLoc.X, -maxLoc.Y, true));
            }
            // else: neither fits cleanly (one axis bigger, one smaller) — skip.
        }

        if (ladder.Count == 0)
            return new SpikeResult(null, null, null, 0, "no valid scale (extended range)");

        int bestIdx = 0;
        for (int i = 1; i < ladder.Count; i++)
            if (ladder[i].Score > ladder[bestIdx].Score) bestIdx = i;
        var coarse = ladder[bestIdx];

        // Diagnostic: best inverted-direction match on its own terms.
        int bestInvIdx = -1;
        for (int i = 0; i < ladder.Count; i++)
            if (ladder[i].Inverted && (bestInvIdx < 0 || ladder[i].Score > ladder[bestInvIdx].Score))
                bestInvIdx = i;
        string invNote = bestInvIdx < 0 ? "" : $"  bestInv: s={ladder[bestInvIdx].Scale:0.00} NCC={ladder[bestInvIdx].Score:0.000} tx={ladder[bestInvIdx].Tx:0.0} ty={ladder[bestInvIdx].Ty:0.0}";

        // Parabolic scale refinement, restricted to same-direction neighbors.
        double refinedScale = coarse.Scale;
        double refinedTx = coarse.Tx;
        double refinedTy = coarse.Ty;
        double refinedScore = coarse.Score;
        string refineNote = $"discrete  direction={(coarse.Inverted ? "inverted" : "normal")}";

        if (bestIdx > 0 && bestIdx < ladder.Count - 1
            && ladder[bestIdx - 1].Inverted == coarse.Inverted
            && ladder[bestIdx + 1].Inverted == coarse.Inverted)
        {
            double y1 = ladder[bestIdx - 1].Score;
            double y2 = ladder[bestIdx].Score;
            double y3 = ladder[bestIdx + 1].Score;
            double denom = y1 - 2 * y2 + y3;
            if (denom < -1e-9)
            {
                double subStep = 0.5 * (y1 - y3) / denom;
                if (Math.Abs(subStep) <= 1.0)
                {
                    refinedScale = coarse.Scale + ScaleStep * subStep;
                    int sw = (int)Math.Round(texSobel.Width * refinedScale);
                    int sh = (int)Math.Round(texSobel.Height * refinedScale);
                    if (sw >= 20 && sh >= 20)
                    {
                        using var scaled = new Mat();
                        Cv2.Resize(texSobel, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
                        if (!coarse.Inverted && sw <= capSobel.Width && sh <= capSobel.Height)
                        {
                            using var result = new Mat();
                            Cv2.MatchTemplate(capSobel, scaled, result, TemplateMatchModes.CCoeffNormed);
                            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
                            var (sdx, sdy) = RefineLocationSubPixel(result, maxLoc);
                            refinedTx = maxLoc.X + sdx;
                            refinedTy = maxLoc.Y + sdy;
                            refinedScore = maxVal;
                            refineNote = $"normal parabolic ds={subStep:+0.00;-0.00;0.00} subpx=({sdx:+0.00;-0.00;0.00},{sdy:+0.00;-0.00;0.00})";
                        }
                        else if (coarse.Inverted && capSobel.Width <= sw && capSobel.Height <= sh)
                        {
                            using var result = new Mat();
                            Cv2.MatchTemplate(scaled, capSobel, result, TemplateMatchModes.CCoeffNormed);
                            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
                            var (sdx, sdy) = RefineLocationSubPixel(result, maxLoc);
                            refinedTx = -(maxLoc.X + sdx);
                            refinedTy = -(maxLoc.Y + sdy);
                            refinedScore = maxVal;
                            refineNote = $"inverted parabolic ds={subStep:+0.00;-0.00;0.00} subpx=({sdx:+0.00;-0.00;0.00},{sdy:+0.00;-0.00;0.00})";
                        }
                    }
                }
            }
        }

        var overlay = RenderFitOverlay(cap, tex, refinedTx, refinedTy, refinedScale,
            $"matchTemplate(sobel,ext)  ({refinedTx:0.0},{refinedTy:0.0},{refinedScale:0.000})  NCC={refinedScore:0.000}  {refineNote}");
        var mask = RenderTransparentEdgeMask(cap, tex, refinedTx, refinedTy, refinedScale);
        return new SpikeResult(refinedTx, refinedTy, refinedScale, refinedScore,
            $"NCC {refinedScore:0.000} @ scale {refinedScale:0.000}  coarse={coarse.Scale:0.00}  {refineNote}",
            Overlay: overlay, TransparentEdges: mask);
    }

    // ---- Sobel + anisotropic refinement (sx, sy independent) ----
    //
    // Starts with the isotropic Sobel best, then runs a narrow 2D search over
    // (sx, sy) in a [s* - 3*step, s* + 3*step] box at half the regular step.
    // Empirically PG's "anisotropy" on the bundles tested is < 5% per axis,
    // so the small search box is enough. Parabolic refinement on each axis
    // independently gives sub-step (sx, sy).
    //
    // If best NCC is within epsilon of the isotropic NCC, falls back to
    // isotropic — anisotropy isn't load-bearing for that bundle.

    private static SpikeResult TemplateMatchSobelAniso(Mat cap, Mat tex)
    {
        // Stage 1: isotropic best as the starting point.
        var iso = TemplateMatchSobelMagnitude(cap, tex);
        if (iso.Scale is not double s0) return iso;
        double tx0 = iso.Tx ?? 0, ty0 = iso.Ty ?? 0, ncc0 = iso.Confidence;

        // Dispose iso's overlay/mask — we'll regenerate at anisotropic params.
        iso.Overlay?.Dispose();
        iso.TransparentEdges?.Dispose();

        using var capSobel = SobelMagnitude8U(cap);
        using var texSobel = SobelMagnitude8U(tex);

        // Stage 2: dense (sx, sy) grid in a [s0 - 3*step, s0 + 3*step] box at
        // half the regular step.
        const double anisoStep = 0.01;
        const double anisoSpread = 0.06;  // ±0.06 in each axis around s0
        double sxMin = Math.Max(0.10, s0 - anisoSpread);
        double sxMax = Math.Min(2.00, s0 + anisoSpread);
        double syMin = Math.Max(0.10, s0 - anisoSpread);
        double syMax = Math.Min(2.00, s0 + anisoSpread);

        double bestNcc = ncc0;
        double bestSx = s0, bestSy = s0;
        Point bestLoc = new Point((int)Math.Round(tx0), (int)Math.Round(ty0));
        Mat? bestResponse = null;

        try
        {
            for (double sx = sxMin; sx <= sxMax + 1e-9; sx += anisoStep)
            {
                int sw = (int)Math.Round(texSobel.Width * sx);
                if (sw < 20 || sw > capSobel.Width) continue;
                for (double sy = syMin; sy <= syMax + 1e-9; sy += anisoStep)
                {
                    int sh = (int)Math.Round(texSobel.Height * sy);
                    if (sh < 20 || sh > capSobel.Height) continue;
                    using var scaled = new Mat();
                    Cv2.Resize(texSobel, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
                    var result = new Mat();
                    Cv2.MatchTemplate(capSobel, scaled, result, TemplateMatchModes.CCoeffNormed);
                    Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
                    if (maxVal > bestNcc)
                    {
                        bestResponse?.Dispose();
                        bestNcc = maxVal;
                        bestSx = sx;
                        bestSy = sy;
                        bestLoc = maxLoc;
                        bestResponse = result;
                    }
                    else
                    {
                        result.Dispose();
                    }
                }
            }

            // Stage 3: sub-pixel translation refinement on the winning response map.
            double finalTx = bestLoc.X, finalTy = bestLoc.Y;
            if (bestResponse is not null)
            {
                var (sdx, sdy) = RefineLocationSubPixel(bestResponse, bestLoc);
                finalTx = bestLoc.X + sdx;
                finalTy = bestLoc.Y + sdy;
            }

            // Aspect deviation: how far from isotropy the winner is.
            double aspectDeviation = Math.Abs(bestSx - bestSy) / s0;
            double nccGain = bestNcc - ncc0;
            string note = aspectDeviation < 0.005 && nccGain < 0.01
                ? $"~isotropic sx={bestSx:0.000} sy={bestSy:0.000} NCC={bestNcc:0.000} (gain {nccGain:+0.000;-0.000;0.000})"
                : $"sx={bestSx:0.000} sy={bestSy:0.000} aspect-dev={aspectDeviation:0.0%}  NCC={bestNcc:0.000} (gain {nccGain:+0.000;-0.000;0.000})";

            double scaleReport = Math.Sqrt(bestSx * bestSy);
            var overlay = RenderAnisoFitOverlay(cap, tex, finalTx, finalTy, bestSx, bestSy,
                $"matchTemplate(sobel,aniso)  ({finalTx:0.0},{finalTy:0.0})  sx={bestSx:0.000} sy={bestSy:0.000}  NCC={bestNcc:0.000}");
            var mask = RenderAnisoEdgeMask(tex, bestSx, bestSy);
            return new SpikeResult(finalTx, finalTy, scaleReport, bestNcc, note,
                Overlay: overlay, TransparentEdges: mask);
        }
        finally
        {
            bestResponse?.Dispose();
        }
    }

    // Anisotropic overlay variants — same as the isotropic versions but accept
    // (sx, sy) instead of a single scale.
    private static Mat RenderAnisoFitOverlay(Mat cap, Mat tex, double tx, double ty, double sx, double sy, string label)
    {
        var overlay = new Mat();
        Cv2.CvtColor(cap, overlay, ColorConversionCodes.GRAY2BGR);
        int sw = (int)Math.Round(tex.Width * sx);
        int sh = (int)Math.Round(tex.Height * sy);
        if (sw < 4 || sh < 4) return overlay;
        using var texS = new Mat();
        Cv2.Resize(tex, texS, new Size(sw, sh), interpolation: InterpolationFlags.Area);
        using var texE = new Mat();
        Cv2.Canny(texS, texE, 50, 150);
        int x0 = (int)Math.Round(tx), y0 = (int)Math.Round(ty);
        var texEIdx = texE.GetGenericIndexer<byte>();
        var overlayIdx = overlay.GetGenericIndexer<Vec3b>();
        for (int y = 0; y < texE.Rows; y++)
            for (int x = 0; x < texE.Cols; x++)
            {
                if (texEIdx[y, x] == 0) continue;
                int cx = x0 + x, cy = y0 + y;
                if (cx < 0 || cy < 0 || cx >= overlay.Cols || cy >= overlay.Rows) continue;
                overlayIdx[cy, cx] = new Vec3b(0, 0, 255);
            }
        Cv2.Rectangle(overlay, new Rect(x0, y0, sw, sh), new Scalar(255, 255, 0), 2);
        Cv2.PutText(overlay, label, new Point(8, 24), HersheyFonts.HersheySimplex, 0.55, new Scalar(0, 0, 0), 4);
        Cv2.PutText(overlay, label, new Point(8, 24), HersheyFonts.HersheySimplex, 0.55, new Scalar(0, 255, 255), 1);
        return overlay;
    }

    private static Mat RenderAnisoEdgeMask(Mat tex, double sx, double sy)
    {
        int sw = (int)Math.Round(tex.Width * sx);
        int sh = (int)Math.Round(tex.Height * sy);
        if (sw < 4 || sh < 4) return new Mat(1, 1, MatType.CV_8UC4, Scalar.All(0));
        using var texS = new Mat();
        Cv2.Resize(tex, texS, new Size(sw, sh), interpolation: InterpolationFlags.Area);
        using var texE = new Mat();
        Cv2.Canny(texS, texE, 50, 150);
        var mask = new Mat(sh, sw, MatType.CV_8UC4, Scalar.All(0));
        var texEIdx = texE.GetGenericIndexer<byte>();
        var maskIdx = mask.GetGenericIndexer<Vec4b>();
        for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw; x++)
                if (texEIdx[y, x] != 0) maskIdx[y, x] = new Vec4b(0, 0, 255, 255);
        return mask;
    }

    // ---- matchTemplate(edge) with 3-level Gaussian pyramid (full / half / quarter) ----
    //
    // Three-stage coarse-to-fine: ladder at quarter resolution → narrow at half
    // resolution → narrow at full resolution + parabolic + sub-pixel refinement.
    // Compared to the 2-level pyramid: more setup cost (one extra PyrDown per
    // side, one extra ladder stage) but the coarse-stage cost drops 4× because
    // matchTemplate at quarter res is ~16× cheaper than half res.

    private static SpikeResult TemplateMatchPyramid3Edge(Mat cap, Mat tex)
    {
        using var capE = new Mat(); Cv2.Canny(cap, capE, 50, 150);
        using var texE = new Mat(); Cv2.Canny(tex, texE, 50, 150);
        using var capL1 = new Mat(); Cv2.PyrDown(capE, capL1);   // half
        using var texL1 = new Mat(); Cv2.PyrDown(texE, texL1);
        using var capL2 = new Mat(); Cv2.PyrDown(capL1, capL2);  // quarter
        using var texL2 = new Mat(); Cv2.PyrDown(texL1, texL2);

        // Stage 1: full scale ladder at quarter resolution.
        var ladderL2 = new List<(double S, double Score)>(64);
        for (double s = ScaleMin; s <= ScaleMax + 1e-6; s += ScaleStep)
        {
            int sw = (int)Math.Round(texL2.Width * s);
            int sh = (int)Math.Round(texL2.Height * s);
            if (sw < 5 || sh < 5 || sw > capL2.Width || sh > capL2.Height) continue;
            using var scaled = new Mat();
            Cv2.Resize(texL2, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
            using var result = new Mat();
            Cv2.MatchTemplate(capL2, scaled, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out _);
            ladderL2.Add((s, maxVal));
        }
        if (ladderL2.Count == 0)
            return new SpikeResult(null, null, null, 0, "no valid scale at quarter res");
        int l2Idx = 0;
        for (int i = 1; i < ladderL2.Count; i++)
            if (ladderL2[i].Score > ladderL2[l2Idx].Score) l2Idx = i;
        double l2Scale = ladderL2[l2Idx].S;

        // Stage 2: narrow ladder at half resolution centered on quarter winner.
        var ladderL1 = new List<(double S, double Score)>(8);
        for (double s = l2Scale - 2 * ScaleStep; s <= l2Scale + 2 * ScaleStep + 1e-6; s += ScaleStep)
        {
            if (s <= 0) continue;
            int sw = (int)Math.Round(texL1.Width * s);
            int sh = (int)Math.Round(texL1.Height * s);
            if (sw < 10 || sh < 10 || sw > capL1.Width || sh > capL1.Height) continue;
            using var scaled = new Mat();
            Cv2.Resize(texL1, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
            using var result = new Mat();
            Cv2.MatchTemplate(capL1, scaled, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out _);
            ladderL1.Add((s, maxVal));
        }
        if (ladderL1.Count == 0)
            return new SpikeResult(null, null, null, 0, "no valid scale at half res");
        int l1Idx = 0;
        for (int i = 1; i < ladderL1.Count; i++)
            if (ladderL1[i].Score > ladderL1[l1Idx].Score) l1Idx = i;
        double l1Scale = ladderL1[l1Idx].S;

        // Stage 3: narrow ladder at full resolution centered on half winner.
        var ladderFine = new List<(double S, double Score, Point Loc)>(8);
        for (double s = l1Scale - 2 * ScaleStep; s <= l1Scale + 2 * ScaleStep + 1e-6; s += ScaleStep)
        {
            if (s <= 0) continue;
            int sw = (int)Math.Round(texE.Width * s);
            int sh = (int)Math.Round(texE.Height * s);
            if (sw < 20 || sh < 20 || sw > capE.Width || sh > capE.Height) continue;
            using var scaled = new Mat();
            Cv2.Resize(texE, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
            using var result = new Mat();
            Cv2.MatchTemplate(capE, scaled, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
            ladderFine.Add((s, maxVal, maxLoc));
        }
        if (ladderFine.Count == 0)
            return new SpikeResult(null, null, null, 0, "no valid scale at full res");
        int fineIdx = 0;
        for (int i = 1; i < ladderFine.Count; i++)
            if (ladderFine[i].Score > ladderFine[fineIdx].Score) fineIdx = i;
        var fineWinner = ladderFine[fineIdx];

        // Stage 4: parabolic scale refinement + sub-pixel translation refinement.
        double refinedScale = fineWinner.S;
        double refinedTx = fineWinner.Loc.X;
        double refinedTy = fineWinner.Loc.Y;
        double refinedScore = fineWinner.Score;
        string refineNote = "discrete";

        if (fineIdx > 0 && fineIdx < ladderFine.Count - 1)
        {
            double y1 = ladderFine[fineIdx - 1].Score;
            double y2 = ladderFine[fineIdx].Score;
            double y3 = ladderFine[fineIdx + 1].Score;
            double denom = y1 - 2 * y2 + y3;
            if (denom < -1e-9)
            {
                double subStep = 0.5 * (y1 - y3) / denom;
                if (Math.Abs(subStep) <= 1.0)
                {
                    refinedScale = fineWinner.S + ScaleStep * subStep;
                    int sw = (int)Math.Round(texE.Width * refinedScale);
                    int sh = (int)Math.Round(texE.Height * refinedScale);
                    if (sw >= 20 && sh >= 20 && sw <= capE.Width && sh <= capE.Height)
                    {
                        using var scaled = new Mat();
                        Cv2.Resize(texE, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
                        using var result = new Mat();
                        Cv2.MatchTemplate(capE, scaled, result, TemplateMatchModes.CCoeffNormed);
                        Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
                        var (sdx, sdy) = RefineLocationSubPixel(result, maxLoc);
                        refinedTx = maxLoc.X + sdx;
                        refinedTy = maxLoc.Y + sdy;
                        refinedScore = maxVal;
                        refineNote = $"parabolic ds={subStep:+0.00;-0.00;0.00} subpx=({sdx:+0.00;-0.00;0.00},{sdy:+0.00;-0.00;0.00})";
                    }
                }
            }
        }

        var overlay = RenderFitOverlay(cap, tex, refinedTx, refinedTy, refinedScale,
            $"matchTemplate(edge,pyr3)  ({refinedTx:0.0},{refinedTy:0.0},{refinedScale:0.000})  NCC={refinedScore:0.000}  {refineNote}");
        var mask = RenderTransparentEdgeMask(cap, tex, refinedTx, refinedTy, refinedScale);
        return new SpikeResult(refinedTx, refinedTy, refinedScale, refinedScore,
            $"NCC {refinedScore:0.000} @ scale {refinedScale:0.000}  l2={l2Scale:0.00}  l1={l1Scale:0.00}  {refineNote}",
            Overlay: overlay, TransparentEdges: mask);
    }

    // ---- chamfer matching: Canny -> distanceTransform on both -> NCC scale ladder ----

    private static SpikeResult ChamferMatchScaleLadder(Mat cap, Mat tex)
    {
        using var capE = new Mat(); Cv2.Canny(cap, capE, 50, 150);
        using var texE = new Mat(); Cv2.Canny(tex, texE, 50, 150);
        // Chamfer distance: distance to nearest edge. Invert the edge map so edges
        // are zero and non-edges are 255, then distanceTransform.
        using var capEinv = new Mat(); Cv2.BitwiseNot(capE, capEinv);
        using var texEinv = new Mat(); Cv2.BitwiseNot(texE, texEinv);
        using var capDt = new Mat(); Cv2.DistanceTransform(capEinv, capDt, DistanceTypes.L2, DistanceTransformMasks.Mask3);
        using var texDt = new Mat(); Cv2.DistanceTransform(texEinv, texDt, DistanceTypes.L2, DistanceTransformMasks.Mask3);
        // Clip distances so far-from-edge regions don't dominate the NCC.
        Cv2.Min(capDt, new Scalar(20.0), capDt);
        Cv2.Min(texDt, new Scalar(20.0), texDt);
        using var capDt8 = new Mat(); capDt.ConvertTo(capDt8, MatType.CV_8U, 12.0);
        using var texDt8 = new Mat(); texDt.ConvertTo(texDt8, MatType.CV_8U, 12.0);

        double bestScore = double.MinValue;
        double bestScale = 0;
        Point bestLoc = default;
        for (double s = ScaleMin; s <= ScaleMax + 1e-6; s += ScaleStep)
        {
            int sw = (int)Math.Round(texDt8.Width * s);
            int sh = (int)Math.Round(texDt8.Height * s);
            if (sw < 20 || sh < 20 || sw > capDt8.Width || sh > capDt8.Height) continue;
            using var scaled = new Mat();
            Cv2.Resize(texDt8, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
            using var result = new Mat();
            Cv2.MatchTemplate(capDt8, scaled, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
            if (maxVal > bestScore)
            {
                bestScore = maxVal;
                bestScale = s;
                bestLoc = maxLoc;
            }
        }

        if (bestScale == 0)
            return new SpikeResult(null, null, null, 0, "no valid scale");
        return new SpikeResult(bestLoc.X, bestLoc.Y, bestScale, bestScore,
            $"best chamfer NCC {bestScore:0.000} @ scale {bestScale:0.00}  (capEdges={Cv2.CountNonZero(capE)} texEdges={Cv2.CountNonZero(texE)})");
    }

    // ---- phaseCorrelate with assumed scale ----

    private static SpikeResult PhaseCorrelateAssumedScale(Mat cap, Mat tex)
    {
        // For axis-aligned PG maps with the in-game map zoomed all the way out, the
        // natural assumption is that the captured map panel ≈ the base texture at
        // some scale. We don't know the scale, but for the spike we make the
        // assumption that minimizes the comparison: resize texture to match cap
        // dimensions exactly, then ask phaseCorrelate for the residual translation.
        // If the user's capture box matched the panel well, the residual should be
        // small and the peak response should be sharp.
        using var texR = new Mat();
        Cv2.Resize(tex, texR, new Size(cap.Width, cap.Height), interpolation: InterpolationFlags.Area);
        using var capF = new Mat(); cap.ConvertTo(capF, MatType.CV_32F);
        using var texF = new Mat(); texR.ConvertTo(texF, MatType.CV_32F);
        using var window = new Mat();
        var pt = Cv2.PhaseCorrelate(capF, texF, window, out double response);
        // No explicit scale recovered here; report 1.0 (assumed) + the response peak.
        return new SpikeResult(pt.X, pt.Y, 1.0, response,
            $"sub-pixel dxdy=({pt.X:0.00},{pt.Y:0.00}) peak={response:0.000}  (assumed scale=1.0 → tex resized to cap)");
    }

    // ---- FastLineDetector: how many line segments does each input even have? ----

    private static SpikeResult LineSegmentCensus(Mat cap, Mat tex)
    {
        using var fld = CvXImgProc.CreateFastLineDetector();
        var capLines = fld.Detect(cap);
        var texLines = fld.Detect(tex);
        double capMean = capLines.Length == 0 ? 0 : capLines.Average(v => LineLen(v));
        double texMean = texLines.Length == 0 ? 0 : texLines.Average(v => LineLen(v));
        return new SpikeResult(null, null, null, 0,
            $"capture={capLines.Length} segs (mean={capMean:0.0}px)  texture={texLines.Length} segs (mean={texMean:0.0}px)");

        static double LineLen(Vec4f v) => Math.Sqrt((v.Item2 - v.Item0) * (v.Item2 - v.Item0) + (v.Item3 - v.Item1) * (v.Item3 - v.Item1));
    }

    // ---- Borgefors classical chamfer matching (sparse point set vs distance field) ----
    //
    // For each candidate (tx, ty, scale): project every texture edge point through
    // T, look up the screenshot's distance transform at the projected position, sum.
    // Best fit = lowest mean distance. Robust to occlusion in the SCREENSHOT — fog
    // pixels have no edges to mislead the score; they're simply not "queried." The
    // texture is the model (full information); the screenshot is the observation
    // (partial information).
    //
    // Cost: O(N_scales * N_tx * N_ty * N_tex_pts). With sane subsampling + coarse
    // grid: ~1-3 s per bundle on a 1280x1060 capture.

    private static SpikeResult BorgeforsChamfer(Mat cap, Mat tex)
    {
        // Screenshot distance transform: distance from each pixel to the nearest
        // screenshot edge. Sparse fog → large distances. Edge pixel → 0.
        using var capE = new Mat(); Cv2.Canny(cap, capE, 50, 150);
        using var capEinv = new Mat(); Cv2.BitwiseNot(capE, capEinv);
        using var capDt = new Mat(); Cv2.DistanceTransform(capEinv, capDt, DistanceTypes.L2, DistanceTransformMasks.Mask3);

        // Texture edges as a point list. Subsample to ~1500 for tractability.
        using var texE = new Mat(); Cv2.Canny(tex, texE, 50, 150);
        var texEdgePts = new List<Point>(8192);
        var capDtIdx = capDt.GetGenericIndexer<float>();
        // Extract nonzero edge pixels.
        var texEIdx = texE.GetGenericIndexer<byte>();
        for (int y = 0; y < texE.Rows; y++)
            for (int x = 0; x < texE.Cols; x++)
                if (texEIdx[y, x] != 0) texEdgePts.Add(new Point(x, y));

        if (texEdgePts.Count == 0)
            return new SpikeResult(null, null, null, 0, "no texture edges");
        if (texEdgePts.Count > 1500)
        {
            // Even-stride subsample.
            int stride = texEdgePts.Count / 1500;
            var sub = new List<Point>(1500);
            for (int i = 0; i < texEdgePts.Count; i += stride) sub.Add(texEdgePts[i]);
            texEdgePts = sub;
        }

        int capW = cap.Width, capH = cap.Height;
        double bestCost = double.MaxValue;
        double bestScale = 0, bestTx = 0, bestTy = 0;
        int bestInBounds = 0;
        const int gridStep = 6;  // pixel quantization for tx/ty search
        const float outOfBoundsPenalty = 25f;  // chamfer "miss" penalty (px)

        for (double s = ScaleMin; s <= ScaleMax + 1e-6; s += ScaleStep)
        {
            // Texture bbox at this scale.
            int sw = (int)Math.Round(tex.Width * s);
            int sh = (int)Math.Round(tex.Height * s);
            if (sw < 20 || sh < 20) continue;

            // Translation search range: texture can extend off-screen a bit on either side.
            int txMin = -sw / 2;
            int txMax = capW - sw / 2;
            int tyMin = -sh / 2;
            int tyMax = capH - sh / 2;

            for (int tx = txMin; tx <= txMax; tx += gridStep)
            {
                for (int ty = tyMin; ty <= tyMax; ty += gridStep)
                {
                    double sum = 0; int inBounds = 0;
                    for (int i = 0; i < texEdgePts.Count; i++)
                    {
                        int px = (int)(tx + s * texEdgePts[i].X);
                        int py = (int)(ty + s * texEdgePts[i].Y);
                        if (px < 0 || py < 0 || px >= capW || py >= capH)
                        {
                            sum += outOfBoundsPenalty;
                            continue;
                        }
                        sum += capDtIdx[py, px];
                        inBounds++;
                    }
                    double cost = sum / texEdgePts.Count;
                    // Penalize candidates where most of the texture would lie offscreen.
                    if (inBounds < texEdgePts.Count / 3) continue;
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestScale = s;
                        bestTx = tx;
                        bestTy = ty;
                        bestInBounds = inBounds;
                    }
                }
            }
        }

        if (bestScale == 0)
            return new SpikeResult(null, null, null, 0, "no candidate fit");

        // Confidence: invert the cost (lower is better). Saturate so it's in [0, 1].
        double confidence = 1.0 / (1.0 + bestCost / 5.0);
        var overlay = RenderFitOverlay(cap, tex, bestTx, bestTy, bestScale,
            $"Borgefors  ({bestTx:0},{bestTy:0},{bestScale:0.00})  dist={bestCost:0.0}px  inB={bestInBounds}/{texEdgePts.Count}");
        var mask = RenderTransparentEdgeMask(cap, tex, bestTx, bestTy, bestScale);
        return new SpikeResult(bestTx, bestTy, bestScale, confidence,
            $"mean chamfer dist={bestCost:0.00}px  inBounds={bestInBounds}/{texEdgePts.Count}",
            Overlay: overlay, TransparentEdges: mask);
    }

    // Paint the texture's Canny edges in red onto the grayscale capture at the
    // recovered (tx, ty, scale), plus a cyan bounding box and a label header.
    // Eyeball-check helper: a "real" fit overlays texture edges on top of
    // screenshot edges; a basin-of-attraction false-positive overlays them in
    // gray fog. PNG output via the harness's RunOne.
    private static Mat RenderFitOverlay(Mat cap, Mat tex, double tx, double ty, double scale, string label)
    {
        var overlay = new Mat();
        Cv2.CvtColor(cap, overlay, ColorConversionCodes.GRAY2BGR);

        int sw = (int)Math.Round(tex.Width * scale);
        int sh = (int)Math.Round(tex.Height * scale);
        if (sw < 4 || sh < 4) return overlay;
        using var texS = new Mat();
        Cv2.Resize(tex, texS, new Size(sw, sh), interpolation: InterpolationFlags.Area);
        using var texE = new Mat();
        Cv2.Canny(texS, texE, 50, 150);

        int x0 = (int)Math.Round(tx);
        int y0 = (int)Math.Round(ty);
        var texEIdx = texE.GetGenericIndexer<byte>();
        var overlayIdx = overlay.GetGenericIndexer<Vec3b>();
        for (int y = 0; y < texE.Rows; y++)
        {
            for (int x = 0; x < texE.Cols; x++)
            {
                if (texEIdx[y, x] == 0) continue;
                int cx = x0 + x;
                int cy = y0 + y;
                if (cx < 0 || cy < 0 || cx >= overlay.Cols || cy >= overlay.Rows) continue;
                overlayIdx[cy, cx] = new Vec3b(0, 0, 255); // red
            }
        }

        Cv2.Rectangle(overlay, new Rect(x0, y0, sw, sh), new Scalar(255, 255, 0), 2);
        Cv2.PutText(overlay, label, new Point(8, 24), HersheyFonts.HersheySimplex, 0.55, new Scalar(0, 0, 0), 4);
        Cv2.PutText(overlay, label, new Point(8, 24), HersheyFonts.HersheySimplex, 0.55, new Scalar(0, 255, 255), 1);
        return overlay;
    }

    // Transparent-background standalone edge sprite: cropped tight to the
    // texture's scaled bbox (sw × sh), red pixels where the texture has Canny
    // edges, fully transparent elsewhere. Position-free — drop it as a layer
    // in GIMP and slide freely to find the true offset. The recovered
    // (tx, ty) is in the filename so you can read the delta from the editor's
    // layer offset back to the algorithm's hypothesis.
    private static Mat RenderTransparentEdgeMask(Mat _cap, Mat tex, double _tx, double _ty, double scale)
    {
        int sw = (int)Math.Round(tex.Width * scale);
        int sh = (int)Math.Round(tex.Height * scale);
        if (sw < 4 || sh < 4) return new Mat(1, 1, MatType.CV_8UC4, Scalar.All(0));
        using var texS = new Mat();
        Cv2.Resize(tex, texS, new Size(sw, sh), interpolation: InterpolationFlags.Area);
        using var texE = new Mat();
        Cv2.Canny(texS, texE, 50, 150);

        var mask = new Mat(sh, sw, MatType.CV_8UC4, Scalar.All(0));
        var texEIdx = texE.GetGenericIndexer<byte>();
        var maskIdx = mask.GetGenericIndexer<Vec4b>();
        for (int y = 0; y < sh; y++)
        {
            for (int x = 0; x < sw; x++)
            {
                if (texEIdx[y, x] == 0) continue;
                maskIdx[y, x] = new Vec4b(0, 0, 255, 255); // red, opaque
            }
        }
        return mask;
    }

    // ---- Generalized Hough Transform on edge points (axis-aligned isotropic similarity) ----
    //
    // For each pair (texture edge point P, screenshot edge point S) under each
    // candidate scale s, the implied translation is (tx, ty) = (Sx - s*Px, Sy - s*Py).
    // Vote in a 3D accumulator (scale, tx, ty). The correct transform receives many
    // consistent votes; random pairings are scattered across the accumulator. Peak
    // = best fit. Robust to massive occlusion: only the visible screenshot edges
    // need to vote; the rest contribute nothing.
    //
    // Cost: O(N_tex_pts * N_screen_pts * N_scales). Subsample both sides to ~400
    // pts each → 400*400*19 ≈ 3M votes. Sub-second.

    private static SpikeResult GeneralizedHough(Mat cap, Mat tex)
    {
        using var capE = new Mat(); Cv2.Canny(cap, capE, 50, 150);
        using var texE = new Mat(); Cv2.Canny(tex, texE, 50, 150);

        var capPts = ExtractEdgePoints(capE, maxPoints: 600);
        var texPts = ExtractEdgePoints(texE, maxPoints: 400);
        if (capPts.Count == 0 || texPts.Count == 0)
            return new SpikeResult(null, null, null, 0, $"cap={capPts.Count} tex={texPts.Count} — no edges");

        int capW = cap.Width, capH = cap.Height;
        // Accumulator: scale × tx × ty. Quantize tx, ty to 8 px bins.
        const int bin = 8;
        int txBins = (capW + 400) / bin;
        int tyBins = (capH + 400) / bin;
        int txOffset = 200 / bin;  // allow texture to start up to 200 px off-screen
        int tyOffset = 200 / bin;

        int numScales = (int)Math.Round((ScaleMax - ScaleMin) / ScaleStep) + 1;
        var accumulator = new ushort[numScales, txBins, tyBins];

        for (int si = 0; si < numScales; si++)
        {
            double s = ScaleMin + si * ScaleStep;
            for (int i = 0; i < texPts.Count; i++)
            {
                double spx = s * texPts[i].X;
                double spy = s * texPts[i].Y;
                for (int j = 0; j < capPts.Count; j++)
                {
                    int tx = (int)(capPts[j].X - spx);
                    int ty = (int)(capPts[j].Y - spy);
                    int txi = tx / bin + txOffset;
                    int tyi = ty / bin + tyOffset;
                    if (txi < 0 || tyi < 0 || txi >= txBins || tyi >= tyBins) continue;
                    if (accumulator[si, txi, tyi] < ushort.MaxValue) accumulator[si, txi, tyi]++;
                }
            }
        }

        // Find peak.
        ushort peakVotes = 0;
        int peakS = 0, peakTxi = 0, peakTyi = 0;
        for (int si = 0; si < numScales; si++)
            for (int txi = 0; txi < txBins; txi++)
                for (int tyi = 0; tyi < tyBins; tyi++)
                    if (accumulator[si, txi, tyi] > peakVotes)
                    {
                        peakVotes = accumulator[si, txi, tyi];
                        peakS = si; peakTxi = txi; peakTyi = tyi;
                    }

        double bestScale = ScaleMin + peakS * ScaleStep;
        double bestTx = (peakTxi - txOffset) * bin;
        double bestTy = (peakTyi - tyOffset) * bin;

        // Confidence: peak / expected-random-floor. Random expectation per bin =
        // total_votes / num_bins. Ratio of peak to this floor.
        long totalVotes = (long)texPts.Count * capPts.Count * numScales;
        double floor = (double)totalVotes / (numScales * txBins * tyBins);
        double confidence = floor > 0 ? peakVotes / floor : 0;

        return new SpikeResult(bestTx, bestTy, bestScale, confidence,
            $"peakVotes={peakVotes} floor={floor:0.0} cap={capPts.Count} tex={texPts.Count}");
    }

    private static List<Point> ExtractEdgePoints(Mat edges, int maxPoints)
    {
        var pts = new List<Point>(8192);
        var idx = edges.GetGenericIndexer<byte>();
        for (int y = 0; y < edges.Rows; y++)
            for (int x = 0; x < edges.Cols; x++)
                if (idx[y, x] != 0) pts.Add(new Point(x, y));
        if (pts.Count <= maxPoints) return pts;
        int stride = pts.Count / maxPoints;
        var sub = new List<Point>(maxPoints + 8);
        for (int i = 0; i < pts.Count; i += stride) sub.Add(pts[i]);
        return sub;
    }
}
