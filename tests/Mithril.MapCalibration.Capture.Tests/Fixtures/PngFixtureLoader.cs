using Mithril.MapCalibration.Detection;
using OpenCvSharp;

namespace Mithril.MapCalibration.Capture.Tests.Fixtures;

internal static class PngFixtureLoader
{
    /// <summary>
    /// Load an 8-bit grayscale PNG into a <see cref="GrayImage"/>.
    /// Uses OpenCvSharp's PNG reader (the test project already depends on
    /// OpenCvSharp via FeatureMatchingRefiner / TextureRegistrationRefiner
    /// transitively through the Capture project's package reference).
    /// </summary>
    public static GrayImage LoadGray(string path)
    {
        using var mat = Cv2.ImRead(path, ImreadModes.Grayscale);
        if (mat.Empty())
            throw new System.IO.FileNotFoundException($"Could not load PNG: {path}", path);

        byte[] pixels = new byte[mat.Rows * mat.Cols];
        System.Runtime.InteropServices.Marshal.Copy(mat.Data, pixels, 0, pixels.Length);
        return new GrayImage(mat.Cols, mat.Rows, pixels);
    }
}
