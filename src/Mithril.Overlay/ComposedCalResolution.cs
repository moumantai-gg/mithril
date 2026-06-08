using Mithril.MapCalibration;

namespace Mithril.Overlay;

/// <summary>Result of <see cref="IComposedOverlayCalibrationResolver.Resolve"/>.
/// On success, <see cref="Calibration"/> is non-null and <see cref="Path"/> says
/// how it was resolved. On miss, <see cref="Path"/> is <see cref="CalPath.None"/>
/// and <see cref="MissReason"/> carries a stable, lowercase, snake_case reason
/// suitable for feeding into <c>LogCalibrationFallback</c>'s dedup key.
///
/// <para>MissReason vocabulary (post-#1107 review fix):
/// <list type="bullet">
/// <item><c>no_scene</c> — caller passed null <c>scene</c>.</item>
/// <item><c>no_usable_calibration</c> — picker returned neither overlay-frame
/// nor texture-frame record.</item>
/// </list></para>
///
/// <para>Pre-review the vocab also included <c>null_sha</c>, <c>unsized_surface</c>,
/// and <c>catalogue_miss</c> — those branches required surface dims and an
/// <c>IMapTextureDimensions</c> catalogue for <c>MapRect</c>-based composition.
/// The post-review composer is a direct rebrand of the texture cal (no catalogue
/// lookup, no surface dims), so those failure modes can't fire.</para></summary>
public readonly record struct ComposedCalResolution(
    WorldToOverlayCalibration? Calibration,
    CalPath Path,
    string? MissReason);
