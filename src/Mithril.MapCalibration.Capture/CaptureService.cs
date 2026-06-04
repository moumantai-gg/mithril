using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Captures the framed bbox via <see cref="IScreenCapture"/> and validates the
/// result before handing a clean <see cref="CaptureMapResult"/> to the solve
/// engine. The overlay windows declare themselves invisible to capture at
/// construction (<see cref="Mithril.Shared.Wpf.WindowCaptureExclusion"/>, #965),
/// so capture has no overlay coupling.
/// </summary>
public sealed class CaptureService : ICaptureService
{
    private readonly IScreenCapture _capture;
    private readonly CaptureValidation _validation;
    private readonly ILogger? _logger;

    public CaptureService(
        IScreenCapture capture,
        CaptureValidation validation,
        ILogger? logger)
    {
        _capture = capture;
        _validation = validation;
        _logger = logger;
    }

    public Task<CaptureMapResult> CaptureMapAsync(CaptureRect bbox, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        CapturedFrame? frame = _capture.Capture(bbox);
        if (frame is null)
        {
            _logger?.LogWarning("Map capture produced no frame for bbox {Width}x{Height} at ({X},{Y})",
                bbox.Width, bbox.Height, bbox.X, bbox.Y);
            return Task.FromResult(new CaptureMapResult(null, null));
        }

        if (!_validation.Validate(frame, bbox, out var reason))
        {
            _logger?.LogWarning("Map capture rejected: {Reason}", reason);
            return Task.FromResult(new CaptureMapResult(null, null));
        }

        return Task.FromResult(new CaptureMapResult(frame, frame.ToGray()));
    }
}
