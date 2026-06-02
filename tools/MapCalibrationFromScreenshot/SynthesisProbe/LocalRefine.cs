namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal static class LocalRefine
{
    /// <summary>
    /// Hill-climbing ascent on (Tx, Ty, Scale). At each iteration, tries +step in
    /// each axis (and -step) and takes the move with the best J; halves the step
    /// when no axis improves. Holds Rot and Mirror fixed (those are discrete
    /// branches at the grid level).
    /// </summary>
    public static CandidateTransform Run(
        CandidateTransform seed,
        IReadOnlyDictionary<string, double[,]> fields,
        IReadOnlyList<ReferencePoint> refs,
        int maxIter,
        double stepInit)
    {
        var t = seed;
        double bestJ = JEvaluator.Evaluate(t, fields, refs).J;
        double stepXY = stepInit;
        double stepScale = stepInit * Math.Max(1e-6, seed.Scale) * 0.01;

        for (int iter = 0; iter < maxIter; iter++)
        {
            var candidates = new (CandidateTransform T, double J)[]
            {
                (t with { Tx = t.Tx + stepXY }, 0),
                (t with { Tx = t.Tx - stepXY }, 0),
                (t with { Ty = t.Ty + stepXY }, 0),
                (t with { Ty = t.Ty - stepXY }, 0),
                (t with { Scale = t.Scale + stepScale }, 0),
                (t with { Scale = Math.Max(1e-6, t.Scale - stepScale) }, 0),
            };
            int bestI = -1;
            double newBest = bestJ;
            for (int i = 0; i < candidates.Length; i++)
            {
                var j = JEvaluator.Evaluate(candidates[i].T, fields, refs).J;
                candidates[i] = (candidates[i].T, j);
                if (j > newBest) { newBest = j; bestI = i; }
            }
            if (bestI < 0)
            {
                stepXY *= 0.5;
                stepScale *= 0.5;
                if (stepXY < 0.01 && stepScale < 1e-6) break;
            }
            else
            {
                t = candidates[bestI].T;
                bestJ = newBest;
            }
        }
        return t;
    }
}
