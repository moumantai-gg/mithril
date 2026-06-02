using Mithril.MapCalibration.Detection;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;

internal static class E2_TranslationSweep
{
    public static void Run(
        IReadOnlyDictionary<string, double[,]> fieldsByType,
        IReadOnlyList<LandmarkReference> refs,
        CandidateTransform truth,
        int templateSizePx,
        SynthesisProbeWriter writer)
    {
        using var act = SynthesisProbeTracer.Source.StartActivity("experiment.E2");
        int halfWindow = 2 * templateSizePx;
        int side = halfWindow * 2 + 1;
        var landscape = new double[side, side];

        double jBest = double.NegativeInfinity;
        int evals = 0;
        for (int dy = -halfWindow; dy <= halfWindow; dy++)
            for (int dx = -halfWindow; dx <= halfWindow; dx++)
            {
                var t = truth with { Tx = truth.Tx + dx, Ty = truth.Ty + dy };
                var jr = JEvaluator.Evaluate(t, fieldsByType, refs);
                landscape[dy + halfWindow, dx + halfWindow] = jr.J;
                if (jr.J > jBest) jBest = jr.J;
                evals++;
                if (evals % 100 == 0)
                    writer.AppendCsvRow("E2", $"dx={dx},dy={dy}", t, jr, dominanceVsRunnerUp: double.NaN);
            }
        writer.WriteLandscapePng("translation", landscape);

        act?.SetTag("eval_count", evals);
        act?.SetTag("J_best", jBest);
        act?.SetTag("window_px", halfWindow);
    }
}
