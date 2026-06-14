using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Tests.Fixtures;

/// <summary>
/// Test-only PNG → <see cref="GrayImage"/> decode via WPF's WIC
/// (<see cref="PngBitmapDecoder"/>). Used by the skippable replay fixtures to
/// load real captured screenshots/textures. The production engine never decodes
/// — capture yields raw pixels and the bundled templates ship pre-decoded — so
/// this WIC dependency is confined to the test project (<c>UseWPF</c>).
/// </summary>
public static class WicImageLoader
{
    public static GrayImage LoadGray(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];

        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = converted.PixelWidth;
        int h = converted.PixelHeight;
        int stride = w * 4;
        var bgra = new byte[stride * h];
        converted.CopyPixels(bgra, stride, 0);

        var gray = new byte[w * h];
        for (int i = 0; i < w * h; i++)
        {
            byte b = bgra[i * 4 + 0];
            byte g = bgra[i * 4 + 1];
            byte r = bgra[i * 4 + 2];
            gray[i] = (byte)((r * 299 + g * 587 + b * 114) / 1000);   // BT.601 luma
        }
        return new GrayImage(w, h, gray);
    }

    /// <summary>
    /// Test-only PNG → raw BGRA byte buffer + dims. Sibling to <see cref="LoadGray"/>;
    /// used by mithril#1155 Phase 3 tests that need to exercise the peak-luma
    /// pre-filter against canonical calibration-bundle screenshots. Matches the
    /// <c>CapturedFrame.Bgra</c> layout: 4 bytes/pixel, B then G then R then A,
    /// row-major top-to-bottom.
    /// </summary>
    public static (byte[] Bgra, int Width, int Height) LoadBgra(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];

        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = converted.PixelWidth;
        int h = converted.PixelHeight;
        int stride = w * 4;
        var bgra = new byte[stride * h];
        converted.CopyPixels(bgra, stride, 0);
        return (bgra, w, h);
    }
}
