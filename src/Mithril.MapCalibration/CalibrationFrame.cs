namespace Mithril.MapCalibration;

/// <summary>
/// Which pixel frame an <see cref="AreaCalibration"/>'s projection outputs into.
/// Persisted on Schema 2+ records (mithril#1076); Schema-1 records infer this
/// at load time from <see cref="AreaCalibration.Source"/> per the table in
/// <c>docs/planning/calibration-1076-pixel-frame-typing/spec.md</c> §7.2.
///
/// <para>The split exists because, pre-#1076, the single projection method on
/// <c>AreaCalibration</c> returned two different things depending on the
/// producer: AutoCalibration-RANSAC fits landed in base-texture pixel coords,
/// while the Legolas walkthrough wizard fits landed in overlay-window pixel
/// coords. The two frames are not interchangeable; the catalyst bug at #1076
/// was a drift check silently comparing one against the other. The
/// <see cref="WorldToTextureCalibration"/> / <see cref="WorldToOverlayCalibration"/>
/// pair replaces the single-method shape with frame-typed return values.</para>
/// </summary>
public enum CalibrationFrame
{
    /// <summary>
    /// Projection outputs into the canonical base-texture pixel frame
    /// (<see cref="TexturePixel"/>). Origin = top-left of the texture asset.
    /// Produced by the AutoCalibration RANSAC solve (post-PR-2 once that ships)
    /// and by the hand-authored bundled-baseline fits committed in
    /// <c>BundledData/map-calibration-baseline.json</c>.
    /// </summary>
    Texture = 0,

    /// <summary>
    /// Projection outputs into the Mithril overlay window's pixel frame
    /// (<see cref="OverlayPixel"/>). Origin = top-left of the overlay window
    /// at calibration time. Produced by Legolas's interactive calibration
    /// walkthrough; the value depends on the overlay's size + placement at
    /// solve time and is the same coordinate system the overlay renderer
    /// consumes for marker drawing.
    /// </summary>
    Overlay = 1,
}
