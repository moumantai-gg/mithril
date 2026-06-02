using System.Threading;
using System.Threading.Tasks;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Captures the framed map region under the blanked overlay and validates it.
/// </summary>
public interface ICaptureService
{
    /// <summary>
    /// Blank the overlay, capture <paramref name="bbox"/>, restore the overlay,
    /// validate the frame, and return both the color frame and its grayscale
    /// derivation. Both halves are <see langword="null"/> on any failure (capture
    /// failed, wrong size, black frame).
    /// </summary>
    Task<CaptureMapResult> CaptureMapAsync(CaptureRect bbox, CancellationToken ct);
}

/// <summary>
/// The result of a map capture attempt: the original BGRA color frame and its
/// grayscale derivation. Both are null when the capture failed or was rejected
/// by validation.
/// </summary>
/// <param name="Color">The raw BGRA32 captured frame, or <see langword="null"/> on failure.</param>
/// <param name="Gray">The grayscale derivation of <paramref name="Color"/>, or <see langword="null"/> on failure.</param>
public sealed record CaptureMapResult(CapturedFrame? Color, GrayImage? Gray);
