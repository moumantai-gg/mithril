using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection.Internal;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Public probe over the on-disk icon-template cache the out-of-process
/// asset-extractor sidecar populates (issue #931). Lets a consumer (the #945
/// icon-template bootstrap in the Capture layer) ask "is the icon cache already
/// populated?" without taking a dependency on the internal manifest filename /
/// loader. The cache format itself (manifest + blob) stays owned by
/// <see cref="Internal.BundledIconTemplateLoader"/>.
/// </summary>
public static class IconTemplateCache
{
    /// <summary>
    /// True iff <paramref name="cacheDir"/> holds a readable icon-template manifest
    /// with a recorded pixel hash — i.e. the sidecar's <c>--icons</c> mode has already
    /// run. This is a "has the sidecar produced a manifest yet?" gate, not a validation
    /// of icon count or manifest contents. Used to make the bootstrap run at most once
    /// per fresh cache (don't re-launch the sidecar on every app start). Fail-soft: any
    /// read/parse error reads as "not populated".
    /// </summary>
    public static bool IsPopulated(string cacheDir, ILogger? logger = null) =>
        BundledIconTemplateLoader.ManifestPixelSha256(cacheDir, logger) is not null;
}
