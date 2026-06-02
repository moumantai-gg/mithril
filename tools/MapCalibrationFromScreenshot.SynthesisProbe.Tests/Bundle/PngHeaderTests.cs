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
}
