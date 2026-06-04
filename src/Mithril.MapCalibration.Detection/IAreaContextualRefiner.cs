namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Refiners that need per-area state set before <see cref="IMapRegionRefiner.Refine"/>
/// (currently: the ORB-descriptor cache key in <see cref="FeatureMatchingRefiner"/>).
/// The engine probes this interface instead of hard-casting to a concrete refiner type
/// so the dispatching <c>CompositeMapRegionRefiner</c> can transparently forward the
/// call to its inner refiners.
/// </summary>
public interface IAreaContextualRefiner
{
    /// <summary>
    /// Set the area-key context for the next <see cref="IMapRegionRefiner.Refine"/> call.
    /// Implementations may treat <c>null</c> as "no per-area context" — equivalent to
    /// never having called this. Not thread-safe by contract (calibration runs
    /// single-attempt-per-hotkey-press).
    /// </summary>
    void SetAreaKey(string? areaKey);
}
