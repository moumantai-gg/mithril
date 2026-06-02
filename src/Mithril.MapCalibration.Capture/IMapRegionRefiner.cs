using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Locates the map's true sub-rect inside a captured frame (spec §4 step 4) via
/// texture registration, so eyeball framing slop + per-zone letterboxing are
/// absorbed before the solve.
/// </summary>
public interface IMapRegionRefiner
{
    /// <summary>
    /// Find where <paramref name="baseTexture"/> sits inside
    /// <paramref name="capturedGray"/>. The acceptance gate lives inside the
    /// refiner — there is no per-call score floor.
    /// </summary>
    MapRegionRefineResult Refine(GrayImage capturedGray, GrayImage baseTexture);
}
