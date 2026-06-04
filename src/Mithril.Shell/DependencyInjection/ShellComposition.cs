using System.Collections.Frozen;
using System.IO;
using System.Net.Http;
using Arda.Abstractions.Diagnostics;
using Arda.Composition;
using Arda.Contracts.State.Health;
using Arda.Hosting;
using Arda.Wpf;
using Arda.World.Chat;
using Arda.World.Player;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture.DependencyInjection;
using Mithril.MapCalibration.DependencyInjection;
using Mithril.Overlay.DependencyInjection;
using Mithril.Shared.Audio;
using Mithril.Shared.Character;
using Mithril.Shared.DependencyInjection;
using Mithril.Shared.Diagnostics.Performance;
using Mithril.Shared.Diagnostics.Telemetry;
using Mithril.Shared.Game;
using Mithril.Shared.Hotkeys;
using Mithril.Shared.Icons;
using Mithril.Shared.MapCalibration;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.Shared.Settings;
using Mithril.Shared.Telemetry.Abstractions;
using Mithril.Shared.Telemetry.Catalog;
using Mithril.Shared.Telemetry.Export;
using Mithril.Shared.Telemetry.Hosting;
using Mithril.Shared.Telemetry.Logs;
using Mithril.Shared.Telemetry.Settings;
using Mithril.Shared.Wpf;
using Mithril.Shared.Wpf.DependencyInjection;
using Mithril.Shell.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Mithril.Shell.DependencyInjection;

/// <summary>
/// Inputs the shell service graph needs that are computed by the bootstrap before
/// the host is built (settings already loaded, cache directories resolved). Carried
/// as a record so the <em>one</em> composition is shared verbatim between the real
/// launch path (<c>Program</c>) and the headless self-check (<c>--selfcheck</c> /
/// the DI-cycle guard test) — no second hand-maintained copy of the registration
/// chain to drift out of sync (#365, Layer 2).
/// </summary>
public sealed record ShellCompositionOptions(
    string PreferencesPath,
    ISettingsStore<ShellSettings> ShellStore,
    ShellSettings ShellSettings,
    GameConfig GameConfig,
    string LogDir,
    string PerfDir,
    string CharactersRootDir,
    string ReferenceCacheDir,
    string CommunityCalibrationCacheDir,
    string IconCacheDir,
    string ShellSettingsDir,
    string MapCalibrationDir,
    string AssetCacheDir,
    Action<string>? ModuleLog = null);

public static class ShellComposition
{
    /// <summary>
    /// Top-level composition entry point. Wires the full shell service graph.
    /// The Arda pipeline (L0–L3 + L4 composition) is the sole log-processing
    /// engine; the legacy world-sim merger was retired alongside
    /// <c>Mithril.GameState</c> and <c>Mithril.WorldSim.*</c>.
    /// </summary>
    public static IServiceCollection AddMithrilApp(
        this IServiceCollection services, ShellCompositionOptions o) =>
        services.AddMithrilShell(o);

    /// <summary>
    /// The complete shell service registration, in order. This is the single source
    /// of truth for the runtime DI graph; <c>Program</c> and the self-check both call
    /// it via <see cref="AddMithrilApp"/> so the guard validates exactly what ships.
    /// </summary>
    public static IServiceCollection AddMithrilShell(
        this IServiceCollection services, ShellCompositionOptions o)
    {
        // #966 Task-3 capture-frame dump toggle. Seed the Capture project's
        // CaptureDiagnosticsOptions singleton from the persisted ShellSettings flags
        // and keep it live by mirroring PropertyChanged onto the mutable POCO (the
        // GameConfig precedent in Program.cs). Registered BELOW (before
        // AddMithrilMapCalibrationCapture) so this AddSingleton wins over that call's
        // TryAddSingleton(new CaptureDiagnosticsOptions()). Process-lifetime
        // subscription (never unsubscribed) — both instances live for the whole
        // process, same as the GameConfig mirror; no disposal ceremony needed.
        var captureDiag = MirrorCaptureDiagnostics(o.ShellSettings);

        services
            .AddMithrilSettings<UserPreferences>(o.PreferencesPath, UserPreferencesJsonContext.Default.UserPreferences)
            .AddSingleton<ISettingsStore<ShellSettings>>(o.ShellStore)
            .AddSingleton(o.ShellSettings)
            .AddSingleton<IActiveCharacterPersistence>(o.ShellSettings)
            .AddSingleton(o.GameConfig)
            // Trace-context enricher stamps trace_id / span_id onto on-disk diagnostics
            // log entries written inside an Activity scope (e.g. perf-recorder sessions,
            // OTLP-exported spans). Allocation-free no-op outside an active Activity, so
            // wiring it unconditionally is safe.
            .AddMithrilLogging(o.LogDir, new MithrilTraceContextEnricher())
            .AddMithrilPerfRecorder(o.PerfDir, sp => () => sp.GetRequiredService<ShellSettings>().VerboseFrameEvents)
            .AddMithrilGameServices()
            .AddMithrilPerCharacterStorage(o.CharactersRootDir)
            .AddMithrilReferenceData(o.ReferenceCacheDir)
            .AddMithrilCommunityCalibration(o.CommunityCalibrationCacheDir)
            .AddMithrilMapCalibration(
                o.MapCalibrationDir,
                // mithril#1041: seed the SceneAssetCache from areas.json so cold-start
                // resolution can fall back when CurrentMapScene isn't yet observed
                // (e.g. fresh install, log truncated past the Downloading Map line,
                // long offline period). IReferenceDataService.Areas is populated
                // synchronously in the constructor, so resolution is safe at first use.
                seedAreaKeys: sp => sp.GetRequiredService<Mithril.Shared.Reference.IReferenceDataService>()
                                      .Areas.Keys.ToHashSet(StringComparer.Ordinal))
            // #835 step 3: wires Mithril.Overlay's shared overlay window
            // (IOverlayWindow), marker registry (IWorldOverlayMarkers), and
            // MarkerSceneRenderer. Consumer modules (Legolas today, Gwaihir
            // tomorrow) plug drawers at activation; the window is materialised
            // lazily on first IOverlayWindow.Window access — production
            // visibility is still owned by Legolas's OverlayController +
            // MapOverlayView during migration steps 3–5 (step 6 retires
            // MapOverlayView and the new window becomes the canonical surface).
            .AddMithrilOverlay()
            // #947: shell-owned persisted capture-rect store backing the
            // Capture-defined IMapCaptureRectStore seam over ShellSettings.
            // Registered BEFORE AddMithrilMapCalibrationCapture for logical clarity
            // (the capture provider + draw controller consume it); DI lambdas are
            // lazy so strict order isn't required for correctness.
            .AddSingleton<IMapCaptureRectStore>(sp => new ShellMapCaptureRectStore(
                sp.GetRequiredService<ShellSettings>(),
                sp.GetRequiredService<ISettingsStore<ShellSettings>>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.Shell.MapCaptureRect")))
            // #966 Task 3: seed + live-mirror the capture-frame-dump toggle (see
            // MirrorCaptureDiagnostics above). Registered BEFORE
            // AddMithrilMapCalibrationCapture so this concrete instance wins over
            // that call's TryAddSingleton(new CaptureDiagnosticsOptions()).
            .AddSingleton(captureDiag)
            // #914 PR-2: map auto-capture pipeline (OS capture + trigger +
            // persistence). Registered AFTER AddMithrilOverlay (consumes
            // IOverlayWindow) and AFTER AddMithrilMapCalibration (consumes
            // IMapCalibrationService). The GameConfig-wired confidence gate
            // override inside this call wins over the engine's default gate
            // (last-registration-wins).
            // mithril#1061: settingsDir threads through to AddMithrilVersionedSettings<MapCalibrationLocateOptions>
            // so locate-stage knobs persist to <settingsDir>/map-calibration-locate.json.
            .AddMithrilMapCalibrationCapture(o.AssetCacheDir, o.ShellSettingsDir)
            // #960: BCL-only provisioner that one-click downloads the third-party
            // UABEA classdata.tpk into the asset cache (same dir the sidecar reads),
            // so icon decoding can engage without bundling the artifact. Needs the
            // shared HttpClient registered by AddMithrilReferenceData above.
            .AddSingleton<IClassDataTpkProvisioner>(sp => new ClassDataTpkProvisioner(
                o.AssetCacheDir,
                sp.GetRequiredService<HttpClient>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Tpk")))
            .AddMithrilIcons(o.IconCacheDir)
            .AddMithrilAudio()
            .AddMithrilHotkeys()
            .AddMithrilDialogs()
            .AddMithrilModuleGates()
            // Register NoOp navigator BEFORE modules so module Register() calls
            // (which run inside AddMithrilModules) can override via AddSingleton.
            // Without this ordering, the NoOp would win and CanOpen would always
            // return false — chip cross-links would render disabled.
            .AddSingleton<IReferenceNavigator, NoOpReferenceNavigator>()
            .AddMithrilModules(o.ModuleLog)
            .AddMithrilAttention()
            .AddMithrilShellUpdates()
            .AddMithrilShellViews()
            .AddMithrilItemDetail()
            .AddMithrilIngredientSources()
            .AddMithrilShellCommands();

        // Opt-in OTLP export (mithril#815). Registration order:
        //   1. AddMithrilVersionedSettings<TelemetrySettings> — registers the
        //      JSON store, loads + migrates the singleton, wires SettingsAutoSaver.
        //   2. Tag-descriptor providers — TagCatalog resolves
        //      IEnumerable<ITagDescriptorProvider> at construction; without these
        //      the catalog is empty and every tag is treated as "newly-seen".
        //   3. AddMithrilOtlpExport(settings, loggerFactory) — no-op when
        //      EnableOtlpExport=false (the default), so end users see no behaviour
        //      change. Must receive the SAME TelemetrySettings instance the
        //      versioned-settings registration produced so settings-UI mutations
        //      to TagExports flow through to the scrubber per span without
        //      restart (see NotifyPropertyChangedOptionsMonitor in TelemetryHostExtensions).
        services.AddMithrilVersionedSettings<TelemetrySettings>(
            Path.Combine(o.ShellSettingsDir, "telemetry.json"),
            TelemetrySettingsJsonContext.Default.TelemetrySettings);
        services.AddSingleton<ITagDescriptorProvider, MithrilSharedTagDescriptors>();
        services.AddSingleton<ITagDescriptorProvider, ArdaTagDescriptors>();

        // Resolve the loaded TelemetrySettings + ILoggerFactory via a temporary
        // provider. This snapshot is used by AddMithrilOtlpExport for the
        // EnableOtlpExport gate and to capture restart-required fields (endpoint,
        // headers, protocol, service-name) once at registration. The
        // IOptionsMonitor inside AddMithrilOtlpExport resolves through DI at
        // request time, so live fields (TagExports) always reflect the host's
        // singleton — UI mutations after launch flow through to the scrubber
        // without restart, even though the snapshot here is a separate instance
        // from the host's eventual singleton.
        using (var tmp = services.BuildServiceProvider())
        {
            var telemetrySettings = tmp.GetRequiredService<TelemetrySettings>();
            var loggerFactory = tmp.GetRequiredService<ILoggerFactory>();
            services.AddMithrilOtlpExport(telemetrySettings, loggerFactory);
        }

        // Telemetry settings sub-section VM (Diagnostics → Telemetry export
        // expander). The VM needs the scrubber-graph collaborators (TagCatalog,
        // NewlySeenTagsObserver, HeaderValueProtection, ExporterHealthMonitor)
        // to bind the chip-cloud / headers grid / status line. AddMithrilOtlpExport
        // only registers those when EnableOtlpExport=true, so we register fallback
        // singletons here to keep the settings UI functional in the off state —
        // critical so the user can see the master toggle and turn export ON the
        // first time. TryAddSingleton means the enabled path's registrations win
        // when present, so the SAME instances feed both the exporter and the UI.
        services.TryAddSingleton<TagCatalog>();
        services.TryAddSingleton<NewlySeenTagsObserver>();
        services.TryAddSingleton<HeaderValueProtection>();
        services.TryAddSingleton<ExporterHealthMonitor>();
        services.AddSingleton<TelemetrySettingsViewModel>();

        // L4 composition (singleton factories resolved eagerly by the bootstrap
        // below). Registered before the Arda drivers so hosted-service startup
        // order ensures all event subscriptions are wired before replay begins.
        services.AddArdaComposition(
            o.CharactersRootDir,
            recipeKeyResolverFactory: sp =>
            {
                var refData = sp.GetRequiredService<IReferenceDataService>();
                return id =>
                {
                    var key = $"recipe_{id}";
                    return refData.Recipes.TryGetValue(key, out var recipe)
                        ? recipe.InternalName ?? key
                        : key;
                };
            },
            serverFallbackFactory: sp =>
            {
                var active = sp.GetRequiredService<IActiveCharacterService>();
                return () => active.ActiveServer;
            });

        services.AddHostedService<CompositionBootstrap>();

        // Arda pipeline (L0–L3): the sole log-processing engine.
        // Uses the game root as log directory (Player.log + ChatLogs/).
        services
            .AddArda(new ArdaOptions(o.GameConfig.GameRoot))
            .AddPlayerWorld(
                itemPoolFactory: sp =>
                {
                    var refData = sp.GetRequiredService<IReferenceDataService>();
                    var keys = refData.ItemsByInternalName.Keys;
                    var identity = keys.ToFrozenDictionary(k => k, k => k, StringComparer.Ordinal);
                    return new Arda.Dispatch.InternPool(identity);
                },
                projectToGameHourFactory: _ => at => GameClock.Project(at).Hour,
                shiftsFactory: sp =>
                {
                    var catalog = sp.GetRequiredService<IShiftCatalog>();
                    return catalog.Shifts.Select(s => (s.Slug, s.StartHour)).ToList();
                })
            .AddChatWorld();

        services.AddSingleton<InventoryProjection>();

        services.AddSingleton<WorldHealthView>();
        services.AddSingleton<IWorldHealthView>(sp => sp.GetRequiredService<WorldHealthView>());
        services.AddSingleton<IAttentionSource>(sp => sp.GetRequiredService<WorldHealthView>());
        services.AddHostedService(sp => sp.GetRequiredService<WorldHealthView>());

        services.AddHostedService<ActiveCharacterLogSynchronizer>(sp => new ActiveCharacterLogSynchronizer(
            sp.GetRequiredService<Arda.Contracts.IDomainEventSubscriber>(),
            sp.GetRequiredService<IActiveCharacterService>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("ActiveChar")));

        services.AddSingleton<SessionAgreementComposer>(sp => new SessionAgreementComposer(
            sp.GetRequiredService<Arda.Contracts.IDomainEventSubscriber>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("SessionAgreement")));
        services.AddSingleton<IAttentionSource>(sp => sp.GetRequiredService<SessionAgreementComposer>());
        services.AddHostedService(sp => sp.GetRequiredService<SessionAgreementComposer>());

        return services;
    }

    /// <summary>
    /// Builds the live <see cref="Mithril.MapCalibration.Capture.CaptureDiagnosticsOptions"/>
    /// singleton seeded from the persisted <see cref="ShellSettings"/> capture-dump
    /// flags, then mirrors subsequent <see cref="ShellSettings.PropertyChanged"/>
    /// edits onto the mutable POCO so a Settings → Diagnostics checkbox flip takes
    /// effect at runtime without re-resolving the graph (#966 Task 3). Mirrors the
    /// GameConfig live-POCO precedent in <c>Program.Main</c>. The subscription is
    /// process-lifetime (never detached) — both objects live for the whole process.
    /// </summary>
    internal static Mithril.MapCalibration.Capture.CaptureDiagnosticsOptions
        MirrorCaptureDiagnostics(ShellSettings settings)
    {
        var captureDiag = new Mithril.MapCalibration.Capture.CaptureDiagnosticsOptions
        {
            DumpCalibrationBundles = settings.DumpCalibrationBundles,
        };
        settings.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ShellSettings.DumpCalibrationBundles):
                    captureDiag.DumpCalibrationBundles = settings.DumpCalibrationBundles;
                    break;
            }
        };
        return captureDiag;
    }
}
