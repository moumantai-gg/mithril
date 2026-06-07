using System.ComponentModel;
using System.Windows;

namespace Mithril.Overlay;

/// <summary>
/// Shared overlay window surface. The overlay's <see cref="Window"/>
/// lifetime is owned by the hosting service; consumers may attach
/// Legolas-specific input handlers (drag survey pin, calibration phase
/// capture, etc.) to it and bind chrome elements to the status surface
/// exposed via <see cref="INotifyPropertyChanged"/>.
///
/// <para>The interface deliberately does <b>not</b> extend
/// <see cref="IDisposable"/>: the singleton lifetime is the host's
/// responsibility (<see cref="Microsoft.Extensions.Hosting.IHostedService.StopAsync"/>
/// on the backing service), and a consumer who resolved this interface and
/// disposed it would tear down the shared overlay for every other consumer.
/// The concrete implementation still implements <see cref="IDisposable"/>
/// for the host's benefit; just don't reach for it through this contract.</para>
///
/// <para><b>Notification properties.</b>
/// <list type="bullet">
/// <item><see cref="IsReady"/> &#8212; <see langword="true"/> while the D3D
/// surface is alive and ready to render. Flipping to <see langword="false"/>
/// after a device-lost event lets consumers hide chrome that depends on the
/// surface being live.</item>
/// <item><see cref="StatusMessage"/> &#8212; user-visible status chip text
/// (e.g. <c>"map not calibrated &#8212; use Legolas wizard"</c> for the
/// uncalibrated-area case). Empty / null when the surface is in its happy
/// state.</item>
/// </list></para>
/// </summary>
public interface IOverlayWindow : INotifyPropertyChanged
{
    /// <summary>The overlay's WPF window. Consumers may attach input
    /// handlers and bind chrome to it; lifetime is owned by the overlay
    /// service.
    ///
    /// <para><b>Allowed</b>: attach input handlers (<c>PreviewKeyDown</c>,
    /// <c>MouseLeftButtonDown</c>, etc.); add child elements / DataTemplates
    /// to the window's content tree; data-bind read-only properties of the
    /// window (e.g. <c>ActualWidth</c>); call <see cref="Window.Show"/> the
    /// first time the overlay becomes user-visible.</para>
    ///
    /// <para><b>Forbidden</b>: <see cref="Window.Close"/> (the host owns
    /// teardown); mutation of <see cref="Window.Topmost"/> /
    /// <see cref="Window.WindowStyle"/> /
    /// <see cref="Window.AllowsTransparency"/> (those are the click-through /
    /// composition invariants from <c>docs/legolas-overview.md</c> &#167;Pitfalls);
    /// re-parenting the window or wrapping it in a new
    /// <see cref="System.Windows.Interop.HwndSource"/>.</para>
    ///
    /// <para>A narrower per-consumer attach surface (e.g.
    /// <c>InputSurface</c> + <c>HeaderContent</c>) is queued as a follow-up
    /// once Gwaihir (second consumer) lands and the attach pressure is
    /// real &#8212; for v1 the raw <see cref="Window"/> matches the issue
    /// spec's "consumers may bind chrome" wording.</para>
    /// </summary>
    Window Window { get; }

    /// <summary>True while the underlying D3D surface is alive and ready to
    /// render. Raises <see cref="INotifyPropertyChanged.PropertyChanged"/>
    /// when the state flips so consumers can react to device-lost without
    /// polling.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Consumer-agnostic status surfaced as the overlay's header chip
    /// (e.g. <c>"map not calibrated &#8212; use Legolas wizard"</c>).
    /// Empty / null when the surface is healthy. Raises
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> when it
    /// changes so the chip can update without polling.
    /// </summary>
    string? StatusMessage { get; }

    /// <summary>
    /// Set (or clear with <see langword="null"/>) the consumer-facing status
    /// chip text (<see cref="StatusMessage"/>). A consumer flips the chip for a
    /// consumer-specific condition (e.g. the map auto-capture pipeline's
    /// "couldn't auto-calibrate — zoom out and redraw the bbox" reason, #914)
    /// and clears it when resolved. No-ops when the value is unchanged. Safe to
    /// call from any thread (the implementation marshals the
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> raise as needed).
    /// </summary>
    void SetStatusMessage(string? message);

    /// <summary>
    /// Register a scene drawer that receives an
    /// <see cref="IOverlaySceneContext"/> per tick. Drawers fire on the
    /// dispatcher inside the surface's <c>BeginDraw</c>/<c>EndDraw</c> pair,
    /// BEFORE the marker renderer; multiple drawers are invoked in
    /// registration order. Dispose the returned <see cref="IDisposable"/> to
    /// deregister; thread-safe under concurrent registration + render. Scene
    /// drawers are skipped this frame when the current area has no
    /// calibration (same gate as the marker renderer's per-tick uncalibrated
    /// chip) &#8212; the chip surfaces via <see cref="StatusMessage"/>.
    ///
    /// <para><b>This is the primary draw API per #835's platform reframe.</b>
    /// World-coord geometry goes through <see cref="IOverlaySceneContext.Project"/>;
    /// pixel-native bits (polylines, calibration placement) use pixels
    /// directly. <see cref="IWorldOverlayMarkers"/> remains a thin
    /// convenience for the "just plot world points" case.</para>
    /// </summary>
    IDisposable RegisterScene(Action<IOverlaySceneContext> draw);
}
