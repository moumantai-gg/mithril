using System.Collections.Generic;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration;

/// <summary>
/// Turns a captured map frame (already cropped to its <see cref="MapRect"/>) plus
/// the aligned base texture into typed icon detections in screenshot-pixel space,
/// grouped by landmark type — the input the <see cref="TypeAwareRansacSolver"/>
/// consumes. Two implementations ship: the deviation-blob detector (the proven
/// sparse-area front-end) and a whole-image template-NCC fallback.
/// </summary>
public interface ICalibrationDetector
{
    /// <summary>
    /// Captured map (already cropped to MapRect) + base texture (aligned) →
    /// typed detections in screenshot-pixel space, grouped by landmark type.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> Detect(DetectionRequest request);
}

/// <summary>Everything a detector needs for one detect pass. BCL-only inputs (no decoder).</summary>
public sealed record DetectionRequest(
    GrayImage Screenshot,
    GrayImage BaseTexture,
    MapRect MapRect,
    IconTemplateSet Templates,
    RimMaskMode RimMask,
    double LowNcc,
    double TypeFloor,           // per-blob template-NCC acceptance gate (§8: ~0.65, not deviation-rim alone)
    BlobOptions BlobOptions)
{
    /// <summary>
    /// On-screen icon render size (px) to downscale native-resolution PG sprites
    /// (~256&#160;px) to before NCC. PG renders every map icon at one consistent
    /// size; the gate study pinned this at <b>16&#160;px</b> (the empirical
    /// sweet-spot — see <c>tools/MapCalibrationFromScreenshot/README.md</c>).
    /// <c>null</c> falls back to the <see cref="IconRenderScaler"/> aggregate-NCC
    /// sweep, which is less reliable on real assets (it can collapse to the
    /// smallest, blurriest size that spuriously correlates with everything;
    /// mithril#916). Ignored when templates are already small (synthetic fixtures).
    /// </summary>
    public int? RenderSizePx { get; init; } = 16;
}
