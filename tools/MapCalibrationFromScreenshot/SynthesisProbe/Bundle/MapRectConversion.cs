using System;
using Mithril.MapCalibration.Detection;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;

internal static class MapRectConversion
{
    /// <summary>
    /// Convert a production-recovered AreaCalibration (texture-pixel space) plus
    /// a MapRect (texture↔screenshot mapping) into a CandidateTransform that
    /// projects world coords into the aligned-pair-pixel space the synthesis
    /// probe's L_t fields live in. The aligned pair is the MapRect's crop minus
    /// its origin — i.e., a local (0, 0) coordinate system whose pixel (0, 0)
    /// is the top-left of the crop, with dimensions (Width, Height).
    ///
    /// MapRect.TextureToScreenshot scales texture coords by (Width/TextureWidth,
    /// Height/TextureHeight) and offsets by (OriginX, OriginY). The aligned-pair
    /// space is that minus the offset:
    ///
    ///     aligned_pair_x = texture_x * (Width / TextureWidth)
    ///     aligned_pair_y = texture_y * (Height / TextureHeight)
    ///
    /// CandidateTransform is isotropic-scale-only; if the X and Y resize ratios
    /// differ, the geometric mean is used and the difference is surfaced via
    /// <paramref name="anisotropyPercent"/>. Callers should warn if it exceeds
    /// roughly 1%.
    /// </summary>
    public static CandidateTransform FromRecoveredCalibration(
        RecoveredCalibrationJson cal,
        MapRect mapRect,
        out double anisotropyPercent)
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
    public static CandidateTransform FromRecoveredCalibration(
        RecoveredCalibrationJson cal, MapRect mapRect)
        => FromRecoveredCalibration(cal, mapRect, out _);
}
