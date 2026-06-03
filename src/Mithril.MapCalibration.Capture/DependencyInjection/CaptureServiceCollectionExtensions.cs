using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.DependencyInjection;
using Mithril.MapCalibration.Internal;
using Mithril.Shared.Game;
using Mithril.Shared.Hotkeys;

namespace Mithril.MapCalibration.Capture.DependencyInjection;

/// <summary>
/// DI composition for the map auto-capture pipeline (mithril#914 PR-2). Registers
/// the OS-capture seams, the headless detect→solve engine (Phase 1, via
/// <see cref="DetectionServiceCollectionExtensions.AddMithrilMapCalibrationDetection"/>),
/// the orchestrator, the two hotkeys, and the background trigger.
/// </summary>
public static partial class CaptureServiceCollectionExtensions
{
    /// <summary>
    /// File name of the out-of-process asset-extractor sidecar (issue #931), as
    /// produced by <c>tools/Mithril.AssetExtractor</c>'s
    /// <c>&lt;AssemblyName&gt;mithril-asset-extract&lt;/AssemblyName&gt;</c> and
    /// published next to <c>Mithril.exe</c> in both pack variants
    /// (<c>release.yml</c> "Publish asset-extractor sidecar"). Resolved at runtime
    /// relative to <see cref="AppContext.BaseDirectory"/> (the install dir holding
    /// the shell). Exposed so the wiring test can assert the same path the
    /// registration builds.
    /// </summary>
    public const string AssetExtractorExeName = "mithril-asset-extract.exe";

    /// <summary>
    /// Hard upper bound on a single sidecar run. The sidecar decodes one area's
    /// base texture or the full icon set from the on-disk PG bundles; on a healthy
    /// install both complete in a couple of seconds, but a cold disk / large bundle
    /// / contended IO can stretch that. 60s is generous headroom that still kills a
    /// genuinely hung child (the timeout fail-softs to "no texture" → gate rejects).
    /// </summary>
    private static readonly TimeSpan AssetExtractorTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Wire the auto-capture pipeline. <paramref name="assetCacheDir"/> is the
    /// out-of-process asset-extractor sidecar cache the #931 base-texture +
    /// icon-template loaders read (BCL-only). Requires the shell to have already
    /// registered the cross-cutting singletons it consumes: <see cref="GameConfig"/>,
    /// <see cref="Mithril.Overlay.IOverlayWindow"/>,
    /// <see cref="Arda.Contracts.IDomainEventSubscriber"/>,
    /// <see cref="Arda.World.Player.IMapState"/> (the engine reads
    /// <c>CurrentMapAsset</c> / <c>CurrentSceneFriendlyName</c> for per-scene
    /// keying, #1021), and
    /// <see cref="Mithril.Shared.Reference.IReferenceDataService"/>.
    ///
    /// <para>#947: the capture region is a SHELL-persisted desktop rect, sourced
    /// independently of any window (the previous #940 live-overlay-bounds model
    /// returned null whenever the overlay wasn't shown). The shell registers
    /// <see cref="IMapCaptureRectStore"/> (over <c>ShellSettings</c>); this method
    /// consumes it optionally so the graph still builds in test setups without the
    /// shell. The overlay's own <c>WindowLayoutBinder</c> (→ <c>LegolasSettings</c>)
    /// is untouched; full overlay-reads-shell-store consolidation is a follow-up.</para>
    ///
    /// <para>#1021: the engine now resolves
    /// <see cref="Arda.World.Player.IMapState"/> instead of
    /// <see cref="Arda.World.Player.IAreaState"/> so the per-scene
    /// <c>CurrentMapAsset</c> + <c>CurrentSceneFriendlyName</c> drive texture
    /// + reference lookups (texture is keyed on <c>Map_&lt;X&gt;</c>, NPC
    /// filtering is sub-zone-aware). The shell registers
    /// <see cref="Arda.World.Player.IMapState"/> via
    /// <c>PlayerWorldExtensions.AddArdaPlayerWorld</c>.</para>
    /// </summary>
    public static IServiceCollection AddMithrilMapCalibrationCapture(
        this IServiceCollection services,
        string assetCacheDir,
        string? pgVersion = null)
    {
        if (string.IsNullOrWhiteSpace(assetCacheDir))
            throw new System.ArgumentException("assetCacheDir required", nameof(assetCacheDir));

        // Detection tier (Phase-1 detect→solve engine + #931 cache providers +
        // FeatureMatchingRefiner + ORB descriptor cache) — spec: detection project split.
        services.AddMithrilMapCalibrationDetection(assetCacheDir, pgVersion);

        // GameConfig-wired gate override (Task 23). Registered AFTER the engine so
        // last-registration-wins makes this the resolved ICalibrationConfidenceGate
        // (honours the user-tunable GameConfig.CalibrationGoodResidualPx).
        services.AddSingleton<ICalibrationConfidenceGate>(sp =>
            BuildConfidenceGate(sp.GetRequiredService<GameConfig>()));

        // Capture region = SHELL-persisted desktop rect (#947), stored in physical
        // pixels (resolved at snip-confirm time from the snip window's own device
        // scale), so the provider returns it verbatim — no read-time DPI / monitor
        // enumeration. Sourced independently of any window, so it survives regardless
        // of overlay-window state (the #947 fix). IMapCaptureRectStore is registered
        // shell-side (ShellMapCaptureRectStore over ShellSettings); resolved as
        // optional here so unit-test graphs without the shell fail soft (provider
        // returns null).
        services.AddSingleton<IMapCaptureRegionProvider>(sp =>
            new MapCaptureRegionProvider(
                sp.GetService<IMapCaptureRectStore>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Capture.Region")));

        // OS capture seams.
        services.AddSingleton<IGameWindowLocator>(sp => new Win32GameWindowLocator(
            sp.GetRequiredService<GameConfig>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Capture.Window")));
        services.AddSingleton<IScreenCapture>(sp => new BitBltScreenCapture(
            sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Capture.Screen")));
        services.AddSingleton<CaptureValidation>();

        // #966 Task 3: capture-frame debug-dump options (OFF by default). Registered
        // only if the shell hasn't already supplied one, so a settings surface can
        // override by registering its own instance first.
        services.TryAddSingleton(new CaptureDiagnosticsOptions());

        // Diagnostic bundle sink infrastructure (#984 Block D).
        services.AddSingleton<Diagnostics.IAttemptBundleVisualizer, Diagnostics.AttemptBundleVisualizer>();
        services.AddSingleton<Diagnostics.FilesystemCalibrationAttemptBundleSink>(sp =>
            new Diagnostics.FilesystemCalibrationAttemptBundleSink(
                root: Diagnostics.CalibrationBundleDirectories.DefaultRoot,
                logger: sp.GetService<ILoggerFactory>()?.CreateLogger("MapCalibration.Bundle"),
                visualizer: sp.GetRequiredService<Diagnostics.IAttemptBundleVisualizer>()));
        services.AddSingleton(sp => new Diagnostics.CalibrationAttemptBundleSinkSelector(
            sp.GetRequiredService<CaptureDiagnosticsOptions>(),
            sp.GetRequiredService<Diagnostics.FilesystemCalibrationAttemptBundleSink>(),
            Diagnostics.NullCalibrationAttemptBundleSink.Instance));

        // Overlay blanking + the capture orchestration over it.
        services.AddSingleton<IOverlayBlanker, OverlayBlanker>();
        services.AddSingleton<ICaptureService>(sp => new CaptureService(
            sp.GetRequiredService<IScreenCapture>(),
            sp.GetRequiredService<IOverlayBlanker>(),
            sp.GetRequiredService<CaptureValidation>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Capture.Service")));

        // Reference points + the solve seam.
        services.AddSingleton<IAreaReferenceProvider>(sp => new ReferenceDataAreaReferenceProvider(
            sp.GetRequiredService<Mithril.Shared.Reference.IReferenceDataService>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Capture.References")));
        services.AddSingleton<IMapCalibrationSolver, MapCalibrationSolveEngineAdapter>();

        // #945 Gap 1: the out-of-process asset-extractor sidecar adapter the engine
        // invokes on a base-texture cache-miss (AutoCalibrationEngine resolves this
        // via the optional sp.GetService<IAssetExtractor>() below — it returned null
        // until this registration existed, so the cache never populated). Registered
        // BEFORE the engine for logical ordering (DI lambdas are lazy, so strict
        // order isn't required for correctness). BCL-only (System.Diagnostics.Process)
        // — the decoder stays out-of-process (#921), the only link is the process
        // boundary. Registered UNCONDITIONALLY (no File.Exists gate): when the exe is
        // absent (dev/F5), ProcessAssetExtractor.ExtractAsync fail-softs with
        // ExitMissingExe rather than throwing, so the graph stays deterministic.
        services.AddSingleton<IAssetExtractor>(sp => new ProcessAssetExtractor(
            exePath: Path.Combine(AppContext.BaseDirectory, AssetExtractorExeName),
            timeout: AssetExtractorTimeout,
            logger: sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Capture.AssetExtractor")));

        // The orchestrator. Resolved as both the concrete type (for the hotkey +
        // DI test) and the narrow IAutoCalibrationRunner seam.
        services.AddSingleton<AutoCalibrationEngine>(sp => new AutoCalibrationEngine(
            sp.GetRequiredService<Arda.World.Player.IMapState>(),
            sp.GetRequiredService<IGameWindowLocator>(),
            sp.GetRequiredService<IMapCaptureRegionProvider>(),
            sp.GetRequiredService<ICaptureService>(),
            sp.GetRequiredService<IMapRegionRefiner>(),
            sp.GetRequiredService<IBaseTextureProvider>(),
            sp.GetRequiredService<IAreaReferenceProvider>(),
            sp.GetRequiredService<IMapCalibrationSolver>(),
            sp.GetRequiredService<IIconTemplateProvider>(),
            sp.GetRequiredService<IMapCalibrationService>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Capture.Engine"),
            sinkSelector: sp.GetRequiredService<Diagnostics.CalibrationAttemptBundleSinkSelector>(),
            assetExtractor: sp.GetService<IAssetExtractor>(),
            gameConfig: sp.GetRequiredService<GameConfig>(),
            assetCacheDir: assetCacheDir,
            pgVersion: pgVersion));
        services.AddSingleton<IAutoCalibrationRunner>(sp => sp.GetRequiredService<AutoCalibrationEngine>());

        // Bbox draw controller (shell-side, over IOverlayWindow). On a confirmed snip
        // it persists the rect to the shell-owned IMapCaptureRectStore (#947) — the
        // authoritative persistence path — and mirrors it onto the overlay for visual
        // feedback. Store resolved optional (null in test graphs → session-only apply).
        services.AddSingleton<IMapBboxDrawController>(sp => new MapBboxDrawController(
            sp.GetRequiredService<Mithril.Overlay.IOverlayWindow>(),
            sp.GetService<IMapCaptureRectStore>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Capture.Draw")));

        // Hotkeys.
        services.AddSingleton<IHotkeyCommand>(sp =>
            new Hotkeys.CaptureCalibrateCommand(
                sp.GetRequiredService<IAutoCalibrationRunner>(),
                sp.GetRequiredService<Mithril.Overlay.IOverlayWindow>()));
        services.AddSingleton<IHotkeyCommand>(sp =>
            new Hotkeys.DrawMapBboxCommand(sp.GetRequiredService<IMapBboxDrawController>()));

        // Background auto-attempt trigger. A hosted service → ILogger is required
        // (CLAUDE.md): resolve ILoggerFactory as required and create the category
        // logger rather than threading an optional logger.
        services.AddSingleton<AutoCalibrationTrigger>(sp => new AutoCalibrationTrigger(
            sp.GetRequiredService<Arda.Contracts.IDomainEventSubscriber>(),
            sp.GetRequiredService<IAutoCalibrationRunner>(),
            sp.GetRequiredService<IMapCaptureRegionProvider>(),
            sp.GetRequiredService<IGameWindowLocator>(),
            sp.GetRequiredService<IMapCalibrationService>(),
            sp.GetRequiredService<Mithril.Overlay.IOverlayWindow>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Mithril.MapCalibration.Capture.Trigger")));
        services.AddHostedService(sp => sp.GetRequiredService<AutoCalibrationTrigger>());

        // #945 Gap 3 / #949: non-blocking icon-template cache warm-up. Runs the
        // sidecar's --icons mode once on startup when the icon cache isn't yet
        // populated, so the common case has icons ready before the first attempt (no
        // first-attempt latency). Since #949 made IIconTemplateProvider re-read the
        // cache per attempt, a same-session populate takes effect immediately — the
        // engine's own --icons demand-trigger (EnsureIconTemplatesAsync) is the
        // correctness backstop, and this warm-up is the latency optimisation.
        // Fail-soft: no exe / InstallRoot empty / non-zero exit → no icons, no throw.
        services.AddSingleton<IconTemplateBootstrap>(sp => new IconTemplateBootstrap(
            sp.GetRequiredService<IAssetExtractor>(),
            sp.GetRequiredService<GameConfig>(),
            assetCacheDir,
            pgVersion,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Mithril.MapCalibration.Capture.IconBootstrap")));
        services.AddHostedService(sp => sp.GetRequiredService<IconTemplateBootstrap>());

        return services;
    }

    /// <summary>
    /// Build the auto path's <see cref="ICalibrationConfidenceGate"/> honouring
    /// <see cref="GameConfig.CalibrationGoodResidualPx"/> (the SAME user-tunable
    /// threshold the manual path uses — PR-0 relocated it to GameConfig; spec §9),
    /// with the shipped <see cref="CalibrationConfidenceGate.DefaultInlierFloor"/>.
    /// </summary>
    internal static ICalibrationConfidenceGate BuildConfidenceGate(GameConfig cfg) =>
        new CalibrationConfidenceGate(cfg.CalibrationGoodResidualPx, CalibrationConfidenceGate.DefaultInlierFloor);

}
