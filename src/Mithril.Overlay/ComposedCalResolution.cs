using Mithril.MapCalibration;

namespace Mithril.Overlay;

/// <summary>Result of <see cref="IComposedOverlayCalibrationResolver.Resolve"/>.
/// On success, <see cref="Calibration"/> is non-null and <see cref="Path"/> says
/// how it was resolved. On miss, <see cref="Path"/> is <see cref="CalPath.None"/>
/// and <see cref="MissReason"/> carries a stable, lowercase, snake_case reason
/// suitable for feeding into <c>LogCalibrationFallback</c>'s dedup key.
///
/// <para>MissReason vocabulary (post-#1096):
/// <list type="bullet">
/// <item><c>no_scene</c> — caller passed null <c>scene</c>.</item>
/// <item><c>no_usable_calibration</c> — picker returned neither overlay-frame
/// nor texture-frame record.</item>
/// <item><c>null_sha</c> — texture-frame record exists but <c>PixelSha256</c>
/// is null (pre-#1081 record; user recovers by re-running AutoCalibrate).</item>
/// <item><c>unsized_surface</c> — surface dims ≤ 0 (window not yet realised;
/// first frame after <c>Show()</c>; wizard viewport not laid out).</item>
/// <item><c>catalogue_miss</c> — texture-frame sha doesn't match any entry in
/// the bundled <c>CanonicalAssetHashes</c>.</item>
/// </list></para></summary>
public readonly record struct ComposedCalResolution(
    WorldToOverlayCalibration? Calibration,
    CalPath Path,
    string? MissReason);
