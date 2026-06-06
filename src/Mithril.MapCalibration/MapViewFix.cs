namespace Mithril.MapCalibration;

/// <summary>
/// A measurement of PG's live world-map view state at a moment in time: where
/// the visible region sits in base-texture-pixel coordinates and how many
/// overlay pixels cover one texture pixel. Produced by an <see cref="Detection.IMapViewProbe"/>;
/// consumed by the layer-2 composition that maps a Texture-frame projection
/// to live overlay pixels. Ephemeral — the user never sees this; it lives
/// in memory and replaces the deleted manual zoom slider.
///
/// <para>See <c>docs/planning/calibration-1095-live-view-detector/spec.md</c>
/// §4.2 for the two-layer projection model.</para>
/// </summary>
public readonly record struct MapViewFix(
    double PanTexPxX,
    double PanTexPxY,
    double ViewScale,
    double Confidence,
    DateTimeOffset MeasuredAt)
{
    /// <summary>Compose a Texture-frame pixel with this fix to produce live
    /// overlay-pixel coordinates: <c>(tex − pan) × viewScale</c>.</summary>
    public (double X, double Y) TextureToOverlay(double texPxX, double texPxY)
        => ((texPxX - PanTexPxX) * ViewScale, (texPxY - PanTexPxY) * ViewScale);
}
