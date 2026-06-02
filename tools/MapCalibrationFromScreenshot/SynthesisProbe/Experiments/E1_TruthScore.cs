namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;

internal static class E1_TruthScore
{
    public static JResult Run(
        IReadOnlyDictionary<string, double[,]> fieldsByType,
        IReadOnlyList<ReferencePoint> refs,
        CandidateTransform truth,
        SynthesisProbeWriter writer)
    {
        using var act = SynthesisProbeTracer.Source.StartActivity("experiment.E1");
        var jr = JEvaluator.Evaluate(truth, fieldsByType, refs);
        act?.SetTag("eval_count", 1);
        act?.SetTag("J_truth", jr.J);
        act?.SetTag("refs_above_0.5", jr.RefsAboveHalf);
        act?.SetTag("refs_off_crop", jr.RefsOffCrop);
        writer.AppendCsvRow("E1", "truth", truth, jr, dominanceVsRunnerUp: double.NaN);
        return jr;
    }
}
