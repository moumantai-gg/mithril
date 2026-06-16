using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// mithril#1153 — covers the v2 → v3 migration on
/// <see cref="MapCalibrationLocateOptions"/>. The migrate body lifts the
/// persisted <see cref="MapCalibrationLocateOptions.ScaleMax"/> from the old
/// default 1.20 to the new default 2.00 ONLY when it has not been explicitly
/// customised — so a user who set their own value (e.g. 1.5 to work around the
/// pre-#1153 failure mode) keeps their override.
///
/// <para>This migration is load-bearing: without it the constructor-default
/// bump is a no-op for every existing install, because <c>JsonSettingsStore</c>
/// writes scaleMax to disk and <c>AddMithrilVersionedSettings</c> only
/// dispatches through <c>Migrate</c> when <c>SchemaVersion != CurrentVersion</c>.
/// A real on-disk file from this dev's machine confirmed at PR time:
/// <c>"schemaVersion": 2, "scaleMax": 1.2</c>.</para>
/// </summary>
public sealed class MapCalibrationLocateOptionsV3MigrateTests
{
    [Fact]
    public void Migrate_v2_to_v3_lifts_prior_default_scaleMax_to_two()
    {
        var loaded = new MapCalibrationLocateOptions
        {
            SchemaVersion = 2,
            ScaleMax = 1.20,  // mirrors a v2 file on disk for any pre-#1153 install
        };

        var migrated = MapCalibrationLocateOptions.Migrate(loaded);

        migrated.ScaleMax.Should().Be(2.00,
            "the v2 → v3 migration lifts the prior default 1.20 to the new default 2.00 for existing installs");
        // Loader stamps the bumped SchemaVersion back after Migrate returns,
        // so Migrate itself does not need to set it.
        migrated.SchemaVersion.Should().Be(2);
    }

    [Fact]
    public void Migrate_v2_to_v3_preserves_explicit_user_override()
    {
        var loaded = new MapCalibrationLocateOptions
        {
            SchemaVersion = 2,
            ScaleMax = 1.50,  // a user who explicitly tuned their value
        };

        var migrated = MapCalibrationLocateOptions.Migrate(loaded);

        // The migration ONLY lifts the value when it equals the OLD default
        // (1.20) — any other value is treated as an explicit user choice and
        // preserved as-is.
        migrated.ScaleMax.Should().Be(1.50);
    }

    [Theory]
    [InlineData(0.20)]   // ScaleMin floor — would be a deliberate degenerate override
    [InlineData(1.00)]   // 1.00 — exactly 1× zoom intent
    [InlineData(1.19)]   // just under the prior default
    [InlineData(1.21)]   // just over the prior default
    [InlineData(2.00)]   // already at the new default (e.g. an early adopter)
    [InlineData(3.50)]   // explicit user widening past the new default
    public void Migrate_v2_to_v3_only_resets_exact_prior_default(double explicitValue)
    {
        var loaded = new MapCalibrationLocateOptions
        {
            SchemaVersion = 2,
            ScaleMax = explicitValue,
        };

        var migrated = MapCalibrationLocateOptions.Migrate(loaded);

        migrated.ScaleMax.Should().Be(explicitValue,
            "the v2 → v3 reset is gated exact-equal to the old 1.20 default — every other value is treated as a user choice");
    }

    [Fact]
    public void Migrate_v1_to_v3_lifts_prior_default_scaleMax_cumulatively()
    {
        // A v1 JSON file (pre-mithril#1070) that omitted the RendererBlur*
        // fields would have had scaleMax = 1.20 written explicitly (the
        // shipping default at the time of v1). Loading such a file lands the
        // v1 → v3 cumulative migration on this same body.
        var loaded = new MapCalibrationLocateOptions
        {
            SchemaVersion = 1,
            ScaleMax = 1.20,
        };

        var migrated = MapCalibrationLocateOptions.Migrate(loaded);

        migrated.ScaleMax.Should().Be(2.00);
        // The blur defaults arrive via the constructor (the v1 → v2 path is
        // additive and relies on the v2-onwards constructor defaults for
        // RendererBlur*; the v3 migrate inherits that behavior).
        migrated.RendererBlurEnabled.Should().BeTrue();
        migrated.RendererBlurIntercept.Should().Be(-1.5643);
    }

    [Fact]
    public void Migrate_returns_same_reference_when_resetting_scaleMax()
    {
        // The migration mutates the loaded instance in place and returns it
        // (cheap — no allocation). Pin so a future refactor that switches to
        // a copy-and-return shape is an intentional change.
        var loaded = new MapCalibrationLocateOptions
        {
            SchemaVersion = 2,
            ScaleMax = 1.20,
        };

        var migrated = MapCalibrationLocateOptions.Migrate(loaded);

        ReferenceEquals(loaded, migrated).Should().BeTrue();
    }
}
