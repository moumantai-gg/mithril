using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection;

// IOverlayCaptureSource was moved to Mithril.MapCalibration.Detection (#1095);
// this implementation still lives in Mithril.Overlay (platform coupling).
namespace Mithril.Overlay.Internal;

/// <summary>
/// Captures the screen region under the shared overlay window into a
/// single-channel <see cref="GrayImage"/>. Uses <c>Graphics.CopyFromScreen</c>
/// against the overlay window's screen rect — we want PG's pixels, not the
/// transparent overlay layer's. The overlay window is excluded from capture
/// via <c>WDA_EXCLUDEFROMCAPTURE</c> on construction (memory:
/// <c>wda_excludefromcapture_canonical_for_overlay_chrome.md</c>), so this
/// reads PG's underlying map view directly.
///
/// <para>Constructor takes a window accessor (a no-arg <c>Func</c>) so the
/// type stays testable: production wires it to read the live overlay window
/// from <see cref="OverlayWindowService"/>; tests pass a fake.</para>
/// </summary>
internal sealed class OverlayWindowCaptureSource : IOverlayCaptureSource
{
    private readonly Func<Window?> _windowAccessor;
    private readonly ILogger? _logger;

    public OverlayWindowCaptureSource(Func<Window?> windowAccessor, ILogger? logger = null)
    {
        _windowAccessor = windowAccessor;
        _logger = logger;
    }

    public GrayImage? Capture()
    {
        var window = _windowAccessor();
        if (window is null)
        {
            _logger?.LogTrace("Capture: window accessor returned null — overlay not realised yet.");
            return null;
        }

        try
        {
            // mithril#1107 review fix: Capture() runs on a Task.Run thread per
            // LiveMapViewService.RunProbe, but WPF visual properties are
            // dispatcher-affined — accessing them off the dispatcher throws
            // InvalidOperationException. Pre-fix: every probe failed with "calling
            // thread cannot access this object", the live-view detector never
            // produced a fix, and consumers fell back to canonical projection.
            // Marshal the read to the dispatcher (with CheckAccess for the rare
            // case the probe IS on the UI thread).
            //
            // mithril#1107 manual-verify fix #2: capture the D2DOverlaySurface's
            // screen rect, NOT the window's. The OverlayWindow has chrome — a
            // 1px Border + a ~25px HeaderChrome bar with the status label —
            // sitting above the rendering surface (OverlayWindow.xaml:27-71).
            // If we capture from window.Top, the probe's recovered (pan, scale)
            // bakes the chrome offset into the live-view fix; markers later
            // drawn onto the D2DOverlaySurface end up ~25px too low vertically
            // (and ~1px right) relative to PG's actual map content. PointToScreen
            // handles DPI-aware coordinate conversion (DIPs → screen pixels)
            // correctly regardless of monitor scale.
            int x, y, w, h;
            (x, y, w, h) = window.Dispatcher.CheckAccess()
                ? ComputeSurfaceScreenRect(window)
                : window.Dispatcher.Invoke(() => ComputeSurfaceScreenRect(window));
            if (w <= 0 || h <= 0)
            {
                _logger?.LogTrace("Capture: surface has non-positive dims ({W}x{H} at {X},{Y}); skipping.", w, h, x, y);
                return null;
            }
            _logger?.LogTrace("Capture: copying screen region {W}x{H} at ({X},{Y}).", w, h, x, y);

            using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h), CopyPixelOperation.SourceCopy);

            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var stride = data.Stride;
                var pixels = new byte[w * h];
                unsafe
                {
                    var src = (byte*)data.Scan0;
                    for (int row = 0; row < h; row++)
                        for (int col = 0; col < w; col++)
                        {
                            byte b = src[row * stride + col * 3];
                            byte gch = src[row * stride + col * 3 + 1];
                            byte r = src[row * stride + col * 3 + 2];
                            // Rec. 601 luma — adequate for correlation against
                            // a gray-decoded base texture.
                            pixels[row * w + col] = (byte)((r * 299 + gch * 587 + b * 114) / 1000);
                        }
                }
                return new GrayImage(w, h, pixels);
            }
            finally { bmp.UnlockBits(data); }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Overlay capture failed — safe-degrade to null fix.");
            return null;
        }
    }

    /// <summary>
    /// Compute the D2DOverlaySurface's screen rect in device pixels. Must be
    /// called on the WPF dispatcher (Window/UIElement properties + PointToScreen
    /// are dispatcher-affined). Returns all-zeros when the window isn't an
    /// OverlayWindow, the surface isn't realised, or it has degenerate dims.
    /// </summary>
    private static (int X, int Y, int W, int H) ComputeSurfaceScreenRect(Window window)
    {
        if (window is not OverlayWindow overlay) return (0, 0, 0, 0);
        var surface = overlay.OverlaySurface;
        if (surface.ActualWidth <= 0 || surface.ActualHeight <= 0) return (0, 0, 0, 0);

        // PointToScreen translates WPF logical units (DIPs) into device pixels
        // accounting for DPI scaling. Two corners give us the actual rendered
        // screen rect rather than relying on Window.Left + a hardcoded chrome
        // offset (which would drift if the header layout ever changes).
        var topLeft = surface.PointToScreen(new System.Windows.Point(0, 0));
        var bottomRight = surface.PointToScreen(
            new System.Windows.Point(surface.ActualWidth, surface.ActualHeight));
        return (
            (int)topLeft.X,
            (int)topLeft.Y,
            (int)(bottomRight.X - topLeft.X),
            (int)(bottomRight.Y - topLeft.Y));
    }
}
