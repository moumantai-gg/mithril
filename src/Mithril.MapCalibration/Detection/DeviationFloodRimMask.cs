using System.Collections.Generic;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Edge-connected flood mask over a deviation array: returns true for any
/// pixel reachable from the image edge through 4-connected pixels whose
/// deviation is above the threshold. The PG rocky rim around outdoor zones
/// is the foreground component that touches the image edge; interior
/// icons are isolated foreground islands, so this mask cleanly excises the
/// rim without eating interior signal. See mithril#897 gate study (Eltibule
/// 11.3% vs colour-flood 67.6%).
///
/// <para>This is the helper shape extracted from <see cref="DeviationBlobDetector.DetectIconBlobs"/>'s
/// inline <see cref="RimMaskMode.DeviationFlood"/> branch (formerly lines 48-71).
/// Used by both production's blob detector and the synthesis-probe tool's
/// <c>IconLikelihoodField.LoadDeviationAsField</c> so they compute on the
/// same masked input.</para>
/// </summary>
public static class DeviationFloodRimMask
{
    /// <summary>
    /// Build the edge-connected foreground rim mask.
    /// </summary>
    /// <param name="dev">Deviation values, row-major (length = <paramref name="w"/> * <paramref name="h"/>).</param>
    /// <param name="w">Image width in pixels.</param>
    /// <param name="h">Image height in pixels.</param>
    /// <param name="devThr">Threshold: pixels with <c>dev[i] &gt;= devThr</c> are foreground.</param>
    /// <returns>A boolean array, same length as <paramref name="dev"/>, with true at each rim pixel.</returns>
    public static bool[] Build(float[] dev, int w, int h, double devThr)
    {
        int n = w * h;
        var rim = new bool[n];
        var q = new Queue<int>();
        void Enq(int x, int y)
        {
            if (x < 0 || x >= w || y < 0 || y >= h) return;
            int k = y * w + x;
            if (dev[k] >= devThr && !rim[k]) { rim[k] = true; q.Enqueue(k); }
        }
        for (int x = 0; x < w; x++) { Enq(x, 0); Enq(x, h - 1); }
        for (int y = 0; y < h; y++) { Enq(0, y); Enq(w - 1, y); }
        while (q.Count > 0)
        {
            int k = q.Dequeue(); int x = k % w, y = k / w;
            Enq(x - 1, y); Enq(x + 1, y); Enq(x, y - 1); Enq(x, y + 1);
        }
        return rim;
    }
}
