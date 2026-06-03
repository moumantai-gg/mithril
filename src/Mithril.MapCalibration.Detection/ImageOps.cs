using System;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// BCL-only pixel resampling + crop operations over <see cref="GrayImage"/>.
/// Lifted from the offline tool's <c>ImageIo</c> (the System.Drawing load/save
/// stays in tools); these three ops take/return <see cref="GrayImage"/> and have
/// no decoder dependency, so they belong in the in-process detection core.
/// </summary>
public static class ImageOps
{
    /// <summary>Downsamples by integer factor with box-averaging. Used to make NCC tractable on full-resolution textures.</summary>
    public static GrayImage Downsample(GrayImage src, int factor)
    {
        if (factor < 1) throw new ArgumentOutOfRangeException(nameof(factor));
        if (factor == 1) return src;
        int dw = src.Width / factor;
        int dh = src.Height / factor;
        var dst = new byte[dw * dh];
        int f2 = factor * factor;
        for (int y = 0; y < dh; y++)
        {
            for (int x = 0; x < dw; x++)
            {
                int sum = 0;
                int sx0 = x * factor;
                int sy0 = y * factor;
                for (int dy = 0; dy < factor; dy++)
                {
                    int row = (sy0 + dy) * src.Width + sx0;
                    for (int dx = 0; dx < factor; dx++)
                    {
                        sum += src.Pixels[row + dx];
                    }
                }
                dst[y * dw + x] = (byte)(sum / f2);
            }
        }
        return new GrayImage(dw, dh, dst);
    }

    /// <summary>
    /// Bilinear resize to an arbitrary target dimension. Used by the map-rect
    /// locator to try non-integer downsample factors — integer-only resampling
    /// can't span the "map fills the screen" case where the right factor is
    /// ~2.1× and the closest integers (2 and 3) bracket it badly.
    /// </summary>
    public static GrayImage Resize(GrayImage src, int newW, int newH)
    {
        if (newW <= 0 || newH <= 0) throw new ArgumentOutOfRangeException(nameof(newW));
        if (newW == src.Width && newH == src.Height) return src;
        var dst = new byte[newW * newH];
        double xRatio = (double)src.Width / newW;
        double yRatio = (double)src.Height / newH;
        for (int y = 0; y < newH; y++)
        {
            double srcY = (y + 0.5) * yRatio - 0.5;
            int y0 = (int)Math.Floor(srcY);
            int y1 = y0 + 1;
            if (y0 < 0) y0 = 0; else if (y0 >= src.Height) y0 = src.Height - 1;
            if (y1 < 0) y1 = 0; else if (y1 >= src.Height) y1 = src.Height - 1;
            double dy = srcY - Math.Floor(srcY);
            int row0 = y0 * src.Width;
            int row1 = y1 * src.Width;
            for (int x = 0; x < newW; x++)
            {
                double srcX = (x + 0.5) * xRatio - 0.5;
                int x0 = (int)Math.Floor(srcX);
                int x1 = x0 + 1;
                if (x0 < 0) x0 = 0; else if (x0 >= src.Width) x0 = src.Width - 1;
                if (x1 < 0) x1 = 0; else if (x1 >= src.Width) x1 = src.Width - 1;
                double dx = srcX - Math.Floor(srcX);
                double p00 = src.Pixels[row0 + x0];
                double p01 = src.Pixels[row0 + x1];
                double p10 = src.Pixels[row1 + x0];
                double p11 = src.Pixels[row1 + x1];
                double top = p00 * (1 - dx) + p01 * dx;
                double bot = p10 * (1 - dx) + p11 * dx;
                dst[y * newW + x] = (byte)(top * (1 - dy) + bot * dy);
            }
        }
        return new GrayImage(newW, newH, dst);
    }

    /// <summary>
    /// Rotates a gray image 180° (point reflection through the centre). The
    /// solve engine enumerates the discrete {0, π} map orientation — PG draws the
    /// texture artwork un-rotated and only the world→pixel mapping flips for
    /// 180°-areas, so a π candidate aligns the texture against the screenshot the
    /// other way up.
    /// </summary>
    public static GrayImage Rotate180(GrayImage src)
    {
        int n = src.Width * src.Height;
        var dst = new byte[n];
        for (int i = 0; i < n; i++) dst[i] = src.Pixels[n - 1 - i];
        return new GrayImage(src.Width, src.Height, dst);
    }

    /// <summary>
    /// Crops a rectangular region out of a gray image. Used to restrict NCC
    /// to the visible map area when a map-rect is supplied, avoiding spurious
    /// detections in the UI chrome around the map (every detection outside
    /// the map-rect is guaranteed noise and competes with real matches in
    /// the RANSAC pool).
    /// </summary>
    public static GrayImage Crop(GrayImage src, int x, int y, int w, int h)
    {
        if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > src.Width || y + h > src.Height)
        {
            throw new ArgumentOutOfRangeException(
                $"crop ({x},{y},{w},{h}) out of bounds for image {src.Width}x{src.Height}");
        }
        var dst = new byte[w * h];
        for (int row = 0; row < h; row++)
        {
            Buffer.BlockCopy(src.Pixels, (y + row) * src.Width + x, dst, row * w, w);
        }
        return new GrayImage(w, h, dst);
    }
}
