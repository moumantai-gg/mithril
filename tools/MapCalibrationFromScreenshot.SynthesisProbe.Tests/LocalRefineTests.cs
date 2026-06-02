// LocalRefineTests.cs
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class LocalRefineTests
{
    [Fact]
    public void Pulls_in_from_5px_offset_on_gaussian_peak()
    {
        int W = 64, H = 64;
        var f = new double[H, W];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                double dx = x - 32, dy = y - 32;
                f[y, x] = Math.Exp(-(dx * dx + dy * dy) / (2 * 3.0 * 3.0));
            }
        var fields = new Dictionary<string, double[,]> { ["Portal"] = f };
        var refs = new[] { new ReferencePoint("p1", "Portal", 0, 0) };
        var seed = new CandidateTransform(Scale: 1.0, RotRadians: 0.0, Mirror: false, Tx: 27.0, Ty: 32.0);

        var refined = LocalRefine.Run(seed, fields, refs, maxIter: 60, stepInit: 1.0);

        refined.Tx.Should().BeApproximately(32.0, 0.5);
        refined.Ty.Should().BeApproximately(32.0, 0.5);
    }
}
