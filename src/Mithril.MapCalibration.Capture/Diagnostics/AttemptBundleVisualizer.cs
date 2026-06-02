using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture.Diagnostics;

/// <summary>
/// WPF-only (DrawingVisual + RenderTargetBitmap + PngBitmapEncoder) renderers
/// for the three annotated bundle PNGs and the deviation map. No
/// System.Drawing (#921 guard).
/// </summary>
public sealed class AttemptBundleVisualizer : IAttemptBundleVisualizer
{
    public BitmapSource RenderDeviation(GrayImage screenshot, GrayImage baseTexture)
    {
        if (screenshot.Width != baseTexture.Width || screenshot.Height != baseTexture.Height)
        {
            throw new ArgumentException(
                $"Deviation inputs must match: screenshot {screenshot.Width}x{screenshot.Height}, " +
                $"baseTexture {baseTexture.Width}x{baseTexture.Height}.");
        }

        int w = screenshot.Width, h = screenshot.Height;
        var diff = new byte[w * h];
        var s = screenshot.Pixels;
        var b = baseTexture.Pixels;
        for (int i = 0; i < diff.Length; i++)
        {
            int d = s[i] - b[i];
            diff[i] = d > 0 ? (byte)d : (byte)0;
        }

        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, diff, w);
        src.Freeze();
        return src;
    }

    public BitmapSource RenderDetectionsOverlay(
        GrayImage gray,
        IReadOnlyList<TypedDetection> detections,
        int renderSizePx)
    {
        int w = gray.Width, h = gray.Height;

        // Background: gray screenshot as a Gray8 BitmapSource.
        var grayBg = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, gray.Pixels, w);
        grayBg.Freeze();

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(grayBg, new System.Windows.Rect(0, 0, w, h));

            var cyan = new Pen(Brushes.Cyan, 1); cyan.Freeze();
            var red = new Pen(Brushes.Red, 1); red.Freeze();
            var labelBrush = Brushes.Cyan;
            var typeface = new Typeface("Segoe UI");

            double half = renderSizePx / 2.0;
            foreach (var det in detections)
            {
                var rect = new System.Windows.Rect(det.AnchorX - half, det.AnchorY - half, renderSizePx, renderSizePx);
                dc.DrawRectangle(brush: null, cyan, rect);
                dc.DrawLine(red, new System.Windows.Point(det.AnchorX - 2, det.AnchorY), new System.Windows.Point(det.AnchorX + 2, det.AnchorY));
                dc.DrawLine(red, new System.Windows.Point(det.AnchorX, det.AnchorY - 2), new System.Windows.Point(det.AnchorX, det.AnchorY + 2));

                var text = new FormattedText(
                    det.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Windows.FlowDirection.LeftToRight, typeface, 9, labelBrush, 96);
                dc.DrawText(text, new System.Windows.Point(rect.Right + 1, rect.Top - 1));
            }
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    public BitmapSource RenderProjectionOverlay(
        CapturedFrame rawColor,
        MapRect mapRect,
        AreaCalibration calibration,
        IReadOnlyList<LandmarkReference> references,
        IReadOnlyList<TypeAwareRansacSolver.AssignedReference> inliers,
        int renderSizePx)
    {
        int w = rawColor.Width, h = rawColor.Height;

        var bg = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, rawColor.Bgra, w * 4);
        bg.Freeze();

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(bg, new System.Windows.Rect(0, 0, w, h));

            var yellow = new Pen(Brushes.Yellow, 1); yellow.Freeze();
            var green = new Pen(Brushes.LimeGreen, 2); green.Freeze();

            // Project every ref via WorldToWindow (texture coords) → TextureToScreenshot.
            foreach (var r in references)
            {
                var px = calibration.WorldToWindow(r.World, currentZoom: 1.0);
                var (sx, sy) = mapRect.TextureToScreenshot(px.X, px.Y);
                dc.DrawLine(yellow, new System.Windows.Point(sx - 3, sy), new System.Windows.Point(sx + 3, sy));
                dc.DrawLine(yellow, new System.Windows.Point(sx, sy - 3), new System.Windows.Point(sx, sy + 3));
            }

            // Green outline rect for each inlier (inlier pixels are texture coords).
            double half = renderSizePx / 2.0;
            foreach (var inl in inliers)
            {
                var (sx, sy) = mapRect.TextureToScreenshot(inl.PixelX, inl.PixelY);
                dc.DrawRectangle(brush: null, green,
                    new System.Windows.Rect(sx - half, sy - half, renderSizePx, renderSizePx));
            }
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
