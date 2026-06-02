using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests.Bundle;

public class MapRectConversionTests
{
    [Fact]
    public void FromRecoveredCalibration_projects_world_to_aligned_pair_pixel()
    {
        var cal = new RecoveredCalibrationJson(
            SchemaVersion: 1,
            Scale: 0.31536, RotationRadians: -3.14153,
            OriginX: 1039.45, OriginY: -36.38,
            MirrorNorth: false, CalibrationZoom: 1.0,
            ResidualPixels: 0.34, ReferenceCount: 4,
            Source: "UserRefinement",
            Inliers: System.Array.Empty<InlierJson>());

        var mapRect = new MapRect(OriginX: 130, OriginY: 60,
            Width: 1013, Height: 1001,
            TextureWidth: 2048, TextureHeight: 2033);

        var t = MapRectConversion.FromRecoveredCalibration(cal, mapRect);

        // Spot-check: world (0, 0, 0) projects to what we'd get composing the
        // canonical AreaCalibration with (MapRect.TextureToScreenshot − origin).
        var canonical = new AreaCalibration(
            cal.Scale, cal.RotationRadians, cal.OriginX, cal.OriginY,
            cal.ReferenceCount, cal.ResidualPixels) { MirrorNorth = cal.MirrorNorth };
        var texturePixel = canonical.WorldToWindow(new WorldCoord(0, 0, 0));
        var screenshotPixel = mapRect.TextureToScreenshot(texturePixel.X, texturePixel.Y);
        var expectedAlignedX = screenshotPixel.Sx - mapRect.OriginX;
        var expectedAlignedY = screenshotPixel.Sy - mapRect.OriginY;

        var actual = t.Apply(new WorldCoord(0, 0, 0));
        actual.X.Should().BeApproximately(expectedAlignedX, 1e-6);
        actual.Y.Should().BeApproximately(expectedAlignedY, 1e-6);
    }

    [Fact]
    public void Anisotropic_MapRect_warns_via_out_param()
    {
        var cal = new RecoveredCalibrationJson(
            1, Scale: 1.0, RotationRadians: 0.0, OriginX: 0.0, OriginY: 0.0,
            MirrorNorth: false, CalibrationZoom: 1.0,
            ResidualPixels: 0.0, ReferenceCount: 1, Source: "UserRefinement",
            Inliers: System.Array.Empty<InlierJson>());

        // 10% anisotropic resize (X factor 0.5, Y factor 0.45).
        var mapRect = new MapRect(0, 0, 1000, 900, 2000, 2000);

        MapRectConversion.FromRecoveredCalibration(cal, mapRect, out var anisotropyPercent);
        anisotropyPercent.Should().BeGreaterThan(1.0);
    }
}
