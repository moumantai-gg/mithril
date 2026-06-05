namespace Mithril.MapCalibration;

/// <summary>
/// A desktop-pixel rectangle: the persisted map bbox and the resolved game
/// client rect both use it. Deliberately a plain BCL struct (no
/// <see cref="System.Windows.Rect"/>) so it can cross into the BCL-only
/// detection core without dragging a WPF dependency through the boundary.
///
/// <para>Lives in the core <c>Mithril.MapCalibration</c> assembly (moved out of
/// <c>Mithril.MapCalibration.Capture</c> in #957) so both the windows-only Capture
/// project and <c>Legolas.Module</c> — which references the core but not Capture —
/// can consume the one-rect seam (<see cref="IMapCaptureRectStore"/>) without
/// taking a dependency on the BitBlt/CsWin32 capture infra.</para>
/// </summary>
public readonly record struct CaptureRect(int X, int Y, int Width, int Height)
{
    /// <summary>An empty rect carries no capturable pixels.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Right edge (exclusive).</summary>
    public int Right => X + Width;

    /// <summary>Bottom edge (exclusive).</summary>
    public int Bottom => Y + Height;

    /// <summary>
    /// The overlapping region of this rect and <paramref name="other"/>, clamped
    /// to their shared extent. A non-overlapping pair yields an
    /// <see cref="IsEmpty"/> rect (non-positive width/height).
    /// </summary>
    public CaptureRect Intersect(CaptureRect other)
    {
        int left = Math.Max(X, other.X);
        int top = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right);
        int bottom = Math.Min(Bottom, other.Bottom);
        return new CaptureRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// True when <paramref name="inner"/> lies fully within this rect.
    /// </summary>
    public bool Contains(CaptureRect inner) =>
        inner.X >= X && inner.Y >= Y && inner.Right <= Right && inner.Bottom <= Bottom;

    /// <summary>
    /// Typed translation from a game-window-frame pixel into the captured-frame
    /// pixel space this rect describes. Subtracts the rect's origin (the rect's
    /// (<see cref="X"/>, <see cref="Y"/>) within the game window's client area).
    /// See spec §5 of the pixel-frame-typing refactor (#1076).
    /// </summary>
    public CapturedFramePixel GameWindowToCaptured(GameWindowPixel pixel) =>
        new(pixel.X - X, pixel.Y - Y);

    /// <summary>Inverse of <see cref="GameWindowToCaptured"/>.</summary>
    public GameWindowPixel CapturedToGameWindow(CapturedFramePixel pixel) =>
        new(pixel.X + X, pixel.Y + Y);
}
