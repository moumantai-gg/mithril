using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture.Tests.Fixtures;

internal static class TestPatterns
{
    public static GrayImage UniformGray(int width, int height, byte value)
    {
        var pixels = new byte[width * height];
        Array.Fill(pixels, value);
        return new GrayImage(width, height, pixels);
    }

    public static GrayImage GenerateChecker(int width, int height, int cellSize)
    {
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                bool dark = ((x / cellSize) + (y / cellSize)) % 2 == 0;
                pixels[y * width + x] = (byte)(dark ? 32 : 224);
            }
        return new GrayImage(width, height, pixels);
    }

    /// <summary>
    /// Checker base + deterministic low-amplitude noise. The pure checker is a
    /// bad ORB target (all corners are descriptor-identical, so Lowe's ratio
    /// kills every match against a near-tie second-best). Adding seeded
    /// per-pixel noise gives each FAST corner a unique BRIEF signature without
    /// removing the corners themselves. Seeded with <paramref name="seed"/>
    /// so the test is deterministic across runs.
    /// </summary>
    public static GrayImage NoisyChecker(int width, int height, int cellSize, int seed = 1)
    {
        var rng = new Random(seed);
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                bool dark = ((x / cellSize) + (y / cellSize)) % 2 == 0;
                int baseValue = dark ? 32 : 224;
                int noisy = baseValue + rng.Next(-24, 25);
                if (noisy < 0) noisy = 0; else if (noisy > 255) noisy = 255;
                pixels[y * width + x] = (byte)noisy;
            }
        return new GrayImage(width, height, pixels);
    }

    /// <summary>
    /// Deterministic full-amplitude random-noise texture. Every pixel is
    /// independently sampled, so every FAST corner has a unique BRIEF
    /// descriptor — ORB's ideal target. Used by FM tests that need a strong
    /// inlier ratio under resize / translation, where a repetitive base
    /// pattern (e.g. <see cref="NoisyChecker"/>) leaves too many ambiguous
    /// matches surviving Lowe's ratio.
    /// </summary>
    public static GrayImage RichNoise(int width, int height, int seed = 1)
    {
        var rng = new Random(seed);
        var pixels = new byte[width * height];
        rng.NextBytes(pixels);
        return new GrayImage(width, height, pixels);
    }

    public static GrayImage Resize(GrayImage src, int newWidth, int newHeight)
        => ImageOps.Resize(src, newWidth, newHeight);

    public static GrayImage PasteInto(GrayImage background, GrayImage foreground, int originX, int originY)
    {
        var pixels = (byte[])background.Pixels.Clone();
        for (int y = 0; y < foreground.Height; y++)
        {
            int dstY = originY + y;
            if (dstY < 0 || dstY >= background.Height) continue;
            for (int x = 0; x < foreground.Width; x++)
            {
                int dstX = originX + x;
                if (dstX < 0 || dstX >= background.Width) continue;
                pixels[dstY * background.Width + dstX] = foreground.Pixels[y * foreground.Width + x];
            }
        }
        return new GrayImage(background.Width, background.Height, pixels);
    }

    public static GrayImage Rotate(GrayImage src, double degrees)
    {
        // Nearest-neighbour rotate about centre; pads with mid-gray.
        double rad = degrees * Math.PI / 180.0;
        double c = Math.Cos(rad), s = Math.Sin(rad);
        double cx = src.Width * 0.5, cy = src.Height * 0.5;
        var pixels = new byte[src.Width * src.Height];
        Array.Fill(pixels, (byte)128);
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                double rx = (x - cx) * c + (y - cy) * s + cx;
                double ry = -(x - cx) * s + (y - cy) * c + cy;
                int sx = (int)Math.Round(rx), sy = (int)Math.Round(ry);
                if (sx >= 0 && sx < src.Width && sy >= 0 && sy < src.Height)
                {
                    pixels[y * src.Width + x] = src.Pixels[sy * src.Width + sx];
                }
            }
        return new GrayImage(src.Width, src.Height, pixels);
    }
}
