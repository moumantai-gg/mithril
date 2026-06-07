using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Mithril.Overlay;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// #947: <see cref="MapCaptureRegionProvider.Current"/> reads the SHELL-persisted
/// capture rect (not the live overlay-window geometry) and returns it verbatim — the
/// rect is already in physical pixels (resolved at snip-confirm time from the snip
/// window's single device scale), so there is no read-time DPI / monitor work. These
/// tests cover the fail-soft cases (null store dependency, null persisted value,
/// degenerate rect → <c>Current</c> null) and the happy path (persisted physical
/// rect → returned verbatim, with NO overlay window involved). They use a fake store,
/// so no WPF window / STA thread is needed for the provider itself.
///
/// <para>The bbox draw controller tests (snip → store + overlay-bounds apply seam)
/// still run on an STA thread because WPF <see cref="Window"/> construction requires
/// it.</para>
/// </summary>
public sealed class MapCaptureRegionProviderTests
{
    [Fact]
    public void Current_is_null_when_store_dependency_is_absent()
    {
        // A unit-test graph without the shell: no store wired → fail soft.
        var provider = new MapCaptureRegionProvider(store: null);
        provider.Current.Should().BeNull();
    }

    [Fact]
    public void Current_is_null_when_no_rect_has_been_snipped()
    {
        var provider = new MapCaptureRegionProvider(new FakeStore(value: null));
        provider.Current.Should().BeNull("never-snipped store value is the legitimate 'no bbox set' state");
    }

    [Fact]
    public void Current_returns_persisted_physical_rect_verbatim_with_no_overlay_window()
    {
        var store = new FakeStore(new CaptureRect(180, 120, 1200, 900));
        var provider = new MapCaptureRegionProvider(store);

        provider.Current.Should().Be(new CaptureRect(180, 120, 1200, 900),
            "the persisted rect is already physical pixels — the provider returns it verbatim");
    }

    [Fact]
    public void Current_returns_signed_origin_verbatim()
    {
        // A rect snipped on a secondary monitor left/above the primary → signed origin.
        var store = new FakeStore(new CaptureRect(-1920, -200, 600, 400));
        var provider = new MapCaptureRegionProvider(store);

        provider.Current.Should().Be(new CaptureRect(-1920, -200, 600, 400));
    }

    [Fact]
    public void Current_is_null_when_persisted_rect_is_degenerate()
    {
        var provider = new MapCaptureRegionProvider(new FakeStore(new CaptureRect(10, 10, 0, 100)));
        provider.Current.Should().BeNull("a degenerate stored rect carries no capturable pixels");
    }

    [Fact]
    public void BeginDraw_with_a_confirmed_snip_persists_physical_to_the_store() => RunOnSta(() =>
    {
        var window = CreateOffscreenBorderlessWindow();
        try
        {
            window.Show();
            DrainDispatcher();

            var store = new FakeStore(value: null);
            var overlay = new RealWindowOverlay(window);
            // The snip resolved a DIU rect (mirrored onto the overlay) AND a physical
            // rect (persisted) — here a 1.0 device scale so the two coincide.
            var controller = new MapBboxDrawController(
                overlay, store, logger: null,
                snip: () => new MapBboxDrawController.SnipResult(
                    new Rect(120, 80, 500, 400), new CaptureRect(120, 80, 500, 400)));

            controller.BeginDraw();
            DrainDispatcher();

            // Authoritative persistence path (#947): the store holds the PHYSICAL rect.
            store.Value.Should().Be(new CaptureRect(120, 80, 500, 400));

            // And the overlay was mirrored (with the DIU rect) for visual feedback.
            window.Left.Should().Be(120);
            window.Width.Should().Be(500);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void BeginDraw_persists_scaled_physical_rect_distinct_from_diu_mirror() => RunOnSta(() =>
    {
        var window = CreateOffscreenBorderlessWindow();
        try
        {
            window.Show();
            DrainDispatcher();

            var store = new FakeStore(value: null);
            var overlay = new RealWindowOverlay(window);
            // A 150% snip: DIU (100,50,400,300) mirrors onto the overlay; the PHYSICAL
            // rect (150,75,600,450) is what BitBlt reads, and what we persist.
            var controller = new MapBboxDrawController(
                overlay, store, logger: null,
                snip: () => new MapBboxDrawController.SnipResult(
                    new Rect(100, 50, 400, 300), new CaptureRect(150, 75, 600, 450)));

            controller.BeginDraw();
            DrainDispatcher();

            store.Value.Should().Be(new CaptureRect(150, 75, 600, 450),
                "the store persists the physical rect, not the DIU rect");
            window.Width.Should().Be(400, "the overlay mirror uses the DIU rect");
        }
        finally { window.Close(); }
    });

    [Fact]
    public void BeginDraw_with_a_cancelled_snip_does_not_touch_the_store() => RunOnSta(() =>
    {
        var window = CreateOffscreenBorderlessWindow();
        try
        {
            window.Show();
            DrainDispatcher();

            var store = new FakeStore(value: null);
            var controller = new MapBboxDrawController(
                new RealWindowOverlay(window), store, logger: null, snip: () => null);

            controller.BeginDraw();
            DrainDispatcher();

            store.Value.Should().BeNull("a cancelled snip persists nothing");
            window.Left.Should().Be(OffScreenLeft, "a cancelled snip leaves the window's initial Left untouched");
        }
        finally { window.Close(); }
    });

    [Fact]
    public void BeginDraw_with_no_physical_rect_does_not_persist_but_still_mirrors() => RunOnSta(() =>
    {
        var window = CreateOffscreenBorderlessWindow();
        try
        {
            window.Show();
            DrainDispatcher();

            var store = new FakeStore(value: null);
            // A confirmed DIU selection but the window transform was unavailable
            // (Physical == null) — persist nothing, but still mirror onto the overlay.
            var controller = new MapBboxDrawController(
                new RealWindowOverlay(window), store, logger: null,
                snip: () => new MapBboxDrawController.SnipResult(new Rect(120, 80, 500, 400), null));

            controller.BeginDraw();
            DrainDispatcher();

            store.Value.Should().BeNull("no physical rect → nothing persisted");
            window.Left.Should().Be(120, "the overlay is still mirrored for the session");
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Apply_seam_moves_and_resizes_only_bounds() => RunOnSta(() =>
    {
        var window = new Window
        {
            Topmost = true,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ShowInTaskbar = false,
            Left = 0,
            Top = 0,
            Width = 10,
            Height = 10,
        };

        MapBboxDrawController.ApplyVirtualDesktopRectToOverlay(window, new Rect(-200, 50, 640, 480));

        window.Left.Should().Be(-200);
        window.Top.Should().Be(50);
        window.Width.Should().Be(640);
        window.Height.Should().Be(480);

        // Forbidden invariants must be untouched.
        window.Topmost.Should().BeTrue();
        window.WindowStyle.Should().Be(WindowStyle.None);
        window.AllowsTransparency.Should().BeTrue();
    });

    [Fact]
    public void Apply_seam_ignores_degenerate_rect() => RunOnSta(() =>
    {
        var window = new Window { Left = 5, Top = 6, Width = 7, Height = 8 };
        MapBboxDrawController.ApplyVirtualDesktopRectToOverlay(window, new Rect(0, 0, 0, 100));
        window.Left.Should().Be(5);
        window.Width.Should().Be(7);
    });

    // mithril#996: Tests that call Show() on a real Window flash briefly in the top-left
    // of the primary monitor when the suite runs in the background. Parking the window
    // at large negative coordinates keeps it off every realistic monitor while still
    // realizing the HWND, the dispatcher, and the layout pass that the SUT exercises.
    private const double OffScreenLeft = -10000;
    private const double OffScreenTop = -10000;

    private static Window CreateOffscreenBorderlessWindow() => new()
    {
        WindowStyle = WindowStyle.None,
        ShowInTaskbar = false,
        Left = OffScreenLeft,
        Top = OffScreenTop,
        Width = 10,
        Height = 10,
    };

    private static void DrainDispatcher() =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.Loaded);

    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (captured is not null) throw captured;
    }

    private sealed class FakeStore : IMapCaptureRectStore
    {
        public FakeStore(CaptureRect? value) => Value = value;
        public CaptureRect? Value { get; private set; }
        public CaptureRect? Get() => Value;
        public void Set(CaptureRect rect) => Value = rect;
    }

    /// <summary>A minimal real-window IOverlayWindow for the controller tests.</summary>
    private sealed class RealWindowOverlay : IOverlayWindow
    {
        public RealWindowOverlay(Window window) => Window = window;
        public Window Window { get; }
        public bool IsReady => true;
        public string? StatusMessage { get; private set; }
        public void SetStatusMessage(string? message) { StatusMessage = message; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusMessage))); }
        public IDisposable RegisterScene(Action<IOverlaySceneContext> draw) => new Noop();
        public event PropertyChangedEventHandler? PropertyChanged;
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}
