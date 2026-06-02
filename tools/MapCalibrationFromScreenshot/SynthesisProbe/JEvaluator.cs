using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal readonly record struct JResult(double J, int RefsAboveHalf, int RefsOffCrop, IReadOnlyList<double> PerRefScores);

internal static class JEvaluator
{
    public static JResult Evaluate(
        CandidateTransform t,
        IReadOnlyDictionary<string, double[,]> fieldsByType,
        IReadOnlyList<ReferencePoint> refs)
    {
        double j = 0;
        int aboveHalf = 0;
        int offCrop = 0;
        var perRef = new double[refs.Count];

        for (int i = 0; i < refs.Count; i++)
        {
            var r = refs[i];
            if (!fieldsByType.TryGetValue(r.LandmarkType, out var field))
            {
                perRef[i] = 0;
                continue;
            }
            var p = t.Apply(new WorldCoord(r.WorldX, 0, r.WorldZ));
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
