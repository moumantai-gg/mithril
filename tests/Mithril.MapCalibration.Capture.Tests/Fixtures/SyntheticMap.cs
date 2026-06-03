using System;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture.Tests.Fixtures;

/// <summary>
/// Tiny synthetic-image helpers for the capture tests: a high-variance noise
/// texture (so NCC is well-defined) and a "paste a texture into a larger gray
/// canvas" blitter — the same padding trick the retired NCC-ladder tests used
/// for recoverable-origin fixtures.
/// </summary>
internal static class SyntheticMap
{
    /// <summary>A high-variance random grayscale texture.</summary>
    public static GrayImage NoisyTexture(int seed, int w, int h)
    {
        var rng = new Random(seed);
        var px = new byte[w * h];
        rng.NextBytes(px);
        return new GrayImage(w, h, px);
    }

    /// <summary>
    /// A texture with strong, survives-downsampling structure (an asymmetric bright
    /// cross + a few solid blobs over low-contrast noise) so the NCC peak stays
    /// unambiguous in x AND y after the working-resolution box-downsample. Pure noise
    /// (<see cref="NoisyTexture"/>) loses its lock when averaged ~4×; a perf test at
    /// live resolution must not depend on that. Mirrors the detector-test helper.
    /// </summary>
    public static GrayImage StructuredTexture(int seed, int w, int h)
    {
        var rng = new Random(seed);
        var px = new byte[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)rng.Next(40, 90);

        // Asymmetric bright cross — breaks translational ambiguity in both axes.
        int cx = w * 3 / 8, cy = h * 5 / 8;
        int bandX = Math.Max(2, w / 40), bandY = Math.Max(2, h / 40);
        for (int y = 0; y < h; y++)
            for (int x = cx - bandX; x <= cx + bandX; x++)
                if (x >= 0 && x < w) px[y * w + x] = 235;
        for (int x = 0; x < w; x++)
            for (int y = cy - bandY; y <= cy + bandY; y++)
                if (y >= 0 && y < h) px[y * w + x] = 235;

        // A few solid blobs at irregular positions for extra distinctiveness.
        (int bx, int by, int br)[] blobs =
        {
            (w / 6, h / 5, Math.Max(4, w / 18)),
            (w * 4 / 5, h / 3, Math.Max(4, w / 22)),
            (w * 2 / 3, h * 4 / 5, Math.Max(4, w / 16)),
        };
        foreach (var (bx, by, br) in blobs)
            for (int y = -br; y <= br; y++)
                for (int x = -br; x <= br; x++)
                    if (x * x + y * y <= br * br)
                    {
                        int px2 = bx + x, py2 = by + y;
                        if (px2 >= 0 && px2 < w && py2 >= 0 && py2 < h) px[py2 * w + px2] = 20;
                    }
        return new GrayImage(w, h, px);
    }

    /// <summary>
    /// Paste <paramref name="texture"/> into a larger mid-gray canvas at
    /// (<paramref name="atX"/>, <paramref name="atY"/>), returning the canvas.
    /// </summary>
    public static GrayImage PasteInto(GrayImage texture, int canvasW, int canvasH, int atX, int atY)
    {
        var px = new byte[canvasW * canvasH];
        Array.Fill(px, (byte)128); // mid-gray background, distinct from the noise
        for (int y = 0; y < texture.Height; y++)
        {
            int dstRow = (atY + y) * canvasW + atX;
            int srcRow = y * texture.Width;
            for (int x = 0; x < texture.Width; x++)
            {
                px[dstRow + x] = texture.Pixels[srcRow + x];
            }
        }
        return new GrayImage(canvasW, canvasH, px);
    }
}
