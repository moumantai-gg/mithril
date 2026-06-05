namespace Mithril.MapCalibration;

/// <summary>
/// World-coord → aligned-pair-pixel transform — the input to <see cref="JEvaluator"/>.
/// Mirrors <see cref="AreaCalibration.WorldToWindow(WorldCoord)"/> at <c>CalibrationZoom = 1.0</c>;
/// intentionally a distinct record so we don't allocate a full
/// <see cref="AreaCalibration"/> per candidate in the synthesis-J top-K loop.
/// Keep <see cref="Apply"/> in sync with <see cref="AreaCalibration.WorldToWindow(WorldCoord)"/>;
/// the equivalence test in <c>CandidateTransformConversionTests</c> is the trip-wire.
/// </summary>
public readonly record struct CandidateTransform(double Scale, double RotRadians, bool Mirror, double Tx, double Ty)
{
    public TexturePixel Apply(WorldCoord world)
    {
        var east = world.X;
        var north = Mirror ? -world.Z : world.Z;
        var cos = Math.Cos(RotRadians);
        var sin = Math.Sin(RotRadians);
        var rotE = east * cos + north * sin;
        var rotN = -east * sin + north * cos;
        return new TexturePixel(Tx + Scale * rotE, Ty - Scale * rotN);
    }

    /// <summary>
    /// Wrap an <see cref="AreaCalibration"/> in candidate space WITHOUT the
    /// MapRect re-scale — the calibration is already expressed in the field's
    /// coordinate system. Use for tests / experiments where the caller built
    /// the L_t field at native texture resolution.
    /// </summary>
    public static CandidateTransform FromAreaCalibration(AreaCalibration cal) =>
        new(cal.Scale, cal.RotationRadians, cal.MirrorNorth, cal.OriginX, cal.OriginY);

    /// <summary>
    /// Convert a texture-pixel-space <see cref="AreaCalibration"/> into the
    /// aligned-pair-pixel space the synthesis-J L_t fields live in. The aligned
    /// pair is the <paramref name="mapRect"/>'s crop with origin (0, 0):
    /// <c>aligned_pair = texture * (Width/TextureWidth, Height/TextureHeight)</c>.
    /// <see cref="CandidateTransform"/> is isotropic-scale-only — if the X and Y
    /// resize ratios differ, the geometric mean is adopted and the residual
    /// anisotropy is surfaced via <paramref name="anisotropyPercent"/>. Callers
    /// should warn at &gt; ~1%.
    /// </summary>
    public static CandidateTransform FromCalibration(
        AreaCalibration cal, MapRect mapRect, out double anisotropyPercent)
    {
        double ratioX = (double)mapRect.Width / mapRect.TextureWidth;
        double ratioY = (double)mapRect.Height / mapRect.TextureHeight;
        double geom = Math.Sqrt(ratioX * ratioY);
        anisotropyPercent = 100.0 * Math.Abs(ratioX - ratioY) / geom;

        return new CandidateTransform(
            Scale: cal.Scale * geom,
            RotRadians: cal.RotationRadians,
            Mirror: cal.MirrorNorth,
            Tx: cal.OriginX * ratioX,
            Ty: cal.OriginY * ratioY);
    }

    /// <summary>Overload without the anisotropy out-param.</summary>
    public static CandidateTransform FromCalibration(AreaCalibration cal, MapRect mapRect)
        => FromCalibration(cal, mapRect, out _);
}
