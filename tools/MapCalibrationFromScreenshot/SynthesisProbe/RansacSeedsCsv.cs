using System.Globalization;
using Mithril.MapCalibration.Detection;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal static class RansacSeedsCsv
{
    public static List<(string Label, CandidateTransform T)> Read(string path)
    {
        var rows = new List<(string, CandidateTransform)>();
        foreach (var line in File.ReadAllLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',', 6);
            if (parts.Length != 6) throw new FormatException($"bad row '{line}' (want label,scale,rot,ox,oy,mirror)");
            rows.Add((parts[0],
                new CandidateTransform(
                    double.Parse(parts[1], CultureInfo.InvariantCulture),
                    double.Parse(parts[2], CultureInfo.InvariantCulture),
                    bool.Parse(parts[5]),
                    double.Parse(parts[3], CultureInfo.InvariantCulture),
                    double.Parse(parts[4], CultureInfo.InvariantCulture))));
        }
        return rows;
    }
}
