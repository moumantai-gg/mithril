using System.IO;
using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.Shared.Settings;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// Round-trip + Migrate dispatch tests for the mithril#1061 versioned-settings
/// backing of <see cref="MapCalibrationLocateOptions"/>.
/// </summary>
public sealed class MapCalibrationLocateOptionsPersistenceTests
{
    [Fact]
    public void Load_returns_defaults_when_file_is_absent()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"mithril-locate-test-{System.Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonSettingsStore<MapCalibrationLocateOptions>(
                tmp, MapCalibrationLocateOptionsJsonContext.Default.MapCalibrationLocateOptions);
            var loaded = store.Load();

            loaded.Should().NotBeNull();
            // Raw store returns `new T()` when the file is absent — that picks
            // up the type's default SchemaVersion (1), not the current Version.
            // The versioned-settings DI wrapper bumps to CurrentVersion via
            // Migrate; that's covered separately in
            // MapCalibrationLocateOptionsV2MigrateTests in the Detection test
            // project. mithril#1070 bumped Version from 1 → 2.
            loaded.SchemaVersion.Should().Be(1);
            loaded.FallbackNccFloor.Should().Be(0.20);
            loaded.ScaleMin.Should().Be(0.20);
            loaded.OrbNFeatures.Should().Be(8000);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void Save_then_load_preserves_custom_values()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"mithril-locate-test-{System.Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonSettingsStore<MapCalibrationLocateOptions>(
                tmp, MapCalibrationLocateOptionsJsonContext.Default.MapCalibrationLocateOptions);
            var write = new MapCalibrationLocateOptions
            {
                FallbackNccFloor = 0.30,
                ScaleMin = 0.15,
                ScaleMax = 1.50,
                OrbNFeatures = 12000,
            };
            store.Save(write);

            var read = store.Load();
            read.FallbackNccFloor.Should().Be(0.30);
            read.ScaleMin.Should().Be(0.15);
            read.ScaleMax.Should().Be(1.50);
            read.OrbNFeatures.Should().Be(12000);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void Migrate_returns_loaded_instance_unchanged_at_current_version()
    {
        var loaded = new MapCalibrationLocateOptions
        {
            SchemaVersion = MapCalibrationLocateOptions.Version,
            FallbackNccFloor = 0.42,
        };
        var migrated = MapCalibrationLocateOptions.Migrate(loaded);
        migrated.FallbackNccFloor.Should().Be(0.42);
        migrated.SchemaVersion.Should().Be(MapCalibrationLocateOptions.Version);
    }

    [Fact]
    public void Migrate_no_op_passes_through_when_schema_version_zero()
    {
        // A hypothetical legacy file without schemaVersion deserialises with the
        // default value 1; this test exercises the explicit-0 path to lock the
        // no-op contract (so a future v1 → v2 migration starts from a known place).
        var legacy = new MapCalibrationLocateOptions
        {
            SchemaVersion = 0,
            FallbackNccFloor = 0.33,
        };
        var migrated = MapCalibrationLocateOptions.Migrate(legacy);
        migrated.FallbackNccFloor.Should().Be(0.33,
            "Migrate must not silently zero out user customisations");
    }

    [Fact]
    public void Serialised_json_uses_camelCase_property_names()
    {
        var opts = new MapCalibrationLocateOptions { FallbackNccFloor = 0.25 };
        var s = JsonSerializer.Serialize(opts,
            MapCalibrationLocateOptionsJsonContext.Default.MapCalibrationLocateOptions);

        s.Should().Contain("\"fallbackNccFloor\": 0.25");
        s.Should().Contain("\"schemaVersion\": 1");
        s.Should().NotContain("\"FallbackNccFloor\"", "STJ source-gen must emit camelCase per the context attribute");
    }
}
