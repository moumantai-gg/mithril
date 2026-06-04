using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture.Tests.Fixtures;

/// <summary>
/// Shared test-helper factories for engine-level unit tests. Originally lived
/// privately inside <c>AutoCalibrationEngineTests</c>; lifted to the Fixtures
/// folder so new test classes (e.g. <c>AutoCalibrationEngineOutcomeCategoryTests</c>)
/// can share the same vocabulary without copy-pasting.
/// </summary>
internal static class TestHelpers
{
    /// <summary>A solver result that successfully solved with the given residual
    /// + inlier count. The other AreaCalibration fields are placeholder values
    /// chosen so the gate logic — not the projector math — drives the test.</summary>
    public static CalibrationSolveResult Accepted(double residual, int inliers) =>
        new(new AreaCalibration(1.2, 0.1, 100, 100, inliers, residual), inliers, null);

    /// <summary>A solver result that REJECTED with the given reason.</summary>
    public static CalibrationSolveResult Rejected(string reason) => new(null, 0, reason);

    /// <summary>A representative pre-existing calibration stamped as the
    /// <see cref="CalibrationSource.BundledBaseline"/> shipped with Mithril.</summary>
    public static AreaCalibration SomeBaseline() =>
        new(1.0, 0, 50, 50, 4, 3.0) { Source = CalibrationSource.BundledBaseline };
}
