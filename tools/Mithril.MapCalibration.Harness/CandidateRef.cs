using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibration.Harness;

/// <summary>
/// What a calibration method emits into the shared ref set via
/// <see cref="ICandidateSink"/>. Coordinates are <b>texture-space</b> — a method
/// converts from screenshot space through
/// <see cref="CalibrationContext.ScreenshotToTexture"/> before emitting, so the
/// session and solver never re-derive the map-rect math.
///
/// <para><see cref="World"/> is null when a method finds a position it cannot
/// yet name (green-pixel detects a dot; the user assigns the landmark
/// afterward). <see cref="Confidence"/> is 1.0 for manual clicks.</para>
/// </summary>
public sealed record CandidateRef(
    TexturePixel TexturePixel,
    WorldCoord? World,
    string? LandmarkId,
    string? SuggestedName,
    string Kind,
    CalibrationRefSource Source,
    double Confidence);
