using System.IO;
using Arda.Contracts;
using Arda.World.Player;
using Mithril.Shared.Character;
using Mithril.Shared.DependencyInjection;
using Mithril.Shared.Hotkeys;
using Mithril.Shared.Icons;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf.Dialogs;
using MahApps.Metro.IconPacks;
using Mithril.Shared.Settings;
using Mithril.Shared.Telemetry.Abstractions;
using Legolas.Diagnostics;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Hotkeys;
using Legolas.Rendering;
using Legolas.Services;
using Legolas.Sharing;
using Legolas.ViewModels;
using Legolas.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Legolas;

public sealed class LegolasModule : IMithrilModule
{
    public string Id => "legolas";
    public string DisplayName => "Legolas · Survey";
    public PackIconLucideKind Icon => PackIconLucideKind.Target;
    public string? IconUri => "pack://application:,,,/Legolas.Module;component/Resources/legolas.ico";
    public int SortOrder => 200;
    public ActivationMode DefaultActivation => ActivationMode.Lazy;
    public Type ViewType => typeof(LegolasPanelView);
    public Type? SettingsViewType => typeof(LegolasSettingsView);

    public void Register(IServiceCollection services)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localApp, "Mithril", "Legolas");
        var settingsPath = Path.Combine(dir, "settings.json");

        services.AddMithrilVersionedSettings<LegolasSettings>(settingsPath, LegolasSettingsJsonContext.Default.LegolasSettings);

        // #1093: declare the Legolas calibration consumer-chain tag vocabulary so the
        // TagCatalog knows about the new keys (otherwise the OTLP allowlist drops them
        // fail-closed). Companion catalog statics live in MithrilActivitySources /
        // MithrilMeters (`Mithril.Shared.Diagnostics.Telemetry`).
        services.AddSingleton<ITagDescriptorProvider, LegolasCalibrationTagDescriptors>();

        services.AddSingleton<InventoryGridSettings>(sp =>
            sp.GetRequiredService<LegolasSettings>().InventoryGrid);
        services.AddSingleton<LegolasColors>(sp =>
            sp.GetRequiredService<LegolasSettings>().Colors);
        services.AddSingleton<LegolasBrushes>();

        // Core services
        services.AddSingleton<HeldKarpOptimizer>();
        services.AddSingleton<NearestNeighbourTwoOptOptimizer>();
        services.AddSingleton<IRouteOptimizer>(sp => new AdaptiveRouteOptimizer(
            sp.GetRequiredService<HeldKarpOptimizer>(),
            sp.GetRequiredService<NearestNeighbourTwoOptOptimizer>()));
        services.AddSingleton<IMultilaterationSolver>(sp =>
            new MultilaterationSolver(sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("Legolas.Multilateration")));
        services.AddSingleton<ICoordinateProjector, CoordinateProjector>();
        services.AddSingleton<IAreaCalibrationService>(sp => new AreaCalibrationService(
            sp.GetRequiredService<Mithril.Shared.Reference.IReferenceDataService>(),
            sp.GetRequiredService<ICoordinateProjector>(),
            sp.GetRequiredService<Mithril.MapCalibration.IMapCalibrationService>()));
        services.AddSingleton<PinCalibrationCoordinator>(sp =>
            new PinCalibrationCoordinator(
                sp.GetRequiredService<IAreaCalibrationService>(),
                sp.GetRequiredService<IMapPinState>(),
                sp.GetRequiredService<IDomainEventSubscriber>(),
                sp.GetRequiredService<LegolasSettings>(),
                sp.GetService<SessionState>(),
                sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>(),
                // #919: shared good-residual threshold (registered by the shell).
                sp.GetService<Mithril.Shared.Game.GameConfig>()));

        // Session + flow controllers + VMs.
        services.AddSingleton<SessionState>(sp =>
        {
            var session = new SessionState();
            var settings = sp.GetRequiredService<LegolasSettings>();
            session.MapOpacity = settings.MapOpacity;
            session.InventoryOpacity = settings.InventoryOpacity;
            session.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(SessionState.MapOpacity):
                        settings.MapOpacity = session.MapOpacity;
                        break;
                    case nameof(SessionState.InventoryOpacity):
                        settings.InventoryOpacity = session.InventoryOpacity;
                        break;
                }
            };
            return session;
        });
        services.AddSingleton<SurveyFlowController>();
        services.AddSingleton<MotherlodeFlowController>();

        // CharacterPinAnchor — declared-position resolver (@me / character-named pin).
        // Now subscribes to Arda MapPinAdded/MapPinRemoved/AreaChanged events via
        // IDomainEventSubscriber instead of the legacy IPlayerPinTracker.
        services.AddSingleton<ICharacterPinAnchor>(sp => new CharacterPinAnchor(
            sp.GetRequiredService<IDomainEventSubscriber>(),
            sp.GetRequiredService<IMapPinState>(),
            sp.GetRequiredService<IActiveCharacterService>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("Legolas")));

        // MotherlodeMeasurementCoordinator — now subscribes to Arda domain
        // events (PlayerPositionChanged, MapPinAdded, InventoryItemRemoved)
        // via IDomainEventSubscriber instead of GameState trackers.
        services.AddSingleton<MotherlodeMeasurementCoordinator>(sp =>
            new MotherlodeMeasurementCoordinator(
                sp.GetRequiredService<IMultilaterationSolver>(),
                sp.GetRequiredService<MotherlodeFlowController>(),
                sp.GetRequiredService<IDomainEventSubscriber>(),
                sp.GetService<IReferenceDataService>(),
                sp.GetRequiredService<LegolasSettings>(),
                sp.GetService<ICharacterPinAnchor>(),
                sp.GetService<IAreaState>(),
                sp.GetService<IMapPinState>(),
                sp.GetService<IAreaCalibrationService>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("Legolas.Motherlode")));

        services.AddSingleton<LegolasReportService>(sp => new LegolasReportService(
            sp.GetRequiredService<SurveyFlowController>(),
            sp.GetRequiredService<SessionState>(),
            clock: TimeProvider.System,
            activeChar: sp.GetService<IActiveCharacterService>(),
            refData: sp.GetService<IReferenceDataService>()));
        services.AddSingleton<LegolasShareCardRenderer>(sp => new LegolasShareCardRenderer(
            sp.GetRequiredService<IReferenceDataService>(),
            sp.GetRequiredService<IIconCacheService>()));

        services.AddSingleton<ILegolasShareImportTarget>(sp => new LegolasShareImportTarget(
            sp.GetService<LegolasShareCardRenderer>(),
            sp.GetService<LegolasSettings>(),
            sp.GetService<IDialogService>(),
            sp.GetService<IReferenceDataService>(),
            sp.GetService<IModuleActivator>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("Legolas")));
        services.AddSingleton<IDeepLinkHandler>(sp =>
            new LegolasDeepLinkHandler(sp.GetRequiredService<ILegolasShareImportTarget>()));

        services.AddSingleton<LegolasWizardViewModel>();
        services.AddSingleton<LegolasSettingsViewModel>();
        services.AddSingleton<ControlPanelViewModel>();
        services.AddSingleton<InventoryOverlayViewModel>();
        services.AddSingleton<MapOverlayViewModel>(sp =>
            new MapOverlayViewModel(
                sp.GetRequiredService<SessionState>(),
                sp.GetRequiredService<ICoordinateProjector>(),
                sp.GetRequiredService<IRouteOptimizer>(),
                sp.GetRequiredService<SurveyFlowController>(),
                sp.GetRequiredService<LegolasBrushes>(),
                sp.GetRequiredService<LegolasSettings>(),
                sp.GetService<PinCalibrationCoordinator>(),
                sp.GetService<IPositionState>(),
                sp.GetService<IDomainEventSubscriber>(),
                sp.GetService<IAreaCalibrationService>(),
                sp.GetService<MotherlodeMeasurementCoordinator>(),
                sp.GetService<ICharacterPinAnchor>(),
                // #835 step 3: route Survey/Motherlode/calibration pins
                // through the shared overlay marker registry. Optional so
                // tests using the simpler ctors still build.
                sp.GetService<Mithril.Overlay.IWorldOverlayMarkers>(),
                sp.GetService<IAreaState>(),
                sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()));
        services.AddSingleton<InventoryGridSettingsViewModel>();
        services.AddSingleton<MotherlodeViewModel>();
        services.AddSingleton<NudgePadViewModel>();
        services.AddSingleton<CalibrationSessionViewModel>(sp =>
            new CalibrationSessionViewModel(
                sp.GetRequiredService<IAreaCalibrationService>(),
                sp.GetService<IDomainEventSubscriber>(),
                sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()));

        services.AddSingleton<LegolasPanelView>(sp => new LegolasPanelView
        {
            DataContext = sp.GetRequiredService<LegolasWizardViewModel>(),
        });
        services.AddSingleton<LegolasSettingsView>(sp => new LegolasSettingsView
        {
            DataContext = sp.GetRequiredService<LegolasSettingsViewModel>(),
        });

        services.AddTransient<MapOverlayView>(sp =>
        {
            var view = new MapOverlayView(
                sp.GetRequiredService<LegolasSettings>(),
                sp.GetRequiredService<SettingsAutoSaver<LegolasSettings>>(),
                sp.GetRequiredService<NudgePadViewModel>());
            view.DataContext = sp.GetRequiredService<MapOverlayViewModel>();
            return view;
        });
        services.AddTransient<InventoryOverlayView>(sp =>
        {
            var view = new InventoryOverlayView(
                sp.GetRequiredService<LegolasSettings>(),
                sp.GetRequiredService<SettingsAutoSaver<LegolasSettings>>());
            view.DataContext = sp.GetRequiredService<InventoryOverlayViewModel>();
            return view;
        });
        services.AddTransient<CalibrationOverlayView>(sp =>
        {
            var view = new CalibrationOverlayView(
                sp.GetRequiredService<LegolasSettings>(),
                sp.GetRequiredService<SettingsAutoSaver<LegolasSettings>>());
            view.DataContext = sp.GetRequiredService<CalibrationSessionViewModel>();
            return view;
        });

        services.AddSingleton<ForegroundFocusGate>();
        services.AddHostedService(sp => sp.GetRequiredService<ForegroundFocusGate>());
        services.Replace(ServiceDescriptor.Singleton<IHotkeyGate>(
            sp => sp.GetRequiredService<ForegroundFocusGate>()));

        // #835 step 6: override the platform's default FixedOverlayZoomSource(1.0)
        // with Legolas's adapter so the shared overlay's projection driver +
        // IOverlaySceneContext.Project read the live in-game zoom that the
        // title-bar slider drives (SessionState.CurrentMapZoom).
        //
        // Review iter-1 S2: RemoveAll + AddSingleton (NOT TryAdd, NOT
        // Replace). TryAdd would silently no-op if the platform's
        // FixedOverlayZoomSource(1.0) registers first (current shell
        // order: AddMithrilOverlay before AddMithrilModules) — pin
        // positions would freeze at 100% zoom. Replace would throw if
        // no descriptor exists yet (e.g. a test that wires
        // LegolasModule.Register before AddMithrilOverlay). RemoveAll
        // strips ANY prior registration (including a test stub) before
        // adding ours, so this works regardless of composition order.
        services.RemoveAll<Mithril.Overlay.IOverlayZoomSource>();
        services.AddSingleton<Mithril.Overlay.IOverlayZoomSource>(
            sp => new LegolasOverlayZoomSource(sp.GetRequiredService<SessionState>()));

        services.AddHostedService<OverlayController>();
        services.AddHostedService<AutoOverlayCoordinator>();

        // #835 step 3: register the Legolas-side IMarkerStyle drawers with the
        // shared MarkerSceneRenderer (lives in Mithril.Overlay). The hosted
        // service runs once on host start, plugs Survey/Motherlode/Motherlode-
        // guidance/Player/Calibration drawers in, then idles. Any later
        // IWorldOverlayMarkers.AddMarker call from Legolas finds a drawer for
        // these style types. (The calibration drawer is added in step 5 when
        // calibration markers switch to the marker API.)
        services.AddHostedService<LegolasOverlayDrawerHostedService>();

        services.AddSingleton<IHotkeyCommand, StartSessionCommand>();
        services.AddSingleton<IHotkeyCommand, MarkCurrentCollectedCommand>();
        services.AddSingleton<IHotkeyCommand, SetSurveyModeCommand>();
        services.AddSingleton<IHotkeyCommand, SetMotherlodeModeCommand>();
        services.AddSingleton<IHotkeyCommand, ToggleMapOverlayCommand>();
        services.AddSingleton<IHotkeyCommand, ToggleInventoryOverlayCommand>();
        services.AddSingleton<IHotkeyCommand, ToggleCalibrationOverlayCommand>();
        services.AddSingleton<IHotkeyCommand, OptimizeRouteCommand>();
        services.AddSingleton<IHotkeyCommand, ToggleMapClickThroughCommand>();
        services.AddSingleton<IHotkeyCommand, ToggleInventoryClickThroughCommand>();
        services.AddSingleton<IHotkeyCommand, ToggleAllOverlaysCommand>();
        services.AddSingleton<IHotkeyCommand, ToggleBearingWedgesCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinUpCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinUpFastCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinUpFineCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinDownCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinDownFastCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinDownFineCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinLeftCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinLeftFastCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinLeftFineCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinRightCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinRightFastCommand>();
        services.AddSingleton<IHotkeyCommand, NudgePinRightFineCommand>();
        services.AddSingleton<IHotkeyCommand, ToggleCalibrationPhaseCommand>();
        services.AddSingleton<IHotkeyCommand, ConfirmCalibrationCommand>();

        // Arda-driven ingestion services (replaces former L1 driver +
        // IPlayerWorld.Bus subscriptions). Both subscribe eagerly during
        // StartAsync via IDomainEventSubscriber.
        services.AddHostedService<PlayerLogIngestionService>();
        services.AddHostedService<ItemCollectionTracker>();

        var perfDir = Path.Combine(dir, "perf");
        services.AddSingleton(_ => new FrameTimeLogger(perfDir));
        services.AddSingleton<SurveyPerfHarness>();
        services.AddSingleton<IHotkeyCommand, ToggleFrameTimeLoggerCommand>();
        services.AddSingleton<IHotkeyCommand, RunSurveyPerfHarnessCommand>();
        services.AddSingleton<IHotkeyCommand, RunSurveyPerfHarnessTreatmentSweepCommand>();
    }
}
