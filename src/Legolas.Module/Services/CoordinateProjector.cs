using Legolas.Domain;

namespace Legolas.Services;

public sealed class CoordinateProjector : ICoordinateProjector
{
    private double _scale = 1.0;
    private double _rotation;
    private OverlayPixel _origin = OverlayPixel.Zero;

    public double Scale => _scale;
    public double RotationRadians => _rotation;
    public OverlayPixel Origin => _origin;

    public OverlayPixel Project(MetreOffset offset)
    {
        var cos = Math.Cos(_rotation);
        var sin = Math.Sin(_rotation);
        var rotE = offset.East * cos + offset.North * sin;
        var rotN = -offset.East * sin + offset.North * cos;
        return new OverlayPixel(
            _origin.X + _scale * rotE,
            _origin.Y - _scale * rotN);
    }

    public void SetOrigin(OverlayPixel origin) => _origin = origin;

    public void ApplyCalibration(AreaCalibration calibration)
    {
        // Adopt ONLY scale + rotation. The landmark solve's origin is the pixel
        // where absolute world-(0,0) lands — meaningless for the survey
        // pipeline, which projects offsets *relative to the player's anchor*.
        // Overwriting _origin with the world-origin pixel made every survey
        // project relative to world-0 instead of the player (the wrong-place /
        // wrong-direction symptom). Scale + rotation are the area-stable parts
        // worth persisting; the origin remains whatever SetOrigin / the
        // player-position click established this session.
        _scale = calibration.Scale;
        _rotation = calibration.RotationRadians;
    }

    public void CalibrateFromClick(OverlayPixel playerPixel, OverlayPixel click, MetreOffset offset)
    {
        _origin = playerPixel;

        var rOffset = offset.Magnitude;
        if (rOffset < 1e-9)
        {
            return;
        }

        var dx = click.X - playerPixel.X;
        var dy = click.Y - playerPixel.Y;
        var rPixel = Math.Sqrt(dx * dx + dy * dy);
        if (rPixel < 1e-9)
        {
            return;
        }

        _scale = rPixel / rOffset;

        var phiOffset = Math.Atan2(offset.East, offset.North);
        var phiPixel = Math.Atan2(dx, -dy);
        _rotation = NormaliseAngle(phiPixel - phiOffset);
    }

    /// <summary>
    /// Closed-form 2D similarity fit (origin + scale + rotation) from corrected
    /// (offset, pixel) pairs. Equivalent to the Umeyama algorithm specialised to 2D
    /// with uniform scale; expressed via complex-number arithmetic for compactness.
    ///
    /// Model: <c>pixel = origin + s\u00b7R(\u03b8)\u00b7offset</c>, where the projection screens
    /// north as -Y. Encoding <c>z = east \u2212 j\u00b7north</c> and <c>w = px + j\u00b7py</c>
    /// turns the model into the linear <c>w = O + c\u00b7z</c> with <c>c = s\u00b7e^(j\u03b8)</c>,
    /// whose least-squares solution is closed-form.
    ///
    /// Earlier 2-DOF implementation kept origin fixed at the user's "Set Player
    /// Position" click. That click is rarely pixel-perfect, so the residual bias
    /// was absorbed into scale and rotation \u2014 leaving a constant pixel error that
    /// dominates near-anchor projections. Solving for all four parameters at once
    /// removes the bias.
    ///
    /// Requires <c>corrections.Count &gt;= 2</c> AND at least 2 contributing
    /// (non-degenerate) corrections \u2014 i.e. centred metre vectors with non-zero
    /// magnitude. Two coincident points carry no rotation/scale information.
    /// Silently no-ops otherwise.
    /// </summary>
    public void Refit(IReadOnlyList<(MetreOffset Offset, OverlayPixel Pixel)> corrections)
    {
        if (corrections.Count < 2)
        {
            return;
        }

        double zSumRe = 0, zSumIm = 0, wSumRe = 0, wSumIm = 0;
        foreach (var (offset, pixel) in corrections)
        {
            zSumRe += offset.East;
            zSumIm += -offset.North;
            wSumRe += pixel.X;
            wSumIm += pixel.Y;
        }
        var n = corrections.Count;
        var zMeanRe = zSumRe / n;
        var zMeanIm = zSumIm / n;
        var wMeanRe = wSumRe / n;
        var wMeanIm = wSumIm / n;

        // c = \u03a3 (w' \u00b7 conj(z')) / \u03a3 |z'|\u00b2
        double numRe = 0, numIm = 0, denom = 0;
        var contributing = 0;
        foreach (var (offset, pixel) in corrections)
        {
            var zRe = offset.East - zMeanRe;
            var zIm = -offset.North - zMeanIm;
            var wRe = pixel.X - wMeanRe;
            var wIm = pixel.Y - wMeanIm;
            var mag2 = zRe * zRe + zIm * zIm;
            if (mag2 < 1e-9)
            {
                continue;
            }
            // (wRe + j\u00b7wIm) \u00b7 (zRe \u2212 j\u00b7zIm) = (wRe\u00b7zRe + wIm\u00b7zIm) + j\u00b7(wIm\u00b7zRe \u2212 wRe\u00b7zIm)
            numRe += wRe * zRe + wIm * zIm;
            numIm += wIm * zRe - wRe * zIm;
            denom += mag2;
            contributing++;
        }

        if (contributing < 2 || denom < 1e-9)
        {
            return;
        }

        var cRe = numRe / denom;
        var cIm = numIm / denom;

        var newScale = Math.Sqrt(cRe * cRe + cIm * cIm);
        if (newScale < 1e-9)
        {
            return;
        }

        // O = w\u0304 \u2212 c\u00b7z\u0304
        var cZmeanRe = cRe * zMeanRe - cIm * zMeanIm;
        var cZmeanIm = cRe * zMeanIm + cIm * zMeanRe;

        _scale = newScale;
        _rotation = NormaliseAngle(Math.Atan2(cIm, cRe));
        _origin = new OverlayPixel(wMeanRe - cZmeanRe, wMeanIm - cZmeanIm);
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
