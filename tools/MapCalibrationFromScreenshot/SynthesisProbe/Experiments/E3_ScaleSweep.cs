using Mithril.MapCalibration.Detection;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;

internal static class E3_ScaleSweep
{
    public static void Run(
        IReadOnlyDictionary<string, double[,]> fieldsByType,
        IReadOnlyList<LandmarkReference> refs,
        CandidateTransform truth,
        SynthesisProbeWriter writer)
    {
        using var act = SynthesisProbeTracer.Source.StartActivity("experiment.E3");
        double jBest = double.NegativeInfinity;
        int evals = 0;
        // -25% .. +25% in 1% steps = 51 samples.
        for (int pct = -25; pct <= 25; pct++)
        {
            double factor = 1.0 + pct / 100.0;
            var t = truth with { Scale = truth.Scale * factor };
            var jr = JEvaluator.Evaluate(t, fieldsByType, refs);
            writer.AppendCsvRow("E3", $"pct={pct}", t, jr, dominanceVsRunnerUp: double.NaN);
            if (jr.J > jBest) jBest = jr.J;
            evals++;
        }
        act?.SetTag("eval_count", evals);
        act?.SetTag("J_best", jBest);
    }
}
