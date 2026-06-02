using System;
using System.IO;
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests.Bundle;

public class PngHeaderTests
{
    [Fact]
    public void ReadDimensions_ReturnsWidthAndHeight()
    {
        const int expectedWidth = 1006;
        const int expectedHeight = 986;

        var path = Path.Combine(Path.GetTempPath(),
            "pngheader-test-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            // Write a real PNG using System.Drawing so the IHDR is authoritative.
            using (var bmp = new System.Drawing.Bitmap(
                       expectedWidth, expectedHeight,
                       System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }

            var (w, h) = PngHeader.ReadDimensions(path);

            w.Should().Be(expectedWidth);
            h.Should().Be(expectedHeight);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ReadDimensions_ThrowsOnNonPngFile()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "pngheader-test-nonpng-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            // 32 bytes starting with the JPEG SOI marker (0xFF 0xD8 0xFF) — readable
            // length but invalid PNG signature.
            var bytes = new byte[32];
            bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF;
            File.WriteAllBytes(path, bytes);

            var act = () => PngHeader.ReadDimensions(path);

            act.Should().Throw<InvalidDataException>()
                .WithMessage("*PNG signature*");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ReadDimensions_ThrowsOnZeroDimensions()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "pngheader-test-zero-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            // Hand-crafted PNG header: valid 8-byte signature + IHDR length(13) +
            // "IHDR" + width=0 + height=0. The remaining IHDR bytes + CRC are not
            // read by our impl (it only consumes the first 24 bytes).
            var bytes = new byte[]
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
                0x00, 0x00, 0x00, 0x0D,                         // IHDR length = 13
                0x49, 0x48, 0x44, 0x52,                         // "IHDR"
                0x00, 0x00, 0x00, 0x00,                         // width = 0 (invalid)
                0x00, 0x00, 0x00, 0x00,                         // height = 0 (invalid)
            };
            File.WriteAllBytes(path, bytes);

            var act = () => PngHeader.ReadDimensions(path);

            act.Should().Throw<InvalidDataException>()
                .WithMessage("*out-of-range*");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
