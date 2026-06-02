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
    /// <paramref name="capturedGray"/>. The returned
    /// <see cref="MapRegionRefineResult"/> always preserves the coarse locator's
    /// best rung (when one was viable), even when the score fell below
    /// <paramref name="minScore"/> — engine logs and the diagnostic bundle read
    /// the score from <see cref="MapRegionRefineResult.BestCoarseRect"/> on
    /// rejection so close-miss vs catastrophic-mismatch is observable.
    /// </summary>
    MapRegionRefineResult Refine(GrayImage capturedGray, GrayImage baseTexture, double minScore);
}
