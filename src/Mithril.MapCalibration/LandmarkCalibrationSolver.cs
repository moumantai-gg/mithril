namespace Mithril.MapCalibration;

/// <summary>
/// Pure solver: given &#8805;2 known reference points &#8212; a landmark/NPC's
/// area-local world position (ground plane X/Z) paired with where the user
/// clicked it on the 1:1 map overlay &#8212; derives the projector similarity
/// transform (<see cref="AreaCalibration"/>) for that area.
///
/// <para>Uses a closed-form 2D-similarity least-squares (Umeyama specialised
/// to 2D, uniform scale, expressed via complex arithmetic). A similarity
/// transform recovers an arbitrary <em>rotation</em> and scale, but
/// <b>not a reflection</b> &#8212; so the world-axis &#8594; (East, North)
/// <em>handedness</em> cannot be absorbed by the fit and must be chosen
/// explicitly. We solve under both handedness conventions and keep whichever
/// yields the lower RMS pixel residual; the wrong handedness would require a
/// reflection and therefore fit poorly. This makes the X/Z &#8594; compass
/// convention self-correcting from real geometry rather than an unverified
/// assumption.</para>
///
/// <para>The unit&#8596;metre scalar (engine units vs. the metres survey/treasure
/// offsets are reported in) is folded into <see cref="AreaCalibration.Scale"/>:
/// references are fed in world units, so the solved px-per-metre already absorbs
/// any constant unit&#8596;metre ratio.</para>
/// </summary>
public static class LandmarkCalibrationSolver
{
    public readonly record struct Reference(double WorldX, double WorldZ, double PixelX, double PixelY);

    /// <summary>
    /// Solves for the area calibration. Returns null when fewer than 2
    /// non-degenerate references are supplied (coincident world points carry
    /// no scale/rotation information).
    /// </summary>
    public static AreaCalibration? Solve(IReadOnlyList<Reference> references)
    {
        if (references is null || references.Count < 2) return null;

        AreaCalibration? best = null;
        // Pure rotation is absorbed by the fit; only the two handedness classes
        // are genuinely distinct. North = +Z vs North = -Z are representatives.
        foreach (var mirrorNorth in new[] { false, true })
        {
            var candidate = SolveForHandedness(references, mirrorNorth);
            if (candidate is null) continue;
            if (best is null || candidate.ResidualPixels < best.ResidualPixels)
                best = candidate;
        }
        return best;
    }

    private static AreaCalibration? SolveForHandedness(IReadOnlyList<Reference> references, bool mirrorNorth)
    {
        var n = references.Count;

        // East = WorldX, North = ±WorldZ. Encode z = East - j*North, w = px + j*py.
        double zSumRe = 0, zSumIm = 0, wSumRe = 0, wSumIm = 0;
        foreach (var r in references)
        {
            var east = r.WorldX;
            var north = mirrorNorth ? -r.WorldZ : r.WorldZ;
            zSumRe += east;
            zSumIm += -north;
            wSumRe += r.PixelX;
            wSumIm += r.PixelY;
        }
        var zMeanRe = zSumRe / n;
        var zMeanIm = zSumIm / n;
        var wMeanRe = wSumRe / n;
        var wMeanIm = wSumIm / n;

        double numRe = 0, numIm = 0, denom = 0;
        var contributing = 0;
        foreach (var r in references)
        {
            var east = r.WorldX;
            var north = mirrorNorth ? -r.WorldZ : r.WorldZ;
            var zRe = east - zMeanRe;
            var zIm = -north - zMeanIm;
            var wRe = r.PixelX - wMeanRe;
            var wIm = r.PixelY - wMeanIm;
            var mag2 = zRe * zRe + zIm * zIm;
            if (mag2 < 1e-9) continue;
            numRe += wRe * zRe + wIm * zIm;
            numIm += wIm * zRe - wRe * zIm;
            denom += mag2;
            contributing++;
        }

        if (contributing < 2 || denom < 1e-9) return null;

        var cRe = numRe / denom;
        var cIm = numIm / denom;
        var scale = Math.Sqrt(cRe * cRe + cIm * cIm);
        if (scale < 1e-9) return null;

        var rotation = NormaliseAngle(Math.Atan2(cIm, cRe));
        var cZmeanRe = cRe * zMeanRe - cIm * zMeanIm;
        var cZmeanIm = cRe * zMeanIm + cIm * zMeanRe;
        var originX = wMeanRe - cZmeanRe;
        var originY = wMeanIm - cZmeanIm;

        // RMS pixel residual under the recovered transform.
        var cos = Math.Cos(rotation);
        var sin = Math.Sin(rotation);
        double sumSq = 0;
        foreach (var r in references)
        {
            var east = r.WorldX;
            var north = mirrorNorth ? -r.WorldZ : r.WorldZ;
            var rotE = east * cos + north * sin;
            var rotN = -east * sin + north * cos;
            var px = originX + scale * rotE;
            var py = originY - scale * rotN;
            var dx = px - r.PixelX;
            var dy = py - r.PixelY;
            sumSq += dx * dx + dy * dy;
        }
        var residual = Math.Sqrt(sumSq / n);

        return new AreaCalibration(scale, rotation, originX, originY, n, residual)
        {
            MirrorNorth = mirrorNorth, // carried so raw world coords re-project
        };
    }

    private static double NormaliseAngle(double radians)
    {
        var twoPi = 2 * Math.PI;
        var r = radians % twoPi;
        if (r > Math.PI) r -= twoPi;
        if (r < -Math.PI) r += twoPi;
        return r;
    }
}
