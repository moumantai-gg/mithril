using System;
using System.Collections.Generic;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Computes a "rocky border" mask for an irregular-bordered area screenshot by
/// flood-filling inward from the image edges. PG outdoor maps are an irregular
/// vegetated/water blob inside a stone rim; that rim is a false-positive factory
/// for icon NCC (its rock texture matches pin templates at noise level), and a
/// rectangular map-rect can't exclude it — so RANSAC locks onto border noise
/// instead of the real interior landmarks (observed on Eltibule / KurMountains).
///
/// <para>The border is the region connected to the image edge that is <b>not</b>
/// vegetation (green-dominant) or water (blue-dominant); the interior terrain is
/// enclosed by that vegetation/water boundary, so the flood stops there. Interior
/// brown paths/structures are <em>not</em> reached because they're ringed by
/// green, not connected to the edge except through the rim. Detections whose
/// anchor falls in the masked border are dropped before RANSAC.</para>
/// </summary>
public static class BorderMask
{
    /// <summary>
    /// Returns a per-pixel mask (row-major, <c>true</c> = border / exclude) for a
    /// BGRA buffer. Classified on a <paramref name="step"/>-downsampled grid for
    /// speed; each output pixel inherits its grid cell's result.
    /// </summary>
    public static bool[] Compute(byte[] bgra, int width, int height, int step = 4)
    {
        if (step < 1) step = 1;
        var gw = (width + step - 1) / step;
        var gh = (height + step - 1) / step;

        // A grid cell is "fillable" (rock/border candidate) when it is neither
        // green-dominant (vegetation) nor blue-dominant (water).
        var fillable = new bool[gw * gh];
        for (var gy = 0; gy < gh; gy++)
        for (var gx = 0; gx < gw; gx++)
        {
            var x = Math.Min(gx * step, width - 1);
            var y = Math.Min(gy * step, height - 1);
            var i = (y * width + x) * 4;
            int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            var green = g > r + 8 && g > b + 8;
            var water = b > r + 8 && b > g + 8;
            fillable[gy * gw + gx] = !(green || water);
        }

        // Flood-fill fillable cells reachable from any image edge.
        var border = new bool[gw * gh];
        var q = new Queue<int>();
        void Enq(int gx, int gy)
        {
            if (gx < 0 || gx >= gw || gy < 0 || gy >= gh) return;
            var k = gy * gw + gx;
            if (fillable[k] && !border[k]) { border[k] = true; q.Enqueue(k); }
        }
        for (var gx = 0; gx < gw; gx++) { Enq(gx, 0); Enq(gx, gh - 1); }
        for (var gy = 0; gy < gh; gy++) { Enq(0, gy); Enq(gw - 1, gy); }
        while (q.Count > 0)
        {
            var k = q.Dequeue();
            var gx = k % gw;
            var gy = k / gw;
            Enq(gx - 1, gy);
            Enq(gx + 1, gy);
            Enq(gx, gy - 1);
            Enq(gx, gy + 1);
        }

        var mask = new bool[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            mask[y * width + x] = border[(y / step) * gw + (x / step)];
        return mask;
    }
}
