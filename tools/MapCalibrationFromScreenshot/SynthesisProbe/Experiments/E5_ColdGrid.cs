using Mithril.MapCalibration.Detection;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;

internal sealed record E5Report(double BestDistanceToTruthPx, double JBestAfterRefine, IReadOnlyList<(CandidateTransform T, double J, double DistanceToTruth)> Top8AfterRefine);

internal static class E5_ColdGrid
{
    /// <summary>
    /// Compute a tight scaleBracket around an expected scale (typically derived
    /// from the MapRect's resize ratio + production AreaCalibration). Excludes the
    /// tiny-scale degeneracy by construction — the synthesis objective's worst
    /// failure mode is at scales orders of magnitude below truth, which never
    /// arise inside a physically-plausible bracket.
    /// </summary>
    public static (double Min, double Max) BracketAroundExpected(double expected, double fractionAbove)
        => (expected * (1.0 - fractionAbove), expected * (1.0 + fractionAbove));

    public static E5Report Run(
        IReadOnlyDictionary<string, double[,]> fields,
        IReadOnlyList<LandmarkReference> refs,
        CandidateTransform truth,
        (double Min, double Max) scaleBracket,
        int scaleSamples,
        int cropWidth, int cropHeight,
        int gridStepPx,
        int templateSizePx,
        SynthesisProbeWriter writer)
    {
        using var act = SynthesisProbeTracer.Source.StartActivity("experiment.E5");
        var rots = new[] { 0.0, Math.PI };
        var mirrors = new[] { false, true };

        var scales = new double[scaleSamples];
        for (int i = 0; i < scaleSamples; i++)
        {
            double frac = (double)i / (scaleSamples - 1);
            scales[i] = scaleBracket.Min * Math.Pow(scaleBracket.Max / scaleBracket.Min, frac);
        }

        var raw = new List<(CandidateTransform T, double J)>(capacity: 4096);
        int evals = 0;
        foreach (var mirror in mirrors)
            foreach (var rot in rots)
                foreach (var scale in scales)
                    for (int ty = gridStepPx / 2; ty < cropHeight; ty += gridStepPx)
                        for (int tx = gridStepPx / 2; tx < cropWidth; tx += gridStepPx)
                        {
                            var t = new CandidateTransform(scale, rot, mirror, tx, ty);
                            var j = JEvaluator.Evaluate(t, fields, refs).J;
                            raw.Add((t, j));
                            evals++;
                        }

        var top8 = raw.OrderByDescending(p => p.J).Take(8).ToArray();

        var refined = new List<(CandidateTransform T, double J, double Distance)>();
        foreach (var (t, _) in top8)
        {
            var rt = LocalRefine.Run(t, fields, refs, maxIter: 60, stepInit: gridStepPx);
            var rj = JEvaluator.Evaluate(rt, fields, refs).J;
            double dx = rt.Tx - truth.Tx, dy = rt.Ty - truth.Ty;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            refined.Add((rt, rj, dist));
            writer.AppendCsvRow("E5", $"refined_J={rj:0.000}", rt, JEvaluator.Evaluate(rt, fields, refs), dominanceVsRunnerUp: double.NaN);
        }

        double bestDist = refined.Min(x => x.Distance);
        double bestJ = refined.Max(x => x.J);
        act?.SetTag("eval_count", evals);
        act?.SetTag("J_best_after_refine", bestJ);
        act?.SetTag("truth_in_topk", bestDist <= 5.0);
        act?.SetTag("best_distance_px", bestDist);

        return new E5Report(bestDist, bestJ, refined);
    }
}
