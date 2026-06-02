using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Outcome of <see cref="IMapRegionRefiner.Refine"/>.
/// <para>
/// <see cref="AcceptedRect"/> is non-null iff the refiner's gate accepted.
/// <see cref="RawFitRect"/> is non-null whenever the refiner produced a fit
/// (gate-pass-or-not) — diagnostics + the bundle's <c>LocatorBest</c> read
/// from this on the rejection branch so a future "map-not-located" outcome
/// is self-triaging.
/// <see cref="Metrics"/> mirrors <see cref="RawFitRect"/>: non-null exactly
/// when a fit exists, carrying the inlier count/ratio + recovered transform
/// parameters for both the gate and the bundle log.
/// </para>
/// </summary>
public sealed record MapRegionRefineResult(
    MapRect? AcceptedRect,
    MapRect? RawFitRect,
    LocateMetrics? Metrics)
{
    /// <summary>Degenerate result — the refiner had no usable fit.</summary>
    public static MapRegionRefineResult None { get; } = new(null, null, null);

    /// <summary>
    /// PR-1 transitional alias for <see cref="RawFitRect"/> so the in-tree
    /// <see cref="TextureRegistrationRefiner"/> keeps populating the
    /// rejection-branch rect under its existing name. PR-3 deletes this
    /// alongside the rest of the NCC-vocabulary cleanup.
    /// </summary>
    [Obsolete("Renamed to RawFitRect. Removed in PR-3.")]
    public MapRect? BestCoarseRect => RawFitRect;

    /// <summary>
    /// PR-1 transitional ctor — preserves the existing positional shape
    /// <c>new MapRegionRefineResult(accepted, bestCoarseRect)</c> so the
    /// existing <see cref="TextureRegistrationRefiner"/> compiles untouched
    /// in PR-1. PR-3 rewrites every call site.
    /// </summary>
    public MapRegionRefineResult(MapRect? AcceptedRect, MapRect? BestCoarseRect)
        : this(AcceptedRect, BestCoarseRect, null) { }
}
