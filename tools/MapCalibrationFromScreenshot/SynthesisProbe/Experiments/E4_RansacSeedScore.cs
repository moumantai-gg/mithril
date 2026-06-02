namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;

internal static class E4_RansacSeedScore
{
    public static void Run(
        IReadOnlyDictionary<string, double[,]> fieldsByType,
        IReadOnlyList<ReferencePoint> refs,
        CandidateTransform truth,
        string csvPath,
        SynthesisProbeWriter writer)
    {
        using var act = SynthesisProbeTracer.Source.StartActivity("experiment.E4");
        var seeds = RansacSeedsCsv.Read(csvPath);
        var jTruth = JEvaluator.Evaluate(truth, fieldsByType, refs);
        double jMaxSeed = double.NegativeInfinity;
        foreach (var (_, t) in seeds)
        {
            var jr = JEvaluator.Evaluate(t, fieldsByType, refs);
            if (jr.J > jMaxSeed) jMaxSeed = jr.J;
        }
        foreach (var (label, t) in seeds)
        {
            var jr = JEvaluator.Evaluate(t, fieldsByType, refs);
            double dominance = jMaxSeed > 0 ? jr.J / jMaxSeed : double.NaN;
            writer.AppendCsvRow("E4", label, t, jr, dominance);
        }
        act?.SetTag("eval_count", seeds.Count);
        act?.SetTag("J_truth", jTruth.J);
        act?.SetTag("J_max_seed", jMaxSeed);
        act?.SetTag("dominance", jMaxSeed > 0 ? jTruth.J / jMaxSeed : double.NaN);
    }
}
