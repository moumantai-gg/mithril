namespace Mithril.MapCalibration;

/// <summary>
/// Unsafe / frame-erased read access to a pixel coordinate. Use ONLY at
/// well-defined leaf sites where the consumer is intrinsically frame-blind:
///   • Direct2D / WPF rendering primitives (the GPU doesn't care about frames)
///   • Serialisation (JSON / log formatting)
///   • Interop with third-party libraries that take raw doubles (OpenCvSharp)
/// Going through this interface erases frame identity — do not use it in any
/// code that combines coordinates from more than one source.
/// </summary>
public interface IPixelPoint
{
    double X { get; }
    double Y { get; }
    double Z { get; }
}
