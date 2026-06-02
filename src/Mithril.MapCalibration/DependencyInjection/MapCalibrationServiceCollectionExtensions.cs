using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
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
    /// bundled baseline shipped in this assembly. Idempotent &#8212; safe to
    /// register more than once (DI throws on duplicates; that's the desired
    /// fail-fast behaviour).
    /// </summary>
    public static IServiceCollection AddMithrilMapCalibration(
        this IServiceCollection services,
        string storageDirectory,
        double goodResidualThresholdPx = DefaultGoodResidualThresholdPx)
    {
        if (string.IsNullOrWhiteSpace(storageDirectory))
            throw new ArgumentException("storageDirectory required", nameof(storageDirectory));

        services.AddSingleton<IMapCalibrationService>(sp =>
            Build(storageDirectory, goodResidualThresholdPx, sp.GetService<ILoggerFactory>()));
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

    /// <summary>
    /// Register the headless detect→solve engine (Phase 1): the deviation-blob
    /// <see cref="ICalibrationDetector"/>, the
    /// <see cref="ICalibrationConfidenceGate"/>, the
    /// <see cref="MapCalibrationSolveEngine"/>, the
    /// <see cref="IIconTemplateProvider"/> (a per-attempt
    /// <see cref="Internal.CachedIconTemplateProvider"/> over the asset cache dir),
    /// and an <see cref="IBaseTextureProvider"/> over the same cache. Independent of
    /// <see cref="AddMithrilMapCalibration"/> (the persistence registration) —
    /// register either or both.
    ///
    /// <para><b>#931:</b> the icon templates + base textures are no longer shipped
    /// as embedded PG art; the out-of-process asset-extractor sidecar populates
    /// <paramref name="assetCacheDir"/> at runtime and these loaders read it
    /// BCL-only. When the dir is absent/empty the provider yields
    /// <see cref="IconTemplateSet.Empty"/> / the base-texture provider returns
    /// null — the intended fail-soft (no detections → gate rejects →
    /// safe-degrade). The optional <paramref name="pgVersion"/> keys the
    /// canonical-hash gate that guards base textures.</para>
    ///
    /// <para><b>#949:</b> the icon templates resolve via a <i>per-attempt</i>
    /// provider (re-reads the cache each call), not an eager singleton, so a
    /// same-session populate (the engine's <c>--icons</c> demand-trigger or the
    /// startup warm-up) engages on the next attempt — first-session calibration no
    /// longer needs a restart.</para>
    /// </summary>
    public static IServiceCollection AddMithrilMapCalibrationEngine(
        this IServiceCollection services,
        string assetCacheDir,
        string? pgVersion = null)
    {
        if (string.IsNullOrWhiteSpace(assetCacheDir))
            throw new ArgumentException("assetCacheDir required", nameof(assetCacheDir));

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
        return services;
    }
}
