namespace Mithril.MapCalibration;

/// <summary>
/// A <see cref="MapRect"/> together with its origin in captured-frame
/// coordinates — i.e. where the located map rect sits within the full OS
/// capture. Use for the "located rect" case in the auto-calibration pipeline;
/// use bare <see cref="MapRect"/> for crop-aligned cases.
///
/// See spec §5.1 of the pixel-frame-typing refactor (#1076) for why this split
/// exists: bare <see cref="MapRect"/> describes texture↔crop with the crop's
/// origin pinned at (0,0) in its own frame; <see cref="LocatedMapRect"/>
/// additionally carries the crop's placement within the captured frame.
/// </summary>
public readonly record struct LocatedMapRect(MapRect MapRect, CapturedFramePixel Origin)
{
    public CapturedFramePixel CroppedToCaptured(CroppedFramePixel pixel) =>
        new(pixel.X + Origin.X, pixel.Y + Origin.Y);

    public CroppedFramePixel CapturedToCropped(CapturedFramePixel pixel) =>
        new(pixel.X - Origin.X, pixel.Y - Origin.Y);
}
