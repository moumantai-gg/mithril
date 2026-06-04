using System;
using System.IO;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;

namespace Mithril.MapCalibration.Capture.Tests.Fixtures;

/// <summary>
/// Loader for the mithril#1061 HogansKeep-223119 corpus regression. Reads the
/// capture from the live diagnostic bundle under
/// <c>%LocalAppData%/Mithril/diagnostics/calibration/</c> and the base texture
/// from the sidecar-populated asset cache under <c>%LocalAppData%/Mithril/assets/</c>.
///
/// <para><b>Why LocalAppData and not a checked-in fixture:</b> the capture is a
/// screenshot of Project Gorgon's in-game map UI and the base texture is decoded
/// PG art — both copyrighted, neither shippable in the repo. The asset-decoding
/// architecture (mithril#921 / #931) is built on the same principle: ship
/// canonical hashes, not PG art. Tests that need the real corpus load it from
/// the developer's local install at runtime; tests run on a clean checkout (CI,
/// new contributor) skip cleanly via <see cref="IsAvailable"/>.</para>
///
/// <para><b>How to populate it locally:</b> with Mithril running against PG,
/// load the in-game map at HogansKeepBasement and trigger an auto-calibrate
/// attempt. The bundle lands at
/// <c>%LocalAppData%/Mithril/diagnostics/calibration/Map_HogansKeepBasement-&lt;timestamp&gt;-&lt;outcome&gt;/</c>;
/// the asset-extractor sidecar populates the base texture at
/// <c>%LocalAppData%/Mithril/assets/map-texture-Map_HogansKeepBasement.{bin,json}</c>.</para>
/// </summary>
internal static class HogansKeepCorpusFixture
{
    public const string AreaKey = "Map_HogansKeepBasement";

    /// <summary>Calibration-diagnostics bundle prefix — the specific bundle this regression locks against.</summary>
    private const string BundlePrefix = "Map_HogansKeepBasement-20260603-223119-";

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string CalibrationDiagnosticsDir =>
        Path.Combine(LocalAppData, "Mithril", "diagnostics", "calibration");

    private static string AssetCacheDir =>
        Path.Combine(LocalAppData, "Mithril", "assets");

    /// <summary>
    /// True iff the live corpus is reachable on this machine — both the bundle
    /// subdirectory holding <c>03-screenshot-gray.png</c> and the asset-cache
    /// texture pair exist. False on clean checkouts (CI, new contributor) →
    /// callers early-return without failing.
    /// </summary>
    public static bool IsAvailable => TryResolveCapturePath() is not null
                                       && File.Exists(TextureBinPath)
                                       && File.Exists(TextureJsonPath);

    private static string TextureBinPath =>
        Path.Combine(AssetCacheDir, $"map-texture-{AreaKey}.bin");

    private static string TextureJsonPath =>
        Path.Combine(AssetCacheDir, $"map-texture-{AreaKey}.json");

    private static string? TryResolveCapturePath()
    {
        if (!Directory.Exists(CalibrationDiagnosticsDir)) return null;
        foreach (var dir in Directory.EnumerateDirectories(CalibrationDiagnosticsDir, BundlePrefix + "*"))
        {
            var png = Path.Combine(dir, "03-screenshot-gray.png");
            if (File.Exists(png)) return png;
        }
        return null;
    }

    public static GrayImage LoadCapture()
    {
        var path = TryResolveCapturePath()
                   ?? throw new InvalidOperationException(
                       "HogansKeep corpus capture not found under "
                       + CalibrationDiagnosticsDir
                       + $" (expected a subdir starting with {BundlePrefix}). "
                       + "Check IsAvailable first.");
        return PngFixtureLoader.LoadGray(path);
    }

    public static GrayImage? LoadTexture()
    {
        if (!File.Exists(TextureBinPath) || !File.Exists(TextureJsonPath))
            throw new InvalidOperationException(
                "HogansKeep corpus base texture not found at " + AssetCacheDir
                + $" (expected map-texture-{AreaKey}.{{bin,json}}). Check IsAvailable first.");
        var provider = new CachedBaseTextureProvider(AssetCacheDir);
        return provider.TryGetBaseTexture(AreaKey);
    }
}
