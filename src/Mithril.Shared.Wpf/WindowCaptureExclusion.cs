using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Marks a WPF <see cref="Window"/> as excluded from screen captures (PrintScreen,
/// Snipping Tool, GDI <c>BitBlt</c> of the screen DC, Windows Graphics Capture)
/// while leaving it fully visible on the display. The pixels beneath the window
/// show through to whatever sits below in any capture surface — not a black
/// rectangle (that was the older <c>WDA_MONITOR</c>).
///
/// <para>Requires Windows 10 2004+ (build 19041). Mithril targets Win11.</para>
/// </summary>
public static class WindowCaptureExclusion
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    /// <summary>
    /// Apply <c>WDA_EXCLUDEFROMCAPTURE</c> to <paramref name="window"/>. Safe to
    /// call before the HWND exists — the helper hooks <see cref="Window.SourceInitialized"/>
    /// once and applies the affinity from that handler.
    /// </summary>
    /// <param name="window">The WPF window to exclude from screen captures.</param>
    /// <param name="logger">Optional. When supplied, a single <c>Warning</c> is
    /// logged on PInvoke failure (Win32 error + HWND). Callers without a logger
    /// get silent fail-soft behavior; the window remains usable.</param>
    public static void ExcludeFromCapture(Window window, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            Apply(hwnd, logger);
            return;
        }

        EventHandler? handler = null;
        handler = (_, _) =>
        {
            window.SourceInitialized -= handler;
            var h = new WindowInteropHelper(window).Handle;
            if (h != IntPtr.Zero) Apply(h, logger);
        };
        window.SourceInitialized += handler;
    }

    private static void Apply(IntPtr hwnd, ILogger? logger)
    {
        if (SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)) return;
        var err = Marshal.GetLastWin32Error();
        logger?.LogWarning(
            "SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) failed for HWND {Hwnd}: Win32 error {Error}.",
            hwnd, err);
    }
}
