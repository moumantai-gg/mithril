// #1076 Phase 5a: PixelPoint -> OverlayPixel rename landed here in 5a despite
// this file's nominal Phase 5b ownership; mouse-event CanvasOverlayMapping
// boundaries still deferred to 5b.
using System.Windows;
using System.Windows.Input;
using Mithril.Shared.Settings;
using Legolas.Controls;
using Legolas.Domain;
using Legolas.ViewModels;

namespace Legolas.Views;

/// <summary>
/// Standalone transparent, topmost click-capture window for per-area map
/// calibration. The user sees the in-game region map through it and clicks
/// where each picked landmark/NPC sits; those clicks feed
/// <see cref="CalibrationSessionViewModel"/> (no survey-FSM coupling).
///
/// Deliberately NOT click-through (unlike the survey overlay's optional mode):
/// the whole point is to capture clicks. <see cref="ClickThrough.KeepTopmost"/>
/// keeps it above the game for its whole visible lifetime.
/// </summary>
public partial class CalibrationOverlayView : Window
{
    public CalibrationOverlayView()
    {
        InitializeComponent();
        Mithril.Shared.Wpf.WindowCaptureExclusion.ExcludeFromCapture(this); // #965
    }

    public CalibrationOverlayView(LegolasSettings settings, SettingsAutoSaver<LegolasSettings> saver) : this()
    {
        WindowLayoutBinder.Bind(this, settings.CalibrationOverlay, saver.Touch);
        // Re-assert TOPMOST on every show + on a low-frequency timer while
        // visible — Loaded/Activated alone miss the Hide()/Show() cycle the
        // OverlayController drives when the game holds the foreground.
        ClickThrough.KeepTopmost(this);
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is CalibrationSessionViewModel vm)
            vm.SetViewport(Viewport.ActualWidth, Viewport.ActualHeight);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private bool _dragging;

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not CalibrationSessionViewModel vm) return;
        if (e.OriginalSource is not FrameworkElement fe || !ReferenceEquals(fe, Viewport)) return;

        var p = Mouse.GetPosition(Viewport);
        var pos = new OverlayPixel(p.X, p.Y);

        // Hit an existing pin → grab it and drag (mouse, not arrow keys —
        // bypasses the listbox/hotkey fight). Miss → click-to-place / arm.
        if (vm.TrySelectPinAt(pos, 14))
        {
            _dragging = true;
            vm.DragSelectedTo(pos);
            Viewport.CaptureMouse();
        }
        else
        {
            vm.ViewportClickedCommand.Execute(pos);
        }
        Viewport.Focus();
        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || DataContext is not CalibrationSessionViewModel vm) return;
        var p = Mouse.GetPosition(Viewport);
        vm.DragSelectedTo(new OverlayPixel(p.X, p.Y));
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Viewport.ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>
    /// Arrow keys fine-tune the selected placement (Shift = 5px). Handled at
    /// the window's PreviewKeyDown (tunnels before any child), and marked
    /// Handled, so the reference/area lists never also arrow-navigate — pick
    /// those by mouse. Only requires the calibration window to be the active
    /// window (true right after a placement click).
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (DataContext is CalibrationSessionViewModel vm
            && vm.CanNudge
            && TryArrowDelta(e.Key, out var ux, out var uy))
        {
            var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 5.0 : 1.0;
            vm.NudgeSelected(ux * step, uy * step);
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private static bool TryArrowDelta(Key key, out double dx, out double dy)
    {
        (dx, dy) = key switch
        {
            Key.Left => (-1.0, 0.0),
            Key.Right => (1.0, 0.0),
            Key.Up => (0.0, -1.0),
            Key.Down => (0.0, 1.0),
            _ => (0.0, 0.0),
        };
        return dx != 0 || dy != 0;
    }
}
