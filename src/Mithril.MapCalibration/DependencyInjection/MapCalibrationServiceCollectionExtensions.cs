using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Internal;

namespace Mithril.MapCalibration.DependencyInjection;

public static class MapCalibrationServiceCollectionExtensions
{
    /// <summary>
    /// Default residual threshold (px) at or below which a user refinement is
    /// preferred over the bundled baseline. Mirrors Legolas's long-standing
    /// <c>CalibrationGoodResidualPx</c> default; surfaced as an override on
    /// <see cref="AddMithrilMapCalibration"/> so callers can re-use whatever
    /// the user has configured in <c>LegolasSettings</c>.
    /// </summary>
    public const double DefaultGoodResidualThresholdPx = 12.0;

    /// <summary>
    /// Register <see cref="IMapCalibrationService"/> backed by a single global
    /// <c>refinements.json</c> at <paramref name="storageDirectory"/> and the
    /// bundled baseline shipped in this assembly. Also registers the
    /// <see cref="ISceneAssetCache"/> trio (cache + persistence store +
    /// <see cref="Microsoft.Extensions.Hosting.IHostedService"/> recorder) under
    /// the same directory; the cache is seeded at startup from the
    /// <c>baseline.json ∩ areas.json</c> intersection via
    /// <paramref name="seedAreaKeys"/> (the composition root projects
    /// <c>IReferenceDataService.Areas.Keys</c> into the set so this assembly
    /// stays free of the <c>Mithril.Shared</c> dependency). Idempotent —
    /// safe to register more than once (DI throws on duplicates; that's the
    /// desired fail-fast behaviour).
    /// </summary>
    public static IServiceCollection AddMithrilMapCalibration(
        this IServiceCollection services,
        string storageDirectory,
        double goodResidualThresholdPx = DefaultGoodResidualThresholdPx,
        Func<IServiceProvider, IReadOnlySet<string>>? seedAreaKeys = null)
    {
        if (string.IsNullOrWhiteSpace(storageDirectory))
            throw new ArgumentException("storageDirectory required", nameof(storageDirectory));

        services.AddSingleton<IMapCalibrationService>(sp =>
            Build(storageDirectory, goodResidualThresholdPx, sp.GetService<ILoggerFactory>()));

        // Scene-asset cache (mithril#1041) — composite-key cache of observed /
        // seeded (ParentArea, SceneFriendlyName?) → MapAssetKey pairings. Cold-
        // start fallback for the resolution helper consumed by
        // OverlayWindowService, AutoCalibrationTrigger, and AreaCalibrationService.
        services.AddSingleton(sp => new SceneAssetCacheStore(
            directory: storageDirectory,
            logger: sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.SceneAssetCacheStore")));

        services.AddSingleton<ISceneAssetCache>(sp =>
        {
            var store = sp.GetRequiredService<SceneAssetCacheStore>();
            var loggerFactory = sp.GetService<ILoggerFactory>();

            // Seed from baseline ∩ areas.json before any consumer reads the cache.
            // The seeder is idempotent on subsequent runs (observation entries
            // win via the timestamp tiebreaker).
            if (seedAreaKeys is not null)
            {
                var baseline = BundledBaselineLoader.Load(loggerFactory?.CreateLogger("Mithril.MapCalibration"));
                SceneAssetCacheSeeder.Seed(
                    store,
                    baseline,
                    seedAreaKeys(sp),
                    loggerFactory?.CreateLogger("Mithril.MapCalibration.SceneAssetCacheSeeder"));
            }
            return new SceneAssetCache(store, loggerFactory?.CreateLogger("Mithril.MapCalibration.SceneAssetCache"));
        });

        services.AddHostedService(sp =>
            new SceneAssetCacheRecorder(
                sp.GetRequiredService<Arda.Contracts.IDomainEventSubscriber>(),
                sp.GetRequiredService<ISceneAssetCache>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.SceneAssetCacheRecorder")));

        return services;
    }

    /// <summary>
    /// Construct a standalone <see cref="IMapCalibrationService"/> without going
    /// through DI. Useful for tests + ad-hoc tooling (e.g. RefreshAndValidate
    /// regenerating baseline JSON from a calibrated install) that want the same
    /// shipped composition without bootstrapping a host.
    /// </summary>
    public static IMapCalibrationService Build(
        string storageDirectory,
        double goodResidualThresholdPx = DefaultGoodResidualThresholdPx,
        ILoggerFactory? loggerFactory = null)
    {
        if (string.IsNullOrWhiteSpace(storageDirectory))
            throw new ArgumentException("storageDirectory required", nameof(storageDirectory));

        var serviceLogger = loggerFactory?.CreateLogger("Mithril.MapCalibration");
        var storeLogger = loggerFactory?.CreateLogger("Mithril.MapCalibration.Store");
        Directory.CreateDirectory(storageDirectory);
        var store = new UserRefinementStore(storageDirectory, storeLogger);
        var baseline = BundledBaselineLoader.Load(serviceLogger);
        return new MapCalibrationService(baseline, store, goodResidualThresholdPx, serviceLogger);
    }
}
