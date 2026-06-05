using Legolas.Domain;

namespace Legolas.Services;

public interface ICoordinateProjector
{
    double Scale { get; }
    double RotationRadians { get; }
    OverlayPixel Origin { get; }

    /// <summary>
    /// Projects a metre offset (east, north) to a pixel position using the current
    /// scale, rotation, and origin. Screen-y is assumed to increase downward;
    /// positive north therefore projects to decreasing y.
    /// </summary>
    OverlayPixel Project(MetreOffset offset);

    /// <summary>
    /// Sets the player/origin pixel position without touching scale or rotation.
    /// </summary>
    void SetOrigin(OverlayPixel origin);

    /// <summary>
    /// Derives scale and rotation from a single click on a reported survey dot.
    /// Sets origin to the supplied player pixel.
    /// </summary>
    void CalibrateFromClick(OverlayPixel playerPixel, OverlayPixel click, MetreOffset offset);

    /// <summary>
    /// Refits scale and rotation from k&#8805;2 user corrections. Origin is held fixed
    /// at the last value supplied through <see cref="SetOrigin"/> or <see cref="CalibrateFromClick"/>.
    /// A closed-form Procrustes-style solution (scale: weighted least squares on
    /// magnitudes; rotation: circular mean of bearing differences) \u2014 no iteration,
    /// no dependency, numerically stable.
    /// </summary>
    void Refit(IReadOnlyList<(MetreOffset Offset, OverlayPixel Pixel)> corrections);

    /// <summary>
    /// Adopts a persisted per-area calibration's <b>scale and rotation only</b>
    /// (the area-stable parts). The origin is deliberately left untouched — it
    /// is the player's per-session anchor (<see cref="SetOrigin"/> / the
    /// position click), not the calibration's world-(0,0) pixel. Removes the
    /// scale/rotation warmup on area entry without breaking the player-relative
    /// projection model.
    /// </summary>
    void ApplyCalibration(AreaCalibration calibration);
}
