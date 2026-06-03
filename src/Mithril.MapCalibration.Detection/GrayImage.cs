using System;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Single-channel byte image (grayscale, or alpha-only when used as a mask).
/// Row-major, top-down. The BCL-only image buffer the detection pipeline
/// operates on — decoders (System.Drawing / WIC) live in the Windows-coupled
/// capture layer + the offline tools and hand a <see cref="GrayImage"/> across
/// the boundary.
/// </summary>
public sealed class GrayImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public GrayImage(int width, int height, byte[] pixels)
    {
        if (pixels.Length != width * height)
        {
            throw new ArgumentException($"pixel buffer length {pixels.Length} != width*height={width * height}");
        }
        Width = width;
        Height = height;
        Pixels = pixels;
    }
}

/// <summary>
/// Detection result: integer-pixel top-left of the match rect + NCC score,
/// plus the sub-pixel-refined position via parabolic interpolation around the
/// score peak (X/Y if refinement was skipped, e.g. at a search edge).
/// Downstream consumers should prefer <see cref="Centre"/> which uses the
/// sub-pixel values automatically.
/// </summary>
public readonly record struct Detection(int X, int Y, double Score, double SubX, double SubY)
{
    public Detection(int x, int y, double score) : this(x, y, score, x, y) { }

    public (double Cx, double Cy) Centre(int templateWidth, int templateHeight) =>
        (SubX + templateWidth / 2.0, SubY + templateHeight / 2.0);
}
