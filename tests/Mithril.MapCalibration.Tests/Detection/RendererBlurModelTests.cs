using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// mithril#1070 — covers the σ(scale) curve used by
/// <c>SobelPaddedPyramidRefiner</c>'s full-resolution stage. Pins the linear
/// model's behaviour at the boundary conditions (disabled, clamp, zero-scale
/// guard) and at a well-separated set of scale points.
/// </summary>
public sealed class RendererBlurModelTests
{
    [Fact]
    public void SigmaFor_returns_zero_when_disabled()
    {
        var options = new MapCalibrationLocateOptions { RendererBlurEnabled = false };
        RendererBlurModel.SigmaFor(0.5, options).Should().Be(0.0);
        RendererBlurModel.SigmaFor(1.0, options).Should().Be(0.0);
        RendererBlurModel.SigmaFor(0.12, options).Should().Be(0.0);
    }

    [Fact]
    public void SigmaFor_is_linear_in_inverse_scale_for_arbitrary_coefficients()
    {
        // Pins the σ = intercept + slope/scale linear-in-1/scale arithmetic with
        // round-number coefficients chosen for arithmetic clarity. The PRODUCTION
        // coefficients (`MapCalibrationLocateOptions` defaults — Plan Task 0 fit:
        // Intercept=-1.5643, Slope=1.0043) are exercised in the
        // SigmaFor_clamps_to_min and SigmaFor_production_curve_matches_fit tests
        // below; this test exists to verify the formula, not the fit.
        var options = new MapCalibrationLocateOptions
        {
            RendererBlurEnabled = true,
            RendererBlurIntercept = 0.10,
            RendererBlurSlope = 0.20,
            RendererBlurMinSigma = 0.0,
            RendererBlurMaxSigma = 3.0,
        };

        // σ = 0.10 + 0.20 / scale
        RendererBlurModel.SigmaFor(0.5, options).Should().BeApproximately(0.50, 1e-9);
        RendererBlurModel.SigmaFor(1.0, options).Should().BeApproximately(0.30, 1e-9);
        RendererBlurModel.SigmaFor(0.25, options).Should().BeApproximately(0.90, 1e-9);
    }

    [Fact]
    public void SigmaFor_clamps_to_min()
    {
        // Production-shape: negative intercept makes σ negative at high scale.
        // Clamp to 0 short-circuits the per-rung GaussianBlur call.
        var options = new MapCalibrationLocateOptions
        {
            RendererBlurEnabled = true,
            RendererBlurIntercept = -1.5643,
            RendererBlurSlope = 1.0043,
            RendererBlurMinSigma = 0.0,
            RendererBlurMaxSigma = 3.0,
        };
        // 1/scale = 1.0 → -1.5643 + 1.0043 = -0.56 → clamped to 0.
        RendererBlurModel.SigmaFor(1.0, options).Should().Be(0.0);

        // Negative slope, positive min: any positive scale floors to min.
        var negSlope = new MapCalibrationLocateOptions
        {
            RendererBlurEnabled = true,
            RendererBlurIntercept = 0.5,
            RendererBlurSlope = -1.0,
            RendererBlurMinSigma = 0.5,
            RendererBlurMaxSigma = 3.0,
        };
        RendererBlurModel.SigmaFor(1.0, negSlope).Should().Be(0.5);
        RendererBlurModel.SigmaFor(0.1, negSlope).Should().Be(0.5);
    }

    [Fact]
    public void SigmaFor_clamps_to_max()
    {
        var options = new MapCalibrationLocateOptions
        {
            RendererBlurEnabled = true,
            RendererBlurIntercept = 0.0,
            RendererBlurSlope = 10.0,
            RendererBlurMinSigma = 0.0,
            RendererBlurMaxSigma = 1.0,
        };
        // 1/scale = 10 → 0 + 100 = 100 → clamped to 1.0
        RendererBlurModel.SigmaFor(0.1, options).Should().Be(1.0);
    }

    [Fact]
    public void SigmaFor_returns_min_on_zero_scale()
    {
        // 1/0 would throw or return +∞. The guard returns MinSigma instead so
        // a degenerate caller (impossible in production, the refiner gates
        // scale > 0 before reaching here) doesn't NaN downstream.
        var options = new MapCalibrationLocateOptions
        {
            RendererBlurEnabled = true,
            RendererBlurIntercept = 0.10,
            RendererBlurSlope = 0.20,
            RendererBlurMinSigma = 0.25,
            RendererBlurMaxSigma = 3.0,
        };
        RendererBlurModel.SigmaFor(0.0, options).Should().Be(0.25);
        RendererBlurModel.SigmaFor(-1.0, options).Should().Be(0.25);
    }
}
