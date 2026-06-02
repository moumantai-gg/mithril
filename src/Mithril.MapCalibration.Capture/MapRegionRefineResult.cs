using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Outcome of <see cref="IMapRegionRefiner.Refine"/>. Always populated when the
/// locator ran — the score is preserved on rejection so the engine can log it
/// and the diagnostic bundle can record it. Lets a future "map-not-located"
/// outcome self-triage close-miss (score 0.47, threshold tweak) vs catastrophic
/// (score 0.05, structural mismatch).
/// </summary>
/// <param name="AcceptedRect">The ECC-refined rect when the coarse locator's
/// score met the caller's threshold; <see langword="null"/> when the score was
/// below threshold OR no rung was viable.</param>
/// <param name="BestCoarseRect">The best-rung rect from the coarse NCC scale
/// ladder, with <see cref="MapRect.AutoDetectScore"/> and
/// <see cref="MapRect.SourceScaleFactor"/> populated. Available whether or not
/// the score met threshold; <see langword="null"/> only when the ladder had no
/// viable rung (degenerate input — capture smaller than every candidate
/// template).</param>
public sealed record MapRegionRefineResult(MapRect? AcceptedRect, MapRect? BestCoarseRect)
{
    /// <summary>Degenerate result — the locator had no viable rung.</summary>
    public static MapRegionRefineResult None { get; } = new(null, null);
}
