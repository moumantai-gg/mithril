using System;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// mithril#1123: the new 3-arg <c>LoadDeviationAsField(deviation, template, rim)</c>
/// overload is what the synthesis-J orchestrator calls after lifting rim-mask
/// computation out of the per-template loop. It MUST produce a byte-identical
/// field to the existing 4-arg overload when the supplied rim mask matches what
/// the 4-arg overload would have built internally — that's what makes the
/// rim-mask-lift-out a behaviour-preserving refactor.
/// </summary>
public sealed class IconLikelihoodFieldOverloadTests
{
    private const int W = 128, H = 96;

    private static (GrayImage Deviation, IconTemplate Template) BuildFixture()
    {
        var texturePixels = SyntheticMap.MakeTexture(W, H, seed: 4242);
        var shotPixels = (byte[])texturePixels.Clone();
        // Bright icon-shaped spot — gives the deviation map predictable signal.
        SyntheticMap.BlitTeardrop(shotPixels, W, H, anchorX: 40, anchorY: 30, width: 16, height: 16, luminance: 220);

        var dev = new byte[W * H];
        for (int i = 0; i < dev.Length; i++)
        {
            int d = shotPixels[i] - texturePixels[i];
            dev[i] = d > 0 ? (byte)Math.Min(255, d) : (byte)0;
        }
        var deviation = new GrayImage(W, H, dev);
        var template = SyntheticMap.BuildTemplates(SyntheticMap.DefaultIcons).Templates[0];
        return (deviation, template);
    }

    /// <summary>
    /// Byte-equivalence is the lift-out's correctness criterion. Pass the SAME
    /// rim mask the 4-arg overload would have built — every L_t entry must match
    /// exactly.
    /// </summary>
    [Fact]
    public void LoadDeviationAsField_3arg_matches_4arg_when_rim_is_freshly_built()
    {
        var (deviation, template) = BuildFixture();

        // Build the rim mask the same way the 4-arg overload does internally.
        int n = W * H;
        var devF = new float[n];
        for (int i = 0; i < n; i++) devF[i] = deviation.Pixels[i] / 255f;
        var rim = DeviationFloodRimMask.Build(devF, W, H, IconLikelihoodField.DefaultDevThr);

        var via4arg = IconLikelihoodField.LoadDeviationAsField(
            deviation, template, applyRimMask: true, devThr: IconLikelihoodField.DefaultDevThr);
        var via3arg = IconLikelihoodField.LoadDeviationAsField(deviation, template, rim);

        via4arg.GetLength(0).Should().Be(via3arg.GetLength(0));
        via4arg.GetLength(1).Should().Be(via3arg.GetLength(1));
        for (int y = 0; y < via4arg.GetLength(0); y++)
        for (int x = 0; x < via4arg.GetLength(1); x++)
        {
            via4arg[y, x].Should().Be(via3arg[y, x],
                $"3-arg and 4-arg overloads must score identically at ({x},{y})");
        }
    }

    /// <summary>
    /// The 3-arg overload defends against shape mismatches with an explicit
    /// ArgumentException — without the guard, ScoreAll would read past the
    /// rim[] bounds (subtle silent corruption) or undermask (a 0-length array).
    /// </summary>
    [Fact]
    public void LoadDeviationAsField_3arg_throws_when_rim_length_mismatched()
    {
        var (deviation, template) = BuildFixture();
        var wrongSizedRim = new bool[42];

        Action act = () => IconLikelihoodField.LoadDeviationAsField(deviation, template, wrongSizedRim);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*rim.Length*must equal*deviation.Width*Height*");
    }
}
