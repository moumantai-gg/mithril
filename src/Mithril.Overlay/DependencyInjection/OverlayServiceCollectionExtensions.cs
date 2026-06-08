using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Internal;
using Mithril.Overlay.Internal;

namespace Mithril.Overlay.DependencyInjection;

public static class OverlayServiceCollectionExtensions
{
    /// <summary>
    /// Register the shared overlay infrastructure: the world-coord marker
    /// registry (<see cref="IWorldOverlayMarkers"/>), the singleton overlay
    /// window service (<see cref="IOverlayWindow"/>), and a single
    /// <see cref="MarkerSceneRenderer"/> instance the migration PRs use to
    /// plug consumer-specific drawers.
    ///
    /// <para>The hosted service is registered but the overlay window is
    /// <b>not shown</b> on startup &#8212; the migration PRs that switch
    /// Legolas's overlays over will be the ones to surface it. This keeps
    /// the scaffold "registered but dormant" so the new project can ship
    /// alongside the existing Legolas overlay without overlap.</para>
    ///
    /// <para>The overlay window has window-level state (a WPF
    /// <c>Window</c> can't run headless), so unlike
    /// <c>AddMithrilMapCalibration</c> there is no parallel <c>Build()</c>
    /// for tests &#8212; tests construct
    /// <c>WorldOverlayMarkers</c> + <c>MarkerSceneRenderer</c> directly.</para>
    /// </summary>
    public static IServiceCollection AddMithrilOverlay(this IServiceCollection services)
    {
        // Marker registry — concrete singleton. Public interface is added via
        // TryAddSingleton so callers can override with a fake if needed.
        services.TryAddSingleton(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new WorldOverlayMarkers(loggerFactory?.CreateLogger("Mithril.Overlay"));
        });
        services.TryAddSingleton<IWorldOverlayMarkers>(sp => sp.GetRequiredService<WorldOverlayMarkers>());

        // Marker drawer registry. Singleton so consumer modules can call
        // RegisterDrawer<TStyle>(...) at activation time and the registration
        // outlives the activation scope.
        services.TryAddSingleton(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new MarkerSceneRenderer(loggerFactory?.CreateLogger("Mithril.Overlay"));
        });

        // mithril#1096 — composed-cal resolver lifted from OverlayWindowService internal.
        // Shared by VM consumers (Legolas) + OverlayWindowService (parity).
        services.TryAddSingleton<IComposedOverlayCalibrationResolver, ComposedOverlayCalibrationResolver>();

        // Overlay window service — singleton, surfaced under three contracts
        // (one instance, multiple lookups). Per CLAUDE.md GameState pattern:
        // the hosted-service registration is the lifecycle hook; the
        // IOverlayWindow registration is the consumer-facing surface.
        services.TryAddSingleton<OverlayWindowService>();
        services.TryAddSingleton<IOverlayWindow>(sp => sp.GetRequiredService<OverlayWindowService>());
        services.AddHostedService(sp => sp.GetRequiredService<OverlayWindowService>());

        // mithril#1095 Phase 1 — live view detector infra.
        // IOverlayCaptureSource: the production impl lives here (platform coupling);
        // the interface moved to Mithril.MapCalibration.Detection to avoid a circular
        // project reference (Detection ← Overlay is already in the graph).
        //
        // IMPORTANT: IOverlayWindow MUST NOT be resolved eagerly here.
        // OverlayWindowService (which IS IOverlayWindow) takes ILiveMapViewService
        // in its ctor, and ILiveMapViewService requires IOverlayCaptureSource, so
        // resolving IOverlayWindow at factory-construction time would form the cycle:
        //   OverlayWindowService → ILiveMapViewService → IOverlayCaptureSource
        //     → IOverlayWindow → OverlayWindowService (re-entrant singleton deadlock)
        // The window-accessor lambda defers resolution to first Capture() call,
        // by which point all singletons are fully constructed. (#1095)
        services.TryAddSingleton<IOverlayCaptureSource>(sp =>
        {
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.Overlay.Capture");
            return new OverlayWindowCaptureSource(
                windowAccessor: () => sp.GetRequiredService<IOverlayWindow>().Window,
                logger: logger);
        });

        // ILiveMapViewService: wires IMapViewProbe + IOverlayCaptureSource +
        // IBaseTextureProvider (registered by AddMithrilMapCalibrationDetection).
        // IBaseTextureProvider must be registered before the host starts; this
        // registration is additive and GetRequiredService will throw at first
        // resolution if AddMithrilMapCalibrationDetection was not called.
        services.TryAddSingleton<ILiveMapViewService>(sp =>
        {
            var probe = sp.GetRequiredService<IMapViewProbe>();
            var capture = sp.GetRequiredService<IOverlayCaptureSource>();
            var textures = sp.GetRequiredService<IBaseTextureProvider>();
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            Action<Action> ui = dispatcher is null
                ? a => a()
                : a => dispatcher.Invoke(a);
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger<LiveMapViewService>();
            return new LiveMapViewService(probe, capture, textures, ui, logger);
        });

        return services;
    }
}
