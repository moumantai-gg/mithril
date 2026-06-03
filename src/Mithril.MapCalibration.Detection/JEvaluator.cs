namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Result of one J(T) evaluation over a reference pool.
/// </summary>
/// <param name="J">Sum of per-ref L_t scores (range roughly [-|refs|, |refs|] but
/// in practice positive for any reasonable fit; the wrong-fit case is sub-1).</param>
/// <param name="RefsAboveHalf">Refs whose sampled L_t ≥ 0.5 — the "N" component
/// of the gate (synthesis-J accepts iff J ≥ J_min AND RefsAboveHalf ≥ N_min).</param>
/// <param name="RefsOffCrop">Refs whose projected position fell outside the L_t
/// field. Diagnostic: a fit with many off-crop refs is geometrically suspect.</param>
/// <param name="PerRefScores">Per-ref score (same order as <c>refs</c>). For
/// debugging / bundle output; consumers don't have to use it.</param>
public readonly record struct JResult(
    double J,
    int RefsAboveHalf,
    int RefsOffCrop,
    IReadOnlyList<double> PerRefScores);

/// <summary>
/// Synthesis-J objective: sum the bicubic-sampled L_t field at each reference's
/// projected pixel. Public for shared use by production's solve engine and the
/// synthesis-probe tool — the two surfaces converge here so probe-measured J
/// and production J are computed identically.
/// </summary>
public static class JEvaluator
{
    public static JResult Evaluate(
        CandidateTransform t,
        IReadOnlyDictionary<string, double[,]> fieldsByType,
        IReadOnlyList<LandmarkReference> refs)
    {
        double j = 0;
        int aboveHalf = 0;
        int offCrop = 0;
        var perRef = new double[refs.Count];

        for (int i = 0; i < refs.Count; i++)
        {
            var r = refs[i];
            if (!fieldsByType.TryGetValue(r.Type, out var field))
            {
                perRef[i] = 0;
                continue;
            }
            var p = t.Apply(r.World);
            int h = field.GetLength(0), w = field.GetLength(1);
            if (p.X < 0 || p.Y < 0 || p.X > w - 1 || p.Y > h - 1)
            {
                offCrop++;
                perRef[i] = 0;
                continue;
            }
            var score = IconLikelihoodField.Sample(field, p.X, p.Y);
            perRef[i] = score;
            j += score;
            if (score >= 0.5) aboveHalf++;
        }

        return new JResult(j, aboveHalf, offCrop, perRef);
    }
}
