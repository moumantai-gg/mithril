using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture.DependencyInjection;
using Mithril.Shared.Game;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// Task 23 (#914): the auto path's confidence gate honours the SAME user-tunable
/// residual threshold the manual path uses (<see cref="GameConfig.CalibrationGoodResidualPx"/>,
/// relocated to GameConfig by PR-0). The factory is tested directly so the
/// threshold wiring is CI-locked independent of the full DI graph.
/// </summary>
public sealed class ConfidenceGateWiringTests
{
    [Fact]
    public void Gate_uses_the_configured_threshold()
    {
        var cfg = new GameConfig { CalibrationGoodResidualPx = 6.0 };
        var gate = CaptureServiceCollectionExtensions.BuildConfidenceGate(cfg);

        gate.Accept(new AreaCalibration(1, 0, 0, 0, 8, 7.0), 8, out var reason).Should().BeFalse();
        reason.Should().Contain("6");
        gate.Accept(new AreaCalibration(1, 0, 0, 0, 8, 5.0), 8, out _).Should().BeTrue();
    }
}
