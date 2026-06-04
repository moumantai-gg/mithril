using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection.Internal;

namespace Mithril.MapCalibration.Detection.DependencyInjection;

public static class DetectionServiceCollectionExtensions
{
    /// <summary>
    /// Register the headless detect→solve engine: the deviation-blob
    /// <see cref="ICalibrationDetector"/>, the <see cref="ICalibrationConfidenceGate"/>,
    /// the <see cref="MapCalibrationSolveEngine"/>, the <see cref="IIconTemplateProvider"/>
    /// (per-attempt <see cref="CachedIconTemplateProvider"/> over the asset cache dir),
    /// an <see cref="IBaseTextureProvider"/> over the same cache, the
    /// <see cref="IMapRegionRefiner"/> backed by <see cref="FeatureMatchingRefiner"/>,
    /// and its ORB descriptor cache (<see cref="CachedOrbDescriptorProvider"/> +
    /// <see cref="OrbDescriptorWriter"/>). Independent of
    /// <see cref="Mithril.MapCalibration.DependencyInjection.MapCalibrationServiceCollectionExtensions.AddMithrilMapCalibration"/>
    /// (the persistence registration) — register either or both.
    /// </summary>
    public static IServiceCollection AddMithrilMapCalibrationDetection(
        this IServiceCollection services,
        string assetCacheDir,
        string? pgVersion = null)
    {
        if (string.IsNullOrWhiteSpace(assetCacheDir))
            throw new System.ArgumentException("assetCacheDir required", nameof(assetCacheDir));

        services.AddSingleton<IIconTemplateProvider>(sp =>
            new CachedIconTemplateProvider(
                assetCacheDir,
                sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Templates")));
        services.AddSingleton<IBaseTextureProvider>(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var gate = CanonicalAssetHashGate.Load(loggerFactory?.CreateLogger("Mithril.MapCalibration.HashGate"));
            return new CachedBaseTextureProvider(
                assetCacheDir,
                gate,
                pgVersion,
                loggerFactory?.CreateLogger("Mithril.MapCalibration.BaseTexture"));
        });
        services.AddSingleton<ICalibrationDetector, DeviationBlobCalibrationDetector>();
        services.AddSingleton<ICalibrationConfidenceGate, CalibrationConfidenceGate>();
        services.TryAddSingleton<MapCalibrationSolverOptions>();
        services.AddSingleton(sp => new MapCalibrationSolveEngine(
            sp.GetRequiredService<ICalibrationDetector>(),
            sp.GetRequiredService<ICalibrationConfidenceGate>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Engine"),
            sp.GetRequiredService<MapCalibrationSolverOptions>()));

        services.TryAddSingleton<MapCalibrationLocateOptions>();
        services.TryAddSingleton<CachedOrbDescriptorProvider>(sp =>
        {
            var opts = sp.GetRequiredService<MapCalibrationLocateOptions>();
            return new CachedOrbDescriptorProvider(
                cacheDir: assetCacheDir,
                orbParamsHash: ComputeOrbParamsHash(opts),
                logger: sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.OrbCache"));
        });
        services.TryAddSingleton<OrbDescriptorWriter>(sp =>
        {
            var opts = sp.GetRequiredService<MapCalibrationLocateOptions>();
            return new OrbDescriptorWriter(
                cacheDir: assetCacheDir,
                orbParamsHash: ComputeOrbParamsHash(opts),
                logger: sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.OrbCache"));
        });
        // mithril#1061: register both concrete refiners + the composite that
        // dispatches between them. The composite resolves as the IMapRegionRefiner
        // singleton; existing consumers see the same interface but get the
        // ORB-primary, Sobel-fallback behaviour automatically. The concrete types
        // stay singleton-registered so tests + future direct consumers can opt in.
        services.AddSingleton<FeatureMatchingRefiner>(sp =>
            new FeatureMatchingRefiner(
                options: sp.GetRequiredService<MapCalibrationLocateOptions>(),
                logger: sp.GetService<ILogger<FeatureMatchingRefiner>>(),
                cachedDescriptors: sp.GetService<CachedOrbDescriptorProvider>(),
                writer: sp.GetService<OrbDescriptorWriter>()));
        services.AddSingleton<SobelPaddedPyramidRefiner>(sp =>
            new SobelPaddedPyramidRefiner(
                options: sp.GetRequiredService<MapCalibrationLocateOptions>(),
                logger: sp.GetService<ILogger<SobelPaddedPyramidRefiner>>()));
        services.AddSingleton<IMapRegionRefiner>(sp =>
            new CompositeMapRegionRefiner(
                primary: sp.GetRequiredService<FeatureMatchingRefiner>(),
                fallback: sp.GetRequiredService<SobelPaddedPyramidRefiner>(),
                logger: sp.GetService<ILogger<CompositeMapRegionRefiner>>()));
        return services;
    }

    /// <summary>
    /// Canonical SHA-256 of the locate options that affect the ORB-descriptor
    /// cache identity. Identical formula to the prior CaptureServiceCollectionExtensions
    /// implementation (PR-2 of #1009); preserved verbatim so existing on-disk caches
    /// keyed by this hash stay valid across the project split.
    /// </summary>
    private static string ComputeOrbParamsHash(MapCalibrationLocateOptions opts)
    {
        var s = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"orb-v1|nFeatures={opts.OrbNFeatures}|loweRatio={opts.LoweRatio:F4}|ransacPx={opts.RansacReprojectionThresholdPx:F4}");
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        return System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    }
}
