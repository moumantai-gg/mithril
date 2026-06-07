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
            // LiveMapViewService.RunProbe, but Window.Left/Top/Width/Height are
            // WPF dispatcher-affined — accessing them off the dispatcher throws
            // InvalidOperationException. Pre-fix: every probe failed with "calling
            // thread cannot access this object", the live-view detector never
            // produced a fix, and consumers fell back to canonical projection.
            // Marshal the read to the dispatcher (with CheckAccess for the rare
            // case the probe IS on the UI thread).
            int x, y, w, h;
            if (window.Dispatcher.CheckAccess())
            {
                x = (int)window.Left; y = (int)window.Top;
                w = (int)window.Width; h = (int)window.Height;
            }
            else
            {
                var (rx, ry, rw, rh) = window.Dispatcher.Invoke(() =>
                    ((int)window.Left, (int)window.Top, (int)window.Width, (int)window.Height));
                x = rx; y = ry; w = rw; h = rh;
            }
            if (w <= 0 || h <= 0)
            {
                _logger?.LogTrace("Capture: window has non-positive dims ({W}x{H} at {X},{Y}); skipping.", w, h, x, y);
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
}
