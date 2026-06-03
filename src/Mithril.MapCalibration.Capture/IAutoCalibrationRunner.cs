using System.Threading;
using System.Threading.Tasks;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// The "run one auto-calibration attempt for the current area" capability, split
/// from the concrete <see cref="AutoCalibrationEngine"/> so the hotkey + trigger
/// depend on a narrow seam (testable with a spy that needs no capture/solve
/// dependencies).
/// </summary>
public interface IAutoCalibrationRunner
{
    Task<AutoCalibrationOutcome> TryCalibrateCurrentAreaAsync(CancellationToken ct);

    /// <summary>
    /// Verify the stored calibration against fresh locator + icon-detector output
    /// (mithril#1046 §6). Returns a <see cref="DriftCheckOutcome"/> the
    /// <c>ManualCalibrationCoordinator</c> branches on to decide
    /// (a) chip-only no-op, (b) arm-and-warn, or (c) fall-through to a cold
    /// solve. Never persists.
    /// </summary>
    Task<DriftCheckOutcome> CheckDriftAsync(CancellationToken ct);
}
