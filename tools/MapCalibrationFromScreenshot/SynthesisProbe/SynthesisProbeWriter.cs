using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal sealed class SynthesisProbeWriter : IDisposable
{
    private readonly StreamWriter _csv;
    private readonly string _outDir;

    public SynthesisProbeWriter(string outDir)
    {
        Directory.CreateDirectory(outDir);
        _outDir = outDir;
        _csv = new StreamWriter(Path.Combine(outDir, "synthesis_probe.csv"));
        _csv.WriteLine("experiment,label,scale,rot,mirror,tx,ty,J,refs_above_0.5,dominance_vs_runner_up");
    }

    public void AppendCsvRow(string experiment, string label, CandidateTransform t, JResult jr, double dominanceVsRunnerUp)
    {
        _csv.Write(experiment); _csv.Write(',');
        _csv.Write(label); _csv.Write(',');
        _csv.Write(t.Scale.ToString("R", CultureInfo.InvariantCulture)); _csv.Write(',');
        _csv.Write(t.RotRadians.ToString("R", CultureInfo.InvariantCulture)); _csv.Write(',');
        _csv.Write(t.Mirror ? "true" : "false"); _csv.Write(',');
        _csv.Write(t.Tx.ToString("R", CultureInfo.InvariantCulture)); _csv.Write(',');
        _csv.Write(t.Ty.ToString("R", CultureInfo.InvariantCulture)); _csv.Write(',');
        _csv.Write(jr.J.ToString("R", CultureInfo.InvariantCulture)); _csv.Write(',');
        _csv.Write(jr.RefsAboveHalf.ToString(CultureInfo.InvariantCulture)); _csv.Write(',');
        _csv.WriteLine(double.IsNaN(dominanceVsRunnerUp) ? "" : dominanceVsRunnerUp.ToString("R", CultureInfo.InvariantCulture));
    }

    public void WriteFieldPng(string type, double[,] field) =>
        WriteScalarPng(Path.Combine(_outDir, $"field_{type}.png"), field, vmin: -1.0, vmax: 1.0);

    public void WriteLandscapePng(string label, double[,] landscape) =>
        WriteScalarPng(Path.Combine(_outDir, $"grid_landscape_{label}.png"), landscape, vmin: Min(landscape), vmax: Max(landscape));

    private static void WriteScalarPng(string path, double[,] field, double vmin, double vmax)
    {
        int h = field.GetLength(0), w = field.GetLength(1);
        double span = (vmax - vmin) > 1e-9 ? (vmax - vmin) : 1.0;
        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            byte[] row = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double v = (field[y, x] - vmin) / span;
                    v = Math.Clamp(v, 0, 1);
                    byte g = (byte)Math.Round(v * 255);
                    row[x * 3 + 0] = g;
                    row[x * 3 + 1] = g;
                    row[x * 3 + 2] = g;
                }
                Marshal.Copy(row, 0, data.Scan0 + y * stride, stride);
            }
        }
        finally { bmp.UnlockBits(data); }
        bmp.Save(path, ImageFormat.Png);
    }

    private static double Min(double[,] f)
    {
        double m = double.PositiveInfinity;
        foreach (var v in f) if (v < m) m = v;
        return m;
    }

    private static double Max(double[,] f)
    {
        double m = double.NegativeInfinity;
        foreach (var v in f) if (v > m) m = v;
        return m;
    }

    public void Dispose() => _csv.Dispose();
}
