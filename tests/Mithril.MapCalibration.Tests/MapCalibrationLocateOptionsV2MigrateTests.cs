using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// mithril#1070 — covers the v1 → v2 additive migration on
/// <see cref="MapCalibrationLocateOptions"/>. The migrate body is an identity
/// passthrough; the production defaults (intercept/slope/min/max) live as
/// constructor defaults so loading a v1 JSON file picks them up automatically.
/// </summary>
public sealed class MapCalibrationLocateOptionsV2MigrateTests
{
    [Fact]
    public void Current_version_is_3()
    {
        // mithril#1153 bumped to v3 to lift persisted scaleMax 1.20 → 2.00 for
        // existing installs. The pin here is the load-bearing signal that a
        // future schema bump didn't quietly down-rev the version constant.
        MapCalibrationLocateOptions.CurrentVersion.Should().Be(3);
    }

    [Fact]
    public void Migrate_v1_to_v2_preserves_existing_knobs_and_default_inits_blur_props()
    {
        var loaded = new MapCalibrationLocateOptions
        {
            SchemaVersion = 1,
            FallbackNccFloor = 0.30,
        };

        var migrated = MapCalibrationLocateOptions.Migrate(loaded);

        // Migrate is an identity passthrough — the loader writes SchemaVersion
        // back to Version after this returns.
        migrated.SchemaVersion.Should().Be(1);
        migrated.FallbackNccFloor.Should().Be(0.30);

        // New v2 fields initialise from constructor defaults — the Plan Task 0
        // measured σ-curve coefficients. RendererBlurEnabled default-on is the
        // production intent.
        migrated.RendererBlurEnabled.Should().BeTrue();
        migrated.RendererBlurIntercept.Should().Be(-1.5643);
        migrated.RendererBlurSlope.Should().Be(1.0043);
        migrated.RendererBlurMinSigma.Should().Be(0.0);
        migrated.RendererBlurMaxSigma.Should().Be(3.0);
    }

    [Fact]
    public void Migrate_is_identity_when_already_at_current_version()
    {
        // mithril#1153 bumped CurrentVersion 2 → 3; pin uses the new current
        // sentinel so the test name remains accurate.
        var loaded = new MapCalibrationLocateOptions
        {
            SchemaVersion = 3,
            RendererBlurEnabled = false,
            RendererBlurSlope = 2.5,
        };

        var migrated = MapCalibrationLocateOptions.Migrate(loaded);

        ReferenceEquals(loaded, migrated).Should().BeTrue();
        migrated.RendererBlurEnabled.Should().BeFalse();
        migrated.RendererBlurSlope.Should().Be(2.5);
    }
}
