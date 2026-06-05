// #1076 Phase 5a: PixelPoint -> OverlayPixel rename landed here in 5a despite
// this file's nominal Phase 5b ownership; mouse-event CanvasOverlayMapping
// boundaries still deferred to 5b.
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Mithril.Shared.Settings;
using Legolas.Controls;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Rendering;
// #835: D2DOverlaySurface / D2DBrushCache / D2DRenderEventArgs lifted to
// Mithril.Overlay.Internal. Legolas keeps consuming them via the
// InternalsVisibleTo("Legolas.Module") seam until Migration step 6.
using Mithril.Overlay;
using Mithril.Overlay.Internal;
using Legolas.ViewModels;

namespace Legolas.Views;

public partial class MapOverlayView : Window
{
    /// <summary>VM for the optional on-overlay nudge pad. Surfaced as a property
    /// (rather than going through MapOverlayViewModel) to avoid a fake cycle:
    /// NudgePadViewModel depends on MapOverlayViewModel, so MapOverlayViewModel
    /// can't take NudgePadViewModel as a ctor param.</summary>
    public NudgePadViewModel? NudgePad { get; }

    /// <summary>Surfaced for overlay-local bindings — the nudge-pad host
    /// <c>Border.Visibility</c> and the #520 header eye toggle's
    /// <c>IsChecked</c>/icon both read <c>{Binding ElementName=Self,
    /// Path=Settings.ShowNudgePadOnOverlay}</c>. Same instance as the one
    /// <see cref="MapOverlayViewModel"/> sees.
    ///
    /// <para><b>Why a DependencyProperty, not a CLR auto-property.</b> The
    /// parameterized ctor chains to the parameterless one, so
    /// <c>InitializeComponent()</c> — which evaluates every XAML binding —
    /// runs <em>before</em> the ctor body assigns this. A plain CLR
    /// auto-property has no INPC, so WPF would never know <c>Settings</c>
    /// transitioned null→instance: the bindings resolve against null at
    /// load, then stay stuck on the null-path fallback forever (pad never
    /// shows, toggle never writes back). Promoting to a DP makes
    /// <c>SetValue</c> notify the binding system, so both bindings
    /// re-resolve the moment <c>Settings</c> is assigned and pick up live
    /// <c>ShowNudgePadOnOverlay</c> flips from then on.</para></summary>
    public static readonly DependencyProperty SettingsProperty =
        DependencyProperty.Register(
            nameof(Settings),
            typeof(LegolasSettings),
            typeof(MapOverlayView));

    public LegolasSettings? Settings
    {
        get => (LegolasSettings?)GetValue(SettingsProperty);
        private set => SetValue(SettingsProperty, value);
    }

    // #454: the editable player anchor is retired for Survey (absolute
    // placement needs none). Viewport interaction now:
    //  * Motherlode mode: a click records the player position (HandleMapClick
    //    → SetPlayerPosition in the VM; Survey mode ignores the click).
    //  * Drag moves SessionState.SelectedSurvey (picked from the wizard
    //    panel's survey list) — a local pixel correction, no recalibration.
    private SurveyItemViewModel? _draggingPinFromViewport;
    // #477A: a placed calibration marker grabbed for drag-correction.
    private bool _draggingCalibrationMarker;
    // Hit-radius (px) for grabbing a calibration marker before a click pairs.
    private const double CalibrationMarkerGrabRadius = 14;

    private readonly D2DBrushCache _brushCache = new();
    private readonly MarchingAntsClock _antsClock = new();

    public MapOverlayView()
    {
        InitializeComponent();
        // #835 step 6: MapSurface (D2DOverlaySurface) was removed from the
        // XAML. The view is no longer Show()n in production (OverlayController
        // routes to IOverlayWindow.Window instead); this constructor stays
        // because the transient DI factory still resolves it for tests +
        // for step 7's deletion-pass call-site survey.
    }

    public MapOverlayView(LegolasSettings settings, SettingsAutoSaver<LegolasSettings> saver, NudgePadViewModel nudgePad) : this()
    {
        Settings = settings;
        NudgePad = nudgePad;
        // Set the overlay pad's DataContext directly. An ElementName=Self binding
        // on a non-notifying CLR property would resolve at indeterminate timing;
        // assigning here guarantees the pad has its VM before any user input
        // arrives, so Command/IsChecked bindings inside the pad always work.
        OverlayNudgePad.DataContext = nudgePad;
        // #957: LegolasSettings.MapOverlay retired — the live survey overlay is the
        // shared IOverlayWindow.Window, positioned from the shell capture rect by
        // OverlayController via CaptureRectWindowBinder. This legacy view is no longer
        // Show()n in production (#835 step 6; step 7 deletes it), so it carries no
        // window-position binding. The `saver` param is consequently unused now —
        // kept in the signature for the transient DI factory until step 7's deletion.
        Loaded += (_, _) => ApplyClickThrough();
        // Re-assert TOPMOST on every show + on a low-frequency timer while
        // visible — Loaded/Activated alone miss the Hide()/Show() cycle the
        // OverlayController drives when the game holds the foreground.
        ClickThrough.KeepTopmost(this);
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LegolasSettings.ClickThroughMap))
                ApplyClickThrough();
        };
        // #477A: the calibration phase overrides the user's click-through
        // preference — Drop must pass right-clicks to the game (click-through
        // ON), Pair must capture left-clicks (OFF). The wizard-panel button
        // toggles the phase; the overlay reacts here (it can't host the trigger
        // — it's a transparent, possibly click-through window).
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MapOverlayViewModel oldVm)
                oldVm.PropertyChanged -= OnOverlayVmPropertyChanged;
            if (e.NewValue is MapOverlayViewModel newVm)
                newVm.PropertyChanged += OnOverlayVmPropertyChanged;
            ApplyClickThrough();
        };
        Closed += (_, _) =>
        {
            // #835 step 6: MapSurface no longer exists; only the brush cache
            // needs disposing (never bound now that OnMapSurfaceRender doesn't
            // fire). IDisposable.Dispose was made explicit in review-iter-1 S1,
            // so use the internal alias visible via InternalsVisibleTo.
            _brushCache.DisposeInternal();
        };
    }

    private void OnMapSurfaceRender(object? sender, D2DRenderEventArgs e)
    {
        if (DataContext is not MapOverlayViewModel vm) return;

        // Bind the brush cache to the current render target — cheap when
        // unchanged, drops cached brushes when the target rebuilds (resize,
        // device-lost) so the cache never holds dangling COM pointers.
        _brushCache.Bind(e.RenderTarget);

        var wedges = new List<WedgeArc>(vm.Surveys.Count);
        var pins = new List<OverlayPixel>(vm.Surveys.Count);
        var selected = vm.Session.SelectedSurvey;
        var listening = vm.IsListening;
        int? activeIndex = null;
        foreach (var s in vm.Surveys)
        {
            if (s.WedgeArc is { } arc) wedges.Add(arc);
            // IsVisible mirrors the WPF data-template's Visibility binding —
            // collected pins drop out, anything without a projected pixel too.
            if (s.IsVisible)
            {
                if (listening && ReferenceEquals(s, selected))
                    activeIndex = pins.Count;
                pins.Add(s.EffectivePixel!.Value);
            }
        }

        ActivePinTreatmentSpec? activeSpec = null;
        if (activeIndex.HasValue)
        {
            var aps = vm.ActivePinStyle;
            activeSpec = new ActivePinTreatmentSpec(
                Treatment: aps.Treatment,
                Color: ParseColor(aps.Color),
                HaloPaddingPx: aps.HaloPaddingPx,
                StrokeThickness: aps.HaloThickness,
                GlowBlurRadius: aps.GlowBlurRadius);
        }

        var pinStyle = vm.PinStyle;
        var outerStyle = new PinLayerStyle(
            Shape: pinStyle.Outer.Shape,
            FillColor: ParseColor(pinStyle.Outer.FillColor),
            StrokeColor: ParseColor(pinStyle.Outer.StrokeColor),
            StrokeStyle: pinStyle.Outer.StrokeStyle,
            StrokeThickness: pinStyle.Outer.StrokeThickness,
            // Outer Size on survey pins is unused (driven by SurveyPinRadiusMetres);
            // see LegolasPinStyle docs.
            Size: 0);
        var centerStyle = new PinLayerStyle(
            Shape: pinStyle.Center.Shape,
            FillColor: ParseColor(pinStyle.Center.FillColor),
            StrokeColor: ParseColor(pinStyle.Center.StrokeColor),
            StrokeStyle: pinStyle.Center.StrokeStyle,
            StrokeThickness: pinStyle.Center.StrokeThickness,
            Size: pinStyle.Center.Size);

        var playerStyle = vm.PlayerPinStyle;
        var playerOuterStyle = new PinLayerStyle(
            Shape: playerStyle.Outer.Shape,
            FillColor: ParseColor(playerStyle.Outer.FillColor),
            StrokeColor: ParseColor(playerStyle.Outer.StrokeColor),
            StrokeStyle: playerStyle.Outer.StrokeStyle,
            StrokeThickness: playerStyle.Outer.StrokeThickness,
            // Player pin's outer Size IS meaningful — drives the visible
            // diameter the way SurveyPinRadiusMetres does for survey pins.
            Size: playerStyle.Outer.Size);
        var playerCenterStyle = new PinLayerStyle(
            Shape: playerStyle.Center.Shape,
            FillColor: ParseColor(playerStyle.Center.FillColor),
            StrokeColor: ParseColor(playerStyle.Center.StrokeColor),
            StrokeStyle: playerStyle.Center.StrokeStyle,
            StrokeThickness: playerStyle.Center.StrokeThickness,
            Size: playerStyle.Center.Size);

        var scene = new PinScene(
            RoutePoints: vm.RoutePoints,
            ActiveSegmentPoints: vm.ActiveSegmentPoints,
            Wedges: wedges,
            SurveyPins: pins,
            MotherlodePins: vm.MotherlodeMarkerPixels,
            MotherlodeGuidance: vm.MotherlodeGuidanceOverlay,
            ActivePinIndex: activeIndex,
            ActiveTreatment: activeSpec,
            SurveyOuter: outerStyle,
            SurveyCenter: centerStyle,
            SurveyOuterDiameter: vm.PinDiameter,
            PlayerPosition: vm.PlayerMarkerPixel,
            PlayerOuter: playerOuterStyle,
            PlayerCenter: playerCenterStyle,
            RouteLineColor: vm.Brushes.RouteLine.Color,
            WedgeFillColor: vm.Brushes.BearingWedgeFill.Color,
            WedgeStrokeColor: vm.Brushes.BearingWedgeStroke.Color,
            ActiveSegmentDashOffset: _antsClock.Advance());

        PinSceneRenderer.Render(scene, e.RenderTarget, e.Factory, _brushCache);
    }

    private void OnOverlayVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MapOverlayViewModel.IsCalibrationCapturing)
                           or nameof(MapOverlayViewModel.IsCalibrationDropping))
            ApplyClickThrough();
    }

    /// <summary>The calibration phase overrides the user's click-through
    /// preference: Drop ⇒ click-through ON (right-clicks reach the game), Pair
    /// ⇒ OFF (the overlay captures the pairing/correction left-clicks). Outside
    /// calibration, honour <see cref="LegolasSettings.ClickThroughMap"/>.
    ///
    /// <para>#520: pass the header strip as an <em>interactive region</em>,
    /// so when click-through is in effect (either the user setting or the
    /// calibration Drop force-on) the chrome — drag handle + nudge-pad
    /// toggle — stays reachable while the body still passes clicks to the
    /// game underneath. Without the carve-out, enabling click-through also
    /// locked out the toggle that's supposed to let the user dismiss the
    /// pad without alt-tabbing.</para></summary>
    private void ApplyClickThrough()
    {
        var clickThrough = Settings?.ClickThroughMap ?? false;
        if (DataContext is MapOverlayViewModel vm)
        {
            if (vm.IsCalibrationDropping) clickThrough = true;
            else if (vm.IsCalibrationCapturing) clickThrough = false;
        }
        ClickThrough.Apply(this, clickThrough, HeaderChrome);
    }

    private static Color ParseColor(string hex) => LegolasBrushes.Parse(hex);

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Arrow-key pin nudging moved to global IHotkeyCommand registrations
        // (Legolas · Pin Nudge category) so it works while the game window has
        // focus. Local handler now only clears the active selection on Escape.
        if (DataContext is MapOverlayViewModel vm && e.Key == Key.Escape)
        {
            // Clear whichever selection is live so a stray arrow does nothing.
            vm.ClearCalibrationSelection();
            vm.Session.SelectedSurvey = null;
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    // Per-pin Thumb drag handlers were removed when pins became visual-only.
    // All click + drag interaction now flows through the viewport handlers
    // below; selection moved to the wizard panel's survey list.

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MapOverlayViewModel vm) return;
        if (e.OriginalSource is not FrameworkElement fe) return;
        // Only act on clicks that hit the transparent Viewport background, not
        // a survey thumb or any overlay content.
        if (!ReferenceEquals(fe, Viewport)) return;

        var canvasPos = Mouse.GetPosition(Viewport);
        var clickPoint = new OverlayPixel(canvasPos.X, canvasPos.Y);

        // #460/#477A: in the guided walkthrough's Pair phase the overlay
        // captures clicks. A click first tries to grab a placed marker for
        // drag-correction; if none is near, it pairs the currently-named pin.
        if (vm.IsCalibrationCapturing)
        {
            if (vm.TrySelectCalibrationMarkerAt(clickPoint, CalibrationMarkerGrabRadius))
            {
                _draggingCalibrationMarker = true;
                Viewport.CaptureMouse();
            }
            else
            {
                vm.PairCalibrationClick(clickPoint);
            }
            e.Handled = true;
            return;
        }

        // Motherlode records the player position from the click; Survey
        // ignores it (placement is automatic + absolute). The VM mode-gates.
        vm.HandleMapClickCommand.Execute(clickPoint);

        var selected = vm.Session.SelectedSurvey;
        if (selected != null && !selected.Collected && !selected.Skipped)
        {
            _draggingPinFromViewport = selected;
            ApplyDraggedPinPosition(canvasPos);
            Viewport.CaptureMouse();
            e.Handled = true;
            return;
        }

        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!Viewport.IsMouseCaptured) return;
        if (DataContext is not MapOverlayViewModel vm) return;
        var canvasPos = Mouse.GetPosition(Viewport);

        if (_draggingCalibrationMarker)
        {
            vm.DragCalibrationMarkerTo(new OverlayPixel(canvasPos.X, canvasPos.Y));
            return;
        }

        if (_draggingPinFromViewport is not null)
        {
            ApplyDraggedPinPosition(canvasPos);
        }
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!Viewport.IsMouseCaptured) return;
        Viewport.ReleaseMouseCapture();

        if (_draggingCalibrationMarker)
        {
            // The drag already committed live via DragCalibrationMarkerTo;
            // just end the gesture (the marker stays selected for nudging).
            _draggingCalibrationMarker = false;
            return;
        }

        if (_draggingPinFromViewport is not null && DataContext is MapOverlayViewModel vm)
        {
            // Final commit through CorrectSurveyCommand — a local pixel
            // correction + route rebuild (no projector refit any more, #454).
            var canvasPos = Mouse.GetPosition(Viewport);
            var finalPixel = new OverlayPixel(canvasPos.X, canvasPos.Y);
            vm.CorrectSurveyCommand.Execute(new CorrectionArgs(_draggingPinFromViewport, finalPixel));
            _draggingPinFromViewport = null;
        }
    }

    private void ApplyDraggedPinPosition(Point cursor)
    {
        if (_draggingPinFromViewport is null) return;
        var newPixel = new OverlayPixel(cursor.X, cursor.Y);
        var updated = _draggingPinFromViewport.Model with { ManualOverride = newPixel };
        _draggingPinFromViewport.UpdateModel(updated);
    }
}
