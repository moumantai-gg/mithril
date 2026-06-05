// #1076 Phase 5b: mouse-event positions (e.GetPosition) are typed as CanvasPixel
// and converted to OverlayPixel via CanvasOverlayMapping before crossing into
// overlay-frame code. Identity at DpiScale=1.0 today (P.3 audit confirmed
// viewport-frame == overlay-frame in Legolas today).
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Mithril.Shared.Modules;
using Mithril.Shared.Settings;
using Legolas.Controls;
using Legolas.Domain;
using Legolas.Services;
using Legolas.ViewModels;
using Legolas.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;
using Mithril.Overlay;

namespace Legolas.Hotkeys;

/// <summary>
/// Manages the two topmost transparent overlay windows in response to
/// <see cref="SessionState.IsMapVisible"/> / <see cref="SessionState.IsInventoryVisible"/>.
/// Overlays cannot live inside the shell's ContentPresenter, so they stay as
/// top-level <see cref="Window"/>s owned by a module-scoped controller.
///
/// Visibility is gated by both the user's intent flag AND the in-app
/// foreground state from <see cref="ForegroundFocusGate"/> (issue #116) so
/// alt-tabbing to a browser doesn't leave the overlays floating over it.
/// The user's intent flag is never mutated here — only the rendered window.
/// </summary>
public sealed class OverlayController : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ModuleGates _gates;
    private readonly SessionState _session;
    private readonly LegolasSettings _settings;
    private readonly ForegroundFocusGate _focusGate;
    private readonly IOverlayWindow _overlayWindow;
    private readonly SettingsAutoSaver<LegolasSettings> _settingsSaver;
    // #957: the survey overlay reads/writes its frame through the one shell-persisted
    // capture rect, so capture-frame and overlay-frame are a single source of truth.
    private readonly IMapCaptureRectStore _captureRectStore;
    private readonly CancellationTokenSource _stopCts = new();
    private Task? _activationTask;
    private bool _subscribed;
    private bool _sharedMapWired;
    // #835 step 6: MapOverlayView is no longer shown in production — the
    // shared IOverlayWindow.Window is the visible map overlay. The legacy
    // view's class file isn't deleted (step 7 owns that) but the field +
    // EnsureMap glue here is gone, and its production Show() site (was
    // SyncMap) routes to _overlayWindow.Window instead.
    private InventoryOverlayView? _inventory;
    private CalibrationOverlayView? _calibration;

    // #835 step 6 review iter-1 B4: drag-correct + calibration capture state
    // hosted on the shared overlay window's ViewportRoot. Mirrors the legacy
    // MapOverlayView fields that drove the same input pipeline.
    private SurveyItemViewModel? _draggingPinFromViewport;
    private bool _draggingCalibrationMarker;
    private MapOverlayViewModel? _wiredMapVm;
    private FrameworkElement? _viewportRoot;
    // Hit-radius (px) for grabbing a placed calibration marker before a
    // pair-click — same constant as the legacy MapOverlayView.
    private const double CalibrationMarkerGrabRadius = 14;
    // #1076 Phase 5b: identity mapping at DpiScale=1.0 today (per P.3 audit).
    // When per-monitor DPI scaling lands the host will inject a real DpiScale.
    private readonly CanvasOverlayMapping _canvasOverlayMapping = new(DpiScale: 1.0);

    public OverlayController(
        IServiceProvider services,
        ModuleGates gates,
        SessionState session,
        LegolasSettings settings,
        ForegroundFocusGate focusGate,
        IOverlayWindow overlayWindow,
        SettingsAutoSaver<LegolasSettings> settingsSaver,
        IMapCaptureRectStore captureRectStore)
    {
        _services = services;
        _gates = gates;
        _session = session;
        _settings = settings;
        _focusGate = focusGate;
        _overlayWindow = overlayWindow;
        _settingsSaver = settingsSaver;
        _captureRectStore = captureRectStore;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Don't block host startup on the module gate — Lazy modules stay
        // closed until the user clicks the tab. Wait on a background task.
        _activationTask = Task.Run(async () =>
        {
            try
            {
                await _gates.For("legolas").WaitAsync(_stopCts.Token).ConfigureAwait(false);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _session.PropertyChanged += OnSessionPropertyChanged;
                    _focusGate.PropertyChanged += OnFocusGatePropertyChanged;
                    _settings.PropertyChanged += OnSettingsPropertyChanged;
                    _subscribed = true;
                    SyncMap();
                    SyncInventory();
                    SyncCalibration();
                });
            }
            catch (OperationCanceledException) { }
        }, _stopCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopCts.Cancel();
        if (_activationTask is not null) { try { await _activationTask.ConfigureAwait(false); } catch { } }
        if (Application.Current is null) return;
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_subscribed)
            {
                _session.PropertyChanged -= OnSessionPropertyChanged;
                _focusGate.PropertyChanged -= OnFocusGatePropertyChanged;
                _settings.PropertyChanged -= OnSettingsPropertyChanged;
            }
            // The shared overlay window is owned by OverlayWindowService's
            // hosted-service lifecycle — DO NOT Close() it from here per
            // the IOverlayWindow.Window remarks (host owns teardown).
            // Hide is harmless and matches the legacy view's Close+null
            // semantics for the user-visible state.
            if (_sharedMapWired) _overlayWindow.Window.Hide();
            _inventory?.Close();
            _calibration?.Close();
            _inventory = null;
            _calibration = null;
        });
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionState.IsMapVisible)) SyncMap();
        else if (e.PropertyName == nameof(SessionState.IsInventoryVisible)) SyncInventory();
        else if (e.PropertyName == nameof(SessionState.IsCalibrationVisible)) SyncCalibration();
    }

    private void OnFocusGatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ForegroundFocusGate.IsInApp)) return;
        SyncMap();
        SyncInventory();
        SyncCalibration();
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Toggling the auto-hide setting itself should immediately reflect on
        // current visibility — flipping it ON while a non-game window has focus
        // should hide the overlays right away, and flipping it OFF should
        // restore them.
        if (e.PropertyName != nameof(LegolasSettings.AutoHideOverlaysOnGameUnfocused)) return;
        SyncMap();
        SyncInventory();
        SyncCalibration();
    }

    private bool ShouldRender(bool sessionFlag) =>
        sessionFlag && (_focusGate.IsInApp || !_settings.AutoHideOverlaysOnGameUnfocused);

    private void SyncMap()
    {
        // #835 step 6: the shared Mithril.Overlay.IOverlayWindow.Window is
        // now the visible map overlay; MapOverlayView is no longer Show()n.
        // Legolas's WindowLayoutBinder + KeepTopmost + ClickThrough chrome
        // behaviors are applied to the shared window lazily on first
        // attach. Step 7 lifts those behaviors into Mithril.Overlay and
        // retires this Legolas-side glue.
        if (ShouldRender(_session.IsMapVisible))
        {
            EnsureSharedMapWired();
            _overlayWindow.Window.Show();
        }
        else
        {
            // First-time access to the shared window forces lazy construction;
            // guard so we don't materialise it just to hide it. The window
            // is wired on the first ShouldRender=true path above.
            if (_sharedMapWired) _overlayWindow.Window.Hide();
        }
    }

    /// <summary>One-shot attach of Legolas's window-level chrome behaviors
    /// (layout persistence, topmost re-assertion, click-through) to the
    /// shared <see cref="IOverlayWindow.Window"/>. Runs on the dispatcher
    /// (this method is called from <see cref="SyncMap"/> which itself runs
    /// from a Dispatcher.InvokeAsync). Idempotent &#8212; the
    /// <see cref="_sharedMapWired"/> latch guards against re-attaching.
    /// Step 7 owns the lift of <see cref="ClickThrough"/> /
    /// <see cref="WindowLayoutBinder"/> into Mithril.Overlay.</summary>
    private void EnsureSharedMapWired()
    {
        if (_sharedMapWired) return;
        var window = _overlayWindow.Window;
        // The shared window has a HeaderChrome border at the top (see
        // Mithril.Overlay/Internal/OverlayWindow.xaml). Click-through
        // carves out that header so the chip + drag handle stay reachable
        // when the body passes clicks through to the game. The carve-out
        // requires a FrameworkElement reference — we pick it up from the
        // visual tree by name.
        FrameworkElement? headerChrome = null;
        if (window.IsLoaded)
        {
            headerChrome = window.FindName("HeaderChrome") as FrameworkElement;
        }
        else
        {
            window.Loaded += (_, _) =>
            {
                headerChrome = window.FindName("HeaderChrome") as FrameworkElement;
                ApplySharedClickThrough(window, headerChrome);
            };
        }

        // #957: bind the survey overlay to the shell capture rect (physical px) — the
        // window positions itself from it and writes drags/resizes back, so the overlay
        // frame IS the capture frame (one-rect). Replaces the WindowLayoutBinder +
        // LegolasSettings.MapOverlay path used by the inventory/calibration overlays.
        CaptureRectWindowBinder.Bind(
            window,
            _captureRectStore,
            _services.GetService<ILoggerFactory>()?.CreateLogger("Legolas.OverlayLayout"));
        ClickThrough.KeepTopmost(window);

        // #835 step 6 review iter-1 B4: wire the survey-drag + calibration-
        // pair mouse handlers onto the shared window's ViewportRoot (the
        // body Grid below HeaderChrome — its Background=Transparent makes
        // it hit-test-visible while Surface.IsHitTestVisible=False lets
        // D2D output stay below). The wiring also drives the calibration-
        // phase click-through override (Drop=ON, Pair=OFF) so Pair-click
        // capture works even when the user has ClickThroughMap=true. Step 7
        // owns the lift of all four (handlers + phase override) into
        // Mithril.Overlay.
        WireMapInputHandlers(window);
        // Only apply now if HeaderChrome is already resolvable (window
        // Loaded). When the window hasn't loaded yet, headerChrome is still
        // null here, and ClickThrough.Apply(window, true, null) would make
        // the ENTIRE window click-through — including the header chip + drag
        // handle — until the deferred Loaded handler re-applies with the real
        // carve-out. Defer to that handler instead, so the header never goes
        // dead on first show with ClickThroughMap=true (#872 review #2).
        if (window.IsLoaded)
            ApplySharedClickThrough(window, headerChrome);

        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LegolasSettings.ClickThroughMap))
                ApplySharedClickThrough(window, headerChrome);
        };

        _sharedMapWired = true;
    }

    /// <summary>Apply the user's <see cref="LegolasSettings.ClickThroughMap"/>
    /// preference, with the calibration-phase override applied on top:
    /// Drop forces click-through ON (right-clicks reach the game to drop
    /// pins), Pair forces it OFF (the overlay captures the pairing
    /// left-clicks). Mirrors the legacy
    /// <c>MapOverlayView.ApplyClickThrough</c> contract; #835 step 7 lifts
    /// this and the input handlers into <c>Mithril.Overlay</c>.</summary>
    private void ApplySharedClickThrough(Window window, FrameworkElement? headerChrome)
    {
        var clickThrough = _settings.ClickThroughMap;
        if (_wiredMapVm is { } vm)
        {
            if (vm.IsCalibrationDropping) clickThrough = true;
            else if (vm.IsCalibrationCapturing) clickThrough = false;
        }
        ClickThrough.Apply(window, clickThrough, headerChrome);
    }

    /// <summary>One-shot attach of the survey-drag + calibration-pair
    /// mouse handlers (#835 step 6 review iter-1 B4) to the shared
    /// overlay window. Resolves <see cref="MapOverlayViewModel"/> lazily
    /// from DI so the VM is available regardless of construction order.
    /// Handlers attach to the <c>ViewportRoot</c> element resolved by
    /// name (same FindName pattern as <c>HeaderChrome</c>) so the mouse
    /// position is measured against the map body, not the header strip.
    /// Detaches on <see cref="Window.Closed"/> to prevent leaks.</summary>
    private void WireMapInputHandlers(Window window)
    {
        if (_wiredMapVm is not null) return; // idempotent
        _wiredMapVm = _services.GetService<MapOverlayViewModel>();
        if (_wiredMapVm is null) return;

        // Re-apply click-through whenever the calibration phase flips so
        // the overlay captures Pair clicks even when the user has
        // ClickThroughMap=true (matches the legacy MapOverlayView contract).
        _wiredMapVm.PropertyChanged += OnSharedMapVmPropertyChanged;

        void AttachToViewport()
        {
            var viewport = window.FindName("ViewportRoot") as FrameworkElement;
            if (viewport is null) return; // step-7-lift will narrow this contract
            _viewportRoot = viewport;
            viewport.MouseLeftButtonDown += SharedViewport_MouseLeftButtonDown;
            viewport.MouseMove += SharedViewport_MouseMove;
            viewport.MouseLeftButtonUp += SharedViewport_MouseLeftButtonUp;
        }

        if (window.IsLoaded) AttachToViewport();
        else window.Loaded += (_, _) => AttachToViewport();

        window.Closed += (_, _) => DetachMapInputHandlers();
    }

    private void DetachMapInputHandlers()
    {
        if (_wiredMapVm is { } vm) vm.PropertyChanged -= OnSharedMapVmPropertyChanged;
        if (_viewportRoot is { } vp)
        {
            vp.MouseLeftButtonDown -= SharedViewport_MouseLeftButtonDown;
            vp.MouseMove -= SharedViewport_MouseMove;
            vp.MouseLeftButtonUp -= SharedViewport_MouseLeftButtonUp;
        }
        _wiredMapVm = null;
        _viewportRoot = null;
    }

    private void OnSharedMapVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MapOverlayViewModel.IsCalibrationCapturing)
                           or nameof(MapOverlayViewModel.IsCalibrationDropping))
        {
            // Re-apply click-through on the dispatcher — PropertyChanged
            // can fire from any thread depending on the VM mutation site.
            if (_sharedMapWired)
            {
                var window = _overlayWindow.Window;
                var headerChrome = window.FindName("HeaderChrome") as FrameworkElement;
                ApplySharedClickThrough(window, headerChrome);
            }
        }
    }

    // -- Drag + pair input handlers (lifted near-verbatim from MapOverlayView) --
    //
    // The legacy view's handlers gated on `ReferenceEquals(fe, Viewport)` to
    // distinguish a viewport-background click from a click on an overlay
    // child element. The shared ViewportRoot has only the D2D Surface as a
    // child (IsHitTestVisible=False), so the equivalent check is "the
    // mouse hit landed on the ViewportRoot or the Surface" — both count
    // as a viewport-background click. Cast through OriginalSource to be safe.

    internal void SharedViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var vp = _viewportRoot;
        var vm = _wiredMapVm;
        if (vp is null || vm is null) return;

        var canvasPos = e.GetPosition(vp);
        var clickPoint = _canvasOverlayMapping.CanvasToOverlay(
            new CanvasPixel(canvasPos.X, canvasPos.Y));

        // Pair-phase capture: try grab-for-drag first, fall back to pair.
        if (vm.IsCalibrationCapturing)
        {
            if (vm.TrySelectCalibrationMarkerAt(clickPoint, CalibrationMarkerGrabRadius))
            {
                _draggingCalibrationMarker = true;
                vp.CaptureMouse();
            }
            else
            {
                vm.PairCalibrationClick(clickPoint);
            }
            e.Handled = true;
            return;
        }

        // Motherlode records player position; Survey ignores it. VM mode-gates.
        vm.HandleMapClickCommand.Execute(clickPoint);

        var selected = vm.Session.SelectedSurvey;
        if (selected != null && !selected.Collected && !selected.Skipped)
        {
            _draggingPinFromViewport = selected;
            ApplyDraggedPinPosition(canvasPos);
            vp.CaptureMouse();
            e.Handled = true;
            return;
        }

        e.Handled = true;
    }

    internal void SharedViewport_MouseMove(object sender, MouseEventArgs e)
    {
        var vp = _viewportRoot;
        var vm = _wiredMapVm;
        if (vp is null || vm is null) return;
        if (!vp.IsMouseCaptured) return;
        var canvasPos = e.GetPosition(vp);

        if (_draggingCalibrationMarker)
        {
            vm.DragCalibrationMarkerTo(_canvasOverlayMapping.CanvasToOverlay(
                new CanvasPixel(canvasPos.X, canvasPos.Y)));
            return;
        }
        if (_draggingPinFromViewport is not null)
        {
            ApplyDraggedPinPosition(canvasPos);
        }
    }

    internal void SharedViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var vp = _viewportRoot;
        var vm = _wiredMapVm;
        if (vp is null || vm is null) return;
        if (!vp.IsMouseCaptured) return;
        vp.ReleaseMouseCapture();

        if (_draggingCalibrationMarker)
        {
            // DragCalibrationMarkerTo committed live; just end the gesture.
            _draggingCalibrationMarker = false;
            return;
        }

        if (_draggingPinFromViewport is not null)
        {
            // Final commit through CorrectSurveyCommand — a local pixel
            // correction + route rebuild (no projector refit, #454).
            var canvasPos = e.GetPosition(vp);
            var finalPixel = _canvasOverlayMapping.CanvasToOverlay(
                new CanvasPixel(canvasPos.X, canvasPos.Y));
            vm.CorrectSurveyCommand.Execute(new CorrectionArgs(_draggingPinFromViewport, finalPixel));
            _draggingPinFromViewport = null;
        }
    }

    private void ApplyDraggedPinPosition(Point cursor)
    {
        if (_draggingPinFromViewport is null) return;
        var newPixel = _canvasOverlayMapping.CanvasToOverlay(new CanvasPixel(cursor.X, cursor.Y));
        var updated = _draggingPinFromViewport.Model with { ManualOverride = newPixel };
        _draggingPinFromViewport.UpdateModel(updated);
    }

    // ---- Test seams for the input pipeline (review iter-1 B4 smoke test) ----
    //
    // These bypass the WPF MouseButtonEventArgs construction that a real
    // unit test can't build without a full visual tree. They drive the
    // exact same state-machine inside the controller (Down → optional
    // Move → Up) so a regression in the handler chain still surfaces.
    // The handlers themselves remain the production path.

    internal void TestInjectMapVmAndViewport(MapOverlayViewModel vm, FrameworkElement viewport)
    {
        _wiredMapVm = vm;
        _viewportRoot = viewport;
    }

    internal bool TestSimulateMouseDown(Point canvasPos)
    {
        var vp = _viewportRoot; var vm = _wiredMapVm;
        if (vp is null || vm is null) return false;
        var clickPoint = _canvasOverlayMapping.CanvasToOverlay(
            new CanvasPixel(canvasPos.X, canvasPos.Y));

        if (vm.IsCalibrationCapturing)
        {
            if (vm.TrySelectCalibrationMarkerAt(clickPoint, CalibrationMarkerGrabRadius))
            {
                _draggingCalibrationMarker = true;
                return true;
            }
            vm.PairCalibrationClick(clickPoint);
            return false;
        }

        vm.HandleMapClickCommand.Execute(clickPoint);
        var selected = vm.Session.SelectedSurvey;
        if (selected != null && !selected.Collected && !selected.Skipped)
        {
            _draggingPinFromViewport = selected;
            ApplyDraggedPinPosition(canvasPos);
            return true;
        }
        return false;
    }

    internal void TestSimulateMouseUp(Point canvasPos)
    {
        var vp = _viewportRoot; var vm = _wiredMapVm;
        if (vp is null || vm is null) return;

        if (_draggingCalibrationMarker)
        {
            _draggingCalibrationMarker = false;
            return;
        }
        if (_draggingPinFromViewport is not null)
        {
            var finalPixel = _canvasOverlayMapping.CanvasToOverlay(
                new CanvasPixel(canvasPos.X, canvasPos.Y));
            vm.CorrectSurveyCommand.Execute(new CorrectionArgs(_draggingPinFromViewport, finalPixel));
            _draggingPinFromViewport = null;
        }
    }

    private void SyncInventory()
    {
        if (ShouldRender(_session.IsInventoryVisible)) EnsureInventory().Show();
        else _inventory?.Hide();
    }

    private void SyncCalibration()
    {
        if (ShouldRender(_session.IsCalibrationVisible)) EnsureCalibration().Show();
        else _calibration?.Hide();
    }

    private InventoryOverlayView EnsureInventory()
    {
        if (_inventory is not null) return _inventory;
        _inventory = _services.GetRequiredService<InventoryOverlayView>();
        _inventory.Closed += (_, _) =>
        {
            _inventory = null;
            _session.IsInventoryVisible = false;
        };
        return _inventory;
    }

    private CalibrationOverlayView EnsureCalibration()
    {
        if (_calibration is not null) return _calibration;
        _calibration = _services.GetRequiredService<CalibrationOverlayView>();
        _calibration.Closed += (_, _) =>
        {
            _calibration = null;
            _session.IsCalibrationVisible = false;
        };
        return _calibration;
    }
}
