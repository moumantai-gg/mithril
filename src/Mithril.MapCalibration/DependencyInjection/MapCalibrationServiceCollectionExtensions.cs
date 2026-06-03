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

}
