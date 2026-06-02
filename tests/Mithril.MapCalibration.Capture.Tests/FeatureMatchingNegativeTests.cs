using System.IO;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.DependencyInjection;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class FeatureMatchingNegativeTests
{
    private static readonly string FixturesRoot = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "CalibrationBundles");

    [Theory]
    [InlineData("KurMountains-Live-20260602", "Eltibule-Accepted-20260602", "AreaEltibule")]
    [InlineData("Eltibule-Accepted-20260602", "KurMountains-Live-20260602", "AreaKurMountains")]
    public void Rejects_when_texture_does_not_match_capture_area(
        string captureFolder, string wrongTextureFolder, string wrongAreaKey)
    {
        var capturePath = Path.Combine(FixturesRoot, captureFolder, "capture.png");
        var capture = PngFixtureLoader.LoadGray(capturePath);

        var textureDir = Path.Combine(FixturesRoot, wrongTextureFolder);
        var provider = new ServiceCollection()
            .AddMithrilMapCalibrationEngine(textureDir)
            .BuildServiceProvider()
            .GetRequiredService<IBaseTextureProvider>();
        var wrongTexture = provider.TryGetBaseTexture(wrongAreaKey)
                           ?? throw new InvalidOperationException(
                               $"Fixture {wrongTextureFolder}: no base texture for area {wrongAreaKey}");

        var refiner = new FeatureMatchingRefiner(new MapCalibrationLocateOptions());
        var result = refiner.Refine(capture, wrongTexture, minScore: 0);

        result.AcceptedRect.Should().BeNull(
            "RANSAC should not converge on a fit, or the inlier/ratio gate should reject the random correspondences");
    }
}
