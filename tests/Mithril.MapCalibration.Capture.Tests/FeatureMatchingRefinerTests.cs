using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class FeatureMatchingRefinerTests
{
    private static FeatureMatchingRefiner BuildRefiner(MapCalibrationLocateOptions? opts = null)
        => new(opts ?? new MapCalibrationLocateOptions());

    [Fact]
    public void Recovers_identity_when_refining_image_against_itself()
    {
        // NoisyChecker (not the plain checker): a pure checkerboard's corners
        // are descriptor-identical to BRIEF, so Lowe's ratio test kills every
        // match against a near-tie second-best and the gate sees 0 inliers.
        // Seeded noise breaks the descriptor symmetry while preserving corners.
        var img = TestPatterns.NoisyChecker(width: 256, height: 256, cellSize: 16);
        var result = BuildRefiner().Refine(img, img, minScore: 0);

        result.AcceptedRect.Should().NotBeNull();
        result.Metrics.Should().NotBeNull();
        result.Metrics!.InlierRatio.Should().BeGreaterThan(0.90);
        result.Metrics.Scale.Should().BeApproximately(1.0, 0.02);
        Math.Abs(result.Metrics.RotationDegrees).Should().BeLessThan(0.1);
        result.AcceptedRect!.OriginX.Should().BeInRange(-2, 2);
        result.AcceptedRect.OriginY.Should().BeInRange(-2, 2);
        result.AcceptedRect.Width.Should().BeInRange(254, 258);
        result.AcceptedRect.Height.Should().BeInRange(254, 258);
    }

    [Fact]
    public void Recovers_half_scale_when_capture_is_downsampled_view()
    {
        // RichNoise: high-frequency random texture so every FAST corner has a
        // unique BRIEF descriptor at BOTH scales. NoisyChecker's repetition
        // hurts the inlier ratio after resize; RichNoise survives the bilinear
        // downsample cleanly because ORB's built-in image pyramid lands
        // distinctive features at both 1x and 0.5x.
        var texture = TestPatterns.RichNoise(width: 512, height: 512);
        var halved = TestPatterns.Resize(texture, 256, 256);
        var result = BuildRefiner().Refine(halved, texture, minScore: 0);

        result.AcceptedRect.Should().NotBeNull();
        result.Metrics!.Scale.Should().BeApproximately(0.5, 0.05);
    }

    [Fact]
    public void Recovers_translation_when_capture_is_a_pasted_crop_of_texture()
    {
        // Frame the texture inside a larger uniform-gray "screenshot" at known origin.
        // RichNoise: a checker has descriptor-ambiguous corners — a translated
        // checker still picks up many wrong-cell pairings that survive Lowe's
        // ratio, dragging the inlier ratio below the gate. RichNoise gives
        // each FAST corner a unique BRIEF signature so the gate clears.
        var texture = TestPatterns.RichNoise(width: 256, height: 256);
        var screenshot = TestPatterns.PasteInto(
            background: TestPatterns.UniformGray(640, 480, 128),
            foreground: texture,
            originX: 192, originY: 100);

        var result = BuildRefiner().Refine(screenshot, texture, minScore: 0);

        result.AcceptedRect.Should().NotBeNull();
        result.AcceptedRect!.OriginX.Should().BeCloseTo(192, 3);
        result.AcceptedRect.OriginY.Should().BeCloseTo(100, 3);
        result.Metrics!.Scale.Should().BeApproximately(1.0, 0.02);
    }

    [Fact]
    public void Rejects_uniform_screenshot_with_no_features()
    {
        var texture = TestPatterns.GenerateChecker(width: 256, height: 256, cellSize: 16);
        var screenshot = TestPatterns.UniformGray(640, 480, 128);

        var result = BuildRefiner().Refine(screenshot, texture, minScore: 0);

        // Either no-fit at all, or a fit the gate rejected.
        result.AcceptedRect.Should().BeNull();
    }

    [Fact]
    public void Rejects_fit_above_rotation_gate()
    {
        var texture = TestPatterns.GenerateChecker(width: 256, height: 256, cellSize: 16);
        var rotated = TestPatterns.Rotate(texture, degrees: 5.0);

        var result = BuildRefiner().Refine(rotated, texture, minScore: 0);

        // Either RANSAC fails or the rotation gate trips.
        result.AcceptedRect.Should().BeNull();
        if (result.Metrics is not null)
        {
            Math.Abs(result.Metrics.RotationDegrees).Should().BeGreaterThan(0.5);
        }
    }
}
