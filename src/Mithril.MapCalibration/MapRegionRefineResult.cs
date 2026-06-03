namespace Mithril.MapCalibration;

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
}
