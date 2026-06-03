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
    private const double ScaleMin = 0.30;
    private const double ScaleMax = 1.20;
    private const double ScaleStep = 0.05;

    public static int Run()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var calibRoot = Path.Combine(localApp, "Mithril", "diagnostics", "calibration");
        var assetsDir = Path.Combine(localApp, "Mithril", "assets");
        var outDir = Path.Combine(Path.GetTempPath(), "sparse-locate-spike");
        Directory.CreateDirectory(outDir);

        // Eltibule accepted bundle's 04-maprect.json: (360, 283, 565, 561) — the
        // production-recovered map sub-rect inside the captured frame. Using it as
        // a *given* crop is the apples-to-apples easy-regime control: with locate
        // already solved, can the candidate algorithms recover (tx≈0, ty≈0,
        // scale≈565/2048=0.276)?
        var bundles = new[]
        {
            new Bundle("GoblinDungeon-19:15:51 (fail)", Path.Combine(calibRoot, "Map_GoblinDungeon-20260603-191551-238-rejected-map-not-located", "02-screenshot-raw.png"), "Map_GoblinDungeon", null),
            new Bundle("GoblinDungeon-19:16:30 (fail)", Path.Combine(calibRoot, "Map_GoblinDungeon-20260603-191630-875-rejected-map-not-located", "02-screenshot-raw.png"), "Map_GoblinDungeon", null),
            new Bundle("GoblinDungeon-19:17:40 (fail)", Path.Combine(calibRoot, "Map_GoblinDungeon-20260603-191740-273-rejected-map-not-located", "02-screenshot-raw.png"), "Map_GoblinDungeon", null),
            new Bundle("Eltibule-06:14:06 (accept, full-frame)", Path.Combine(calibRoot, "AreaEltibule-20260603-061406-016-accepted",                 "02-screenshot-raw.png"), "AreaEltibule", null),
            new Bundle("Eltibule-06:14:06 (accept, cropped to mapRect)", Path.Combine(calibRoot, "AreaEltibule-20260603-061406-016-accepted",                 "02-screenshot-raw.png"), "AreaEltibule", new Rect(360, 283, 565, 561)),
        };

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
            using var cap = b.CropRect is Rect r ? new Mat(capFull, r) : capFull.Clone();
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

    private sealed record SpikeResult(
        double? Tx, double? Ty, double? Scale, double Confidence, string Note,
        Mat? Overlay = null);

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

        double bestScore = double.MinValue;
        double bestScale = 0;
        Point bestLoc = default;
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
                if (maxVal > bestScore)
                {
                    bestScore = maxVal;
                    bestScale = s;
                    bestLoc = maxLoc;
                }
            }
        }
        finally
        {
            if (edgesOnly) { capPrep.Dispose(); texPrep.Dispose(); }
        }

        if (bestScale == 0)
            return new SpikeResult(null, null, null, 0, "no valid scale (texture too large at all rungs)");
        return new SpikeResult(bestLoc.X, bestLoc.Y, bestScale, bestScore,
            $"best NCC peak {bestScore:0.000} @ scale {bestScale:0.00}");
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
        return new SpikeResult(bestTx, bestTy, bestScale, confidence,
            $"mean chamfer dist={bestCost:0.00}px  inBounds={bestInBounds}/{texEdgePts.Count}");
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
