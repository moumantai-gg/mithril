using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibration.Harness;

/// <summary>
/// A landmark projected through the current calibration, for the verification
/// overlay. <see cref="TexturePixel"/> is where the current
/// <see cref="AreaCalibration"/> places this landmark in texture space (the WPF
/// shell maps it to screenshot space for drawing). <see cref="ResidualPx"/> is
/// the per-ref residual when this landmark is also an enabled reference, else
/// null (a pure generalization check — projected but never ref'd).
/// </summary>
public sealed record ProjectionMarker(
    string Name,
    string Kind,
    TexturePixel TexturePixel,
    double? ResidualPx);
