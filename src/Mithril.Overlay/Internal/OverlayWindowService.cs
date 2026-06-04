using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Arda.Contracts;
using Arda.World.Player;
using Arda.World.Player.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;
using Mithril.Shared.Diagnostics.Telemetry;
using Vortice.Direct2D1;

namespace Mithril.Overlay.Internal;

/// <summary>
/// Hosted-service owner of the shared overlay window + per-tick projection
/// driver. Implements <see cref="IOverlayWindow"/> &#8212; one instance,
/// surfaced under both contracts via DI.
///
/// <para><b>Lifetime.</b> Constructed via DI when the host starts.
/// <see cref="StartAsync"/> is intentionally cheap &#8212; it does not
/// <c>Show()</c> the window. The window is materialised lazily on first
/// <see cref="Window"/> access; consumers <c>Show()</c> it once their
/// drawers are registered. <see cref="Dispose"/> may run on a non-UI thread
/// (host shutdown) and marshals window + brush-cache disposal back onto the
/// dispatcher (the cache's brushes are dispatcher-affined via the bound
/// render target).</para>
///
/// <para><b>Threading.</b> Window/surface construction must happen on the
/// dispatcher; the projection callback runs on the dispatcher (it's the
/// <see cref="D2DOverlaySurface.Render"/> handler). All marker registry
/// reads + scene-drawer dispatch happen there. Scene-drawer registration
/// (<see cref="RegisterScene"/>) is lock-free and safe from any thread.</para>
///
/// <para><b>Per-tick draw order (#835 step 6).</b>
/// <list type="number">
/// <item>read <c>IAreaState.CurrentArea</c> &#8211; the area key</item>
/// <item>set <see cref="WorldOverlayMarkers.CurrentArea"/></item>
/// <item>uncalibrated area: surface the chip (but still run the scene
/// drawers below — they self-gate and the pixel-native passes, e.g.
/// calibration placement pins, must draw during an uncalibrated Drop/Pair
/// walkthrough per dissolved-#868); the <em>marker renderer</em> is the only
/// thing skipped when uncalibrated (it depends on
/// <see cref="IOverlaySceneContext.Project"/> which would always return
/// null)</item>
/// <item>invoke each registered scene drawer in registration order with an
/// <see cref="IOverlaySceneContext"/> bound to this frame's target / factory
/// / brushes / area key / live zoom</item>
/// <item>calibrated only: project the registry markers and dispatch through
/// <see cref="MarkerSceneRenderer"/></item>
/// </list>
/// Scene drawers run BEFORE the marker renderer so any layer-3 markers
/// (Gwaihir POI pins, calibration placement) sit on top of layer-2 scene
/// geometry (route polylines, wedges, survey/motherlode pin layers).</para>
/// </summary>
internal sealed class OverlayWindowService : IHostedService, IOverlayWindow, IDisposable
{
    // The chip flips on any uncalibrated area. Wording updated in review
    // iteration-1 B2 — the previous "use Legolas wizard" was misleading
    // because the registry-only calibration walkthrough now requires a
    // baseline calibration to anchor against (WindowToWorld returns null
    // without one). The seed comes from a bundled / community baseline or
    // the Mithril MapCalibration workspace tool (#864).
    private const string UncalibratedMessage =
        "map not calibrated — seed calibration needed (no baseline for this area)";

    // Inject the concrete type directly (not IWorldOverlayMarkers): the
    // projection driver needs the internal CurrentArea setter and the
    // concrete type is registered as a singleton ahead of the interface.
    private readonly WorldOverlayMarkers _markers;
    private readonly MarkerSceneRenderer _renderer;
    private readonly IMapCalibrationService _calibration;
    private readonly IAreaState _areaState;
    private readonly IMapState _mapState;
    private readonly ISceneAssetCache _sceneCache;
    private readonly IDomainEventSubscriber _bus;
    private IDisposable? _mapAssetChangedSub;
    private readonly IPositionState _positionState; // reserved for future consumers; ensures the DI shape matches Decision C
    private readonly IOverlayZoomSource _zoomSource;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger? _logger;
    private readonly D2DBrushCache _brushCache = new();

    private OverlayWindow? _window;
    private Dispatcher? _dispatcher;
    private bool _isReady;
    private string? _statusMessage;
    private bool _firstFrameLogged;
    private string? _lastSeenUncalibratedArea;
    // ConcurrentDictionary mirrors the MarkerSceneRenderer._missingDrawerLogged
    // pattern: today this field is touched only from the dispatcher (via
    // OnSurfaceRender → ProjectMarkers), but nothing in the type signature
    // encodes that, and a future cross-thread caller would race silently on a
    // plain HashSet. TryAdd is lock-free, so the per-tick cost stays a hashed
    // lookup.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _projectionMissAreasLogged
        = new(StringComparer.Ordinal);
    private bool _disposed;

    // Scene-drawer registry. Volatile snapshot-array pattern: registrations
    // CAS-swap a fresh array, render-time enumeration reads the snapshot
    // once + iterates by index. Per-tick allocation: zero (no enumerator,
    // no copy). Lock-free for registration; lock-free for render.
    private SceneDrawerRegistration[] _sceneDrawers = Array.Empty<SceneDrawerRegistration>();
    private readonly object _sceneDrawersLock = new();

    // Reusable scene context — bound to this frame's target/factory/area at
    // the top of OnSurfaceRender. Lives as a field to avoid per-tick
    // allocation; only ever touched on the dispatcher.
    private readonly OverlaySceneContext _sceneContext;

    public OverlayWindowService(
        WorldOverlayMarkers markers,
        MarkerSceneRenderer renderer,
        IMapCalibrationService calibration,
        IAreaState areaState,
        IMapState mapState,
        ISceneAssetCache sceneCache,
        IDomainEventSubscriber bus,
        IPositionState positionState,
        IOverlayZoomSource zoomSource,
        ILoggerFactory? loggerFactory = null)
    {
        _markers = markers;
        _renderer = renderer;
        _calibration = calibration;
        _areaState = areaState;
        _mapState = mapState;
        _sceneCache = sceneCache;
        _bus = bus;
        _positionState = positionState;
        _zoomSource = zoomSource;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger("Mithril.Overlay");
        _sceneContext = new OverlaySceneContext(this);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Window Window
    {
        get
        {
            EnsureWindow();
            return _window!;
        }
    }

    public bool IsReady => _isReady;

    public string? StatusMessage => _statusMessage;

    /// <summary>Convenience for XAML triggers that need a bool instead of
    /// null-vs-non-null.</summary>
    public bool HasStatusMessage => !string.IsNullOrEmpty(_statusMessage);

    public IDisposable RegisterScene(Action<IOverlaySceneContext> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        var registration = new SceneDrawerRegistration(draw);
        lock (_sceneDrawersLock)
        {
            var current = _sceneDrawers;
            var next = new SceneDrawerRegistration[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[current.Length] = registration;
            // Volatile.Write semantics via plain assignment is fine here —
            // ConcurrentDictionary-style: writers serialize on the lock, the
            // reader reads a single reference (atomic on any supported
            // runtime). The array itself is never mutated after publishing.
            _sceneDrawers = next;
        }
        _logger?.LogInformation(
            "Scene drawer registered (now {Count}).",
            _sceneDrawers.Length);
        return new SceneDrawerHandle(this, registration);
    }

    private void UnregisterScene(SceneDrawerRegistration registration)
    {
        lock (_sceneDrawersLock)
        {
            var current = _sceneDrawers;
            var idx = Array.IndexOf(current, registration);
            if (idx < 0) return;
            var next = new SceneDrawerRegistration[current.Length - 1];
            Array.Copy(current, next, idx);
            Array.Copy(current, idx + 1, next, idx, current.Length - idx - 1);
            _sceneDrawers = next;
        }
        _logger?.LogInformation(
            "Scene drawer deregistered (now {Count}).",
            _sceneDrawers.Length);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var act = MithrilActivitySources.Overlay.StartActivity("service.start");
        // Resolve the WPF dispatcher off Application.Current rather than
        // Dispatcher.CurrentDispatcher. The latter creates a *new* dispatcher
        // on the calling thread if none exists — which silently passes in a
        // unit test but means an overlay built off the wrong thread can never
        // marshal back to the UI dispatcher. Mithril.Shell calls
        // host.StartAsync() on the UI thread after `new App()`, so
        // Application.Current.Dispatcher is the authoritative reference.
        _dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException(
            "OverlayWindowService.StartAsync requires an active WPF Application — "
            + "Mithril.Shell calls host.StartAsync on the UI thread after `new App()`. "
            + "If you see this from a test or non-shell host, ensure a WPF dispatcher is available "
            + "(or skip the hosted-service registration and exercise the projection helper directly).");
        // mithril#1041: subscribe to MapAssetChanged so per-scene transitions
        // inside aggregator areas drive a frame invalidation directly — without
        // this, the renderer waits for the next surface tick to notice a fresh
        // CurrentMapScene observation.
        _mapAssetChangedSub = _bus.Subscribe<MapAssetChanged>(OnMapAssetChanged);
        _logger?.LogInformation("OverlayWindowService starting (window will be created on first Window-access).");
        return Task.CompletedTask;
    }

    private void OnMapAssetChanged(MapAssetChanged evt)
    {
        // Frame invalidation: the next OnSurfaceRender tick re-reads state via
        // ResolveCurrentScene; we don't need to push anything synchronously
        // from this handler. The marshaling is implicit (the surface render
        // loop runs on the dispatcher).
        // No-op body — the per-tick read path covers correctness; the
        // subscription here is documentation that the renderer is reactive
        // to MapAssetChanged events even if the tick cadence is fast enough
        // that explicit invalidation isn't required.
        _ = evt;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        using var act = MithrilActivitySources.Overlay.StartActivity("service.stop");
        _logger?.LogInformation("OverlayWindowService stopping.");
        _mapAssetChangedSub?.Dispose();
        _mapAssetChangedSub = null;
        DisposeWindow();
        return Task.CompletedTask;
    }

    private void EnsureWindow()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window is not null) return;
        if (_dispatcher is null)
            throw new InvalidOperationException(
                "OverlayWindowService.StartAsync must run before Window is accessed (it captures the WPF dispatcher).");

        if (_dispatcher.CheckAccess())
        {
            CreateWindowOnDispatcher();
        }
        else
        {
            _dispatcher.Invoke(CreateWindowOnDispatcher);
        }
    }

    private void CreateWindowOnDispatcher()
    {
        if (_window is not null) return;
        using var act = MithrilActivitySources.Overlay.StartActivity("window.create");
        _window = new OverlayWindow();
        Mithril.Shared.Wpf.WindowCaptureExclusion.ExcludeFromCapture( // #965
            _window, _loggerFactory?.CreateLogger("Mithril.Overlay.Capture"));
        _window.DataContext = this;
        _window.OverlaySurface.Render += OnSurfaceRender;
        _window.OverlaySurface.Logger = _loggerFactory?.CreateLogger("Mithril.Overlay.Surface");
        _window.Closed += OnWindowClosed;
        _logger?.LogInformation("OverlayWindow created (not shown — consumer must Show() to surface it).");
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        SetReady(false);
        if (_window is not null)
        {
            _logger?.LogWarning(
                "OverlayWindow closed externally — IOverlayWindow.Window.Close() is forbidden per the contract; the host owns teardown. Disposing surface + brush cache as a defensive measure.");
            CleanupAfterClose(_window);
        }
        else
        {
            _logger?.LogInformation("OverlayWindow closed.");
        }
    }

    private void CleanupAfterClose(OverlayWindow window)
    {
        try { window.OverlaySurface.Render -= OnSurfaceRender; } catch (Exception ex)
        {
            _logger?.LogTrace(ex, "OverlayWindow.OverlaySurface.Render detach threw during close cleanup; surface likely already torn down.");
        }
        try { window.OverlaySurface.Dispose(); }
        catch (ObjectDisposedException) { /* idempotent dispose */ }
        _brushCache.DisposeInternal();
        _window = null;
    }

    private void OnSurfaceRender(object? sender, D2DRenderEventArgs e)
    {
        // Render-hook runs on the dispatcher. Inside a BeginDraw/EndDraw pair
        // (the surface manages that).
        SetReady(true);
        _brushCache.Bind(e.RenderTarget);

        var areaKey = _areaState.CurrentArea;
        _markers.CurrentArea = areaKey;

        if (string.IsNullOrEmpty(areaKey))
        {
            SetStatusMessage(null);
            return;
        }

        // mithril#1041 resolution cascade: live IMapState.CurrentMapScene >
        // SceneAssetCache (cold-start fallback for known areas) > strict-null.
        // The resolved MapSceneRef is what every IMapCalibrationService call
        // below takes — fixes the #1041 headline regression where the renderer
        // was looking up calibrations by bare parent-area key against the
        // Map_<X>-keyed store (post-#1040).
        var resolvedScene = SceneResolution.ResolveCurrentScene(_mapState, _sceneCache);

        // Uncalibrated-scene handling. The marker-projection block (further
        // down) depends on WorldToWindow/Project, which always returns null
        // without a calibration — so when uncalibrated we skip ONLY that
        // block, surface the "not calibrated" chip, and log once per scene.
        // The scene-drawer loop still runs: scene drawers self-gate
        // (DrawCalibrationGhosts on ShowCalibrationGhosts; the placement-pin
        // pass on IsCalibrationCapturing) and draw pixel-native, so the
        // calibration placement pins MUST render during a Drop/Pair
        // walkthrough in an uncalibrated scene (dissolved-#868) — calibration
        // only persists at Confirm, so IsCalibrated is false throughout the
        // walkthrough.
        var isCalibrated = resolvedScene is { } scene && _calibration.IsCalibrated(scene);
        if (!isCalibrated)
        {
            if (!string.Equals(_lastSeenUncalibratedArea, areaKey, StringComparison.Ordinal))
            {
                _lastSeenUncalibratedArea = areaKey;
                _logger?.LogInformation(
                    "OverlayWindowService: scene {AreaKey} ({AssetKey}) is uncalibrated; surfacing 'not calibrated' chip and skipping marker projection. Scene drawers still run for pixel-native passes (e.g. calibration placement pins).",
                    areaKey, resolvedScene?.MapAssetKey ?? "<unresolved>");
            }
            SetStatusMessage(UncalibratedMessage);
        }
        else
        {
            _lastSeenUncalibratedArea = null;
            SetStatusMessage(null);
        }

        // Snapshot the live zoom once per frame so scene drawers and the
        // registry projection see the same value (no per-Project read
        // inconsistency across the frame).
        var currentZoom = SnapshotZoom();

        // The scene the per-frame context binds to: if resolution failed, use
        // a synthesized composite with empty calibration key so Project()
        // returns null (no calibration → uncalibrated path); scene drawers
        // still get area context via CurrentAreaKey.
        var frameScene = resolvedScene ?? new MapSceneRef(areaKey, null, string.Empty);

        // Scene drawers — fire BEFORE the marker renderer. Snapshot the
        // drawer array reference once so a concurrent register/unregister
        // can't shift it mid-iteration. Each drawer is invoked in its own
        // try/catch so a throwing drawer is isolated from siblings + the
        // marker renderer's per-tick work (review iteration-1 B1). A
        // single throw inside BeginDraw/EndDraw would otherwise abort the
        // whole frame and poison the surface for every consumer.
        var drawers = _sceneDrawers;
        if (drawers.Length > 0)
        {
            _sceneContext.BeginFrame(e.RenderTarget, e.Factory, _brushCache, areaKey, frameScene, currentZoom);
            using (var sceneAct = MithrilActivitySources.Overlay.StartActivity("scene"))
            {
                sceneAct?.SetTag("area", areaKey);
                sceneAct?.SetTag("scene.asset_key", frameScene.MapAssetKey);
                sceneAct?.SetTag("drawer_count", drawers.Length);
                for (var i = 0; i < drawers.Length; i++)
                {
                    InvokeSceneDrawerIsolated(drawers[i], i);
                }
            }
        }

        // Marker projection + render — calibrated areas only. WorldToWindow
        // returns null without a calibration, so there is nothing meaningful
        // to project here when uncalibrated (the scene drawers above already
        // ran, including the pixel-native calibration placement pins).
        if (!isCalibrated) return;

        var snapshot = _markers.CurrentAreaMarkers;
        var projected = ProjectMarkers(snapshot, resolvedScene!.Value, _calibration, currentZoom,
            onMiss: this, snapshotCount: snapshot.Count);
        if (!_firstFrameLogged)
        {
            _firstFrameLogged = true;
            _logger?.LogInformation(
                "OverlayWindowService: first frame projected for area {AreaKey} ({Count} markers, {DrawerCount} drawers registered, {SceneCount} scene drawers, zoom={Zoom:F2}).",
                areaKey, projected.Count, _renderer.DrawerCount, drawers.Length, currentZoom);
        }

        var sw = ValueStopwatch.StartNew();
        using (var renderAct = MithrilActivitySources.Overlay.StartActivity("project"))
        {
            renderAct?.SetTag("area", areaKey);
            renderAct?.SetTag("scene.asset_key", resolvedScene!.Value.MapAssetKey);
            renderAct?.SetTag("marker_count", projected.Count);
            _renderer.Render(projected, e.RenderTarget, e.Factory, _brushCache);
        }
        var elapsedMs = sw.GetElapsedMilliseconds();
        MithrilMeters.Overlay.ProjectionLatencyMs.Record(elapsedMs,
            new KeyValuePair<string, object?>("area", areaKey));
        MithrilMeters.Overlay.FrameMarkers.Add(projected.Count,
            new KeyValuePair<string, object?>("area", areaKey));
    }

    /// <summary>Read the live zoom from the injected source, defensively
    /// substituting 1.0 if the producer surfaced a non-finite value. Per
    /// <see cref="IOverlayZoomSource"/>'s remarks: producers should never
    /// let NaN reach here, but the projection driver tolerates it.</summary>
    private double SnapshotZoom()
    {
        var z = _zoomSource.CurrentZoom;
        return double.IsFinite(z) && z > 0 ? z : 1.0;
    }

    /// <summary>Pure projection helper &#8212; takes a snapshot + a calibration
    /// service and returns the projected pixel list. Test-friendly overload.</summary>
    internal static IReadOnlyList<(PixelPoint Pixel, IMarkerStyle Style)> ProjectMarkers(
        IReadOnlyList<MarkerSnapshot> markers,
        MapSceneRef scene,
        IMapCalibrationService calibration,
        double currentZoom)
        => ProjectMarkers(markers, scene, calibration, currentZoom, onMiss: null, snapshotCount: markers.Count);

    private static IReadOnlyList<(PixelPoint Pixel, IMarkerStyle Style)> ProjectMarkers(
        IReadOnlyList<MarkerSnapshot> markers,
        MapSceneRef scene,
        IMapCalibrationService calibration,
        double currentZoom,
        OverlayWindowService? onMiss,
        int snapshotCount)
    {
        if (markers.Count == 0)
            return Array.Empty<(PixelPoint, IMarkerStyle)>();

        var result = new List<(PixelPoint, IMarkerStyle)>(markers.Count);
        for (var i = 0; i < markers.Count; i++)
        {
            var snap = markers[i];
            var pixel = calibration.WorldToWindow(scene, snap.World, currentZoom);
            if (pixel is null)
            {
                if (onMiss is not null)
                {
                    var assetKey = scene.MapAssetKey;
                    MithrilMeters.Overlay.ProjectionMisses.Add(1,
                        new KeyValuePair<string, object?>("area", scene.ParentAreaKey));
                    if (onMiss._projectionMissAreasLogged.TryAdd(assetKey, 0))
                    {
                        onMiss._logger?.LogTrace(
                            "OverlayWindowService: WorldToWindow returned null for a marker in calibrated scene {AssetKey} (style={StyleType}); marker silently skipped.",
                            assetKey, snap.Style.GetType().Name);
                    }
                }
                continue;
            }
            result.Add((pixel.Value, snap.Style));
        }
        return result;
    }

    /// <summary>Surface the renderer to test code &#8212; production
    /// consumers inject <see cref="MarkerSceneRenderer"/> directly via DI
    /// and call <c>RegisterDrawer</c> there.</summary>
    internal MarkerSceneRenderer Renderer => _renderer;

    /// <summary>Test seam: the current scene-drawer count. Exposed so
    /// <c>Mithril.Overlay.Tests</c> can verify register / dispose mechanics
    /// without standing up a D3D surface.</summary>
    internal int SceneDrawerCount => _sceneDrawers.Length;

    /// <summary>Test seam: drive one scene-tick against a supplied render
    /// target / factory, with the supplied area key + zoom override.
    /// Bypasses the D3D surface so unit tests can exercise scene-drawer
    /// dispatch + the uncalibrated-area gate.</summary>
    internal void DriveSceneForTest(
        ID2D1RenderTarget renderTarget,
        ID2D1Factory factory,
        string areaKey,
        double currentZoom)
    {
        _brushCache.Bind(renderTarget);
        // Mirror OnSurfaceRender: the scene-drawer loop runs regardless of
        // calibration (drawers self-gate and draw pixel-native passes such as
        // the calibration placement pins). Only the chip differs by state;
        // the marker-projection block is absent from this seam, so there is
        // no calibration-gated early-return here (#872 / #887).
        // Tests of the calibration path go through OnSurfaceRender (or its
        // unit-test seam) which resolves via SceneResolution — this convenience
        // seam synthesises a scene from areaKey so legacy tests still pass.
        var scene = SceneResolution.ResolveCurrentScene(_mapState, _sceneCache)
            ?? new MapSceneRef(areaKey, null, areaKey);
        var isCalibrated = _calibration.IsCalibrated(scene);
        SetStatusMessage(isCalibrated ? null : UncalibratedMessage);

        var drawers = _sceneDrawers;
        if (drawers.Length == 0) return;
        _sceneContext.BeginFrame(renderTarget, factory, _brushCache, areaKey, scene, currentZoom);
        for (var i = 0; i < drawers.Length; i++)
        {
            InvokeSceneDrawerIsolated(drawers[i], i);
        }
    }

    /// <summary>Invoke one scene drawer with exception isolation. A
    /// throwing drawer is logged + counted, and sibling drawers / the
    /// marker renderer still get their per-tick work (review iteration-1
    /// B1 &#8212; the dispatcher path runs inside a single
    /// <c>BeginDraw</c>/<c>EndDraw</c> pair, so an uncaught throw here
    /// would tear the whole frame down and poison the surface).</summary>
    private void InvokeSceneDrawerIsolated(SceneDrawerRegistration drawer, int index)
    {
        try
        {
            drawer.Invoke(_sceneContext);
        }
        catch (Exception ex)
        {
            var drawerType = drawer.TargetTypeName;
            _logger?.LogError(ex,
                "Scene drawer {DrawerIndex} ({DrawerType}) threw; isolating from sibling drawers and the marker renderer for this tick.",
                index, drawerType);
            MithrilMeters.Overlay.SceneDrawerExceptions.Add(1,
                new KeyValuePair<string, object?>("drawer_type", drawerType));
        }
    }

    private void SetReady(bool value)
    {
        if (_isReady == value) return;
        _isReady = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReady)));
    }

    /// <summary>Sets (or clears) the consumer-facing status chip. Public to
    /// satisfy <see cref="IOverlayWindow.SetStatusMessage"/> — a consumer
    /// (Legolas' calibration coordinator) flips the chip for a
    /// consumer-specific condition and clears it when resolved. The
    /// per-tick uncalibrated-area gate in <see cref="OnSurfaceRender"/>
    /// also drives it. No-ops when the value is unchanged.</summary>
    public void SetStatusMessage(string? value)
    {
        if (string.Equals(_statusMessage, value, StringComparison.Ordinal)) return;
        _statusMessage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusMessage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasStatusMessage)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeWindow();
    }

    private void DisposeWindow()
    {
        var window = _window;
        var dispatcher = _dispatcher;
        if (window is null)
        {
            _brushCache.DisposeInternal();
            return;
        }

        void Close()
        {
            window.Closed -= OnWindowClosed;
            try { window.Close(); }
            catch (InvalidOperationException ex)
            {
                _logger?.LogTrace(ex, "OverlayWindow.Close threw — already closed or in non-closeable state; ignoring.");
            }
            CleanupAfterClose(window);
            SetReady(false);
        }

        if (dispatcher is null) throw new InvalidOperationException(
            "OverlayWindowService.DisposeWindow: dispatcher missing but window exists. This indicates EnsureWindow was bypassed.");

        if (dispatcher.CheckAccess()) Close();
        else dispatcher.Invoke(Close);
    }

    private readonly struct ValueStopwatch
    {
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
        private readonly long _startTicks;
        private ValueStopwatch(long start) { _startTicks = start; }
        public static ValueStopwatch StartNew() => new(Stopwatch.GetTimestamp());
        public double GetElapsedMilliseconds() => (Stopwatch.GetTimestamp() - _startTicks) * TicksToMs;
    }

    /// <summary>Wraps a scene-drawer callback with object identity so the
    /// returned <see cref="IDisposable"/> can locate + remove its own entry
    /// without an enumeration over Action&lt;...&gt; equality (Delegate
    /// equality matches by Target+Method, so two registrations of the same
    /// instance method would collide).</summary>
    private sealed class SceneDrawerRegistration
    {
        private readonly Action<IOverlaySceneContext> _draw;
        public SceneDrawerRegistration(Action<IOverlaySceneContext> draw)
        {
            _draw = draw;
            // Cache the target type name once so the per-tick isolation
            // path doesn't reflect on every exception. Delegate.Target is
            // the instance the method is bound to (or null for a static
            // method); falling back to the method's declaring type keeps
            // the tag stable for static-method drawers.
            TargetTypeName = (draw.Target?.GetType() ?? draw.Method.DeclaringType)?.FullName
                ?? "unknown";
        }
        public void Invoke(IOverlaySceneContext ctx) => _draw(ctx);
        public string TargetTypeName { get; }
    }

    private sealed class SceneDrawerHandle : IDisposable
    {
        private OverlayWindowService? _owner;
        private readonly SceneDrawerRegistration _registration;
        public SceneDrawerHandle(OverlayWindowService owner, SceneDrawerRegistration r)
        {
            _owner = owner;
            _registration = r;
        }
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.UnregisterScene(_registration);
        }
    }

    /// <summary>Per-instance scene context. Bound at the top of
    /// <see cref="OnSurfaceRender"/> via <see cref="BeginFrame"/> so the
    /// per-tick allocation is zero. Drawers must not retain the instance
    /// past the callback — fields rebind on the next frame.</summary>
    private sealed class OverlaySceneContext : IOverlaySceneContext
    {
        private readonly OverlayWindowService _owner;
        private ID2D1RenderTarget? _renderTarget;
        private ID2D1Factory? _factory;
        private D2DBrushCache? _brushes;
        private string _areaKey = string.Empty;
        private MapSceneRef _scene;
        private double _currentZoom = 1.0;

        public OverlaySceneContext(OverlayWindowService owner) { _owner = owner; }

        public void BeginFrame(
            ID2D1RenderTarget renderTarget,
            ID2D1Factory factory,
            D2DBrushCache brushes,
            string areaKey,
            MapSceneRef scene,
            double currentZoom)
        {
            _renderTarget = renderTarget;
            _factory = factory;
            _brushes = brushes;
            _areaKey = areaKey;
            _scene = scene;
            _currentZoom = currentZoom;
        }

        public ID2D1RenderTarget RenderTarget =>
            _renderTarget ?? throw new InvalidOperationException(
                "IOverlaySceneContext.RenderTarget accessed outside the scene-drawer callback.");

        public ID2D1Factory Factory =>
            _factory ?? throw new InvalidOperationException(
                "IOverlaySceneContext.Factory accessed outside the scene-drawer callback.");

        public IOverlayBrushes Brushes =>
            _brushes ?? throw new InvalidOperationException(
                "IOverlaySceneContext.Brushes accessed outside the scene-drawer callback.");

        public string CurrentAreaKey => _areaKey;

        public MapSceneRef CurrentScene => _scene;

        public PixelPoint? Project(double worldX, double worldZ)
        {
            // Calibrated-area gate is enforced before drawers fire, so we
            // can call WorldToWindow directly. A null return here means the
            // calibration service couldn't project this specific point
            // (e.g. NaN inputs); the drawer treats that as "skip this pin"
            // — same shape as the marker renderer's null-skip branch.
            return _owner._calibration.WorldToWindow(
                _scene, new WorldCoord(worldX, 0, worldZ), _currentZoom);
        }
    }
}
