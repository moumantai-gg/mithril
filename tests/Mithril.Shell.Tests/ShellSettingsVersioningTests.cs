using System.Text.Json;
using FluentAssertions;
using Mithril.Shell;
using Xunit;

namespace Mithril.Shell.Tests;

/// <summary>
/// #957 (#208): ShellSettings became schema-versioned. Pins the v1 invariants — a
/// fresh instance is current, <see cref="ShellSettings.Migrate"/> is an identity
/// passthrough, and the version round-trips through the source-gen JSON context.
/// </summary>
public sealed class ShellSettingsVersioningTests
{
    [Fact]
    public void Current_version_is_two_and_fresh_instances_are_current()
    {
        ShellSettings.CurrentVersion.Should().Be(2);
        new ShellSettings().SchemaVersion.Should().Be(ShellSettings.CurrentVersion);
    }

    [Fact]
    public void Migrate_stamps_current_version_and_preserves_other_fields()
    {
        var s = new ShellSettings { GameRoot = @"C:\Games\PG", SidebarWidth = 333, SchemaVersion = 1 };

        var migrated = ShellSettings.Migrate(s);

        migrated.Should().BeSameAs(s);
        migrated.SchemaVersion.Should().Be(ShellSettings.CurrentVersion);
        migrated.GameRoot.Should().Be(@"C:\Games\PG");
        migrated.SidebarWidth.Should().Be(333);
    }

    [Fact]
    public void Legacy_unversioned_json_loads_as_current_shape()
    {
        // A pre-#957 shell.json never carried a schemaVersion key. STJ leaves the
        // property at its initializer (= current), so the loader treats it as v1 (it
        // IS the v1 shape) — no spurious migration, no throw.
        var loaded = JsonSerializer.Deserialize(
            """{ "gameRoot": "C:/PG", "sidebarWidth": 280 }""",
            ShellSettingsJsonContext.Default.ShellSettings)!;

        loaded.SchemaVersion.Should().Be(ShellSettings.CurrentVersion);
        loaded.GameRoot.Should().Be("C:/PG");
    }

    [Fact]
    public void Schema_version_round_trips_through_json()
    {
        var written = JsonSerializer.Serialize(
            new ShellSettings(), ShellSettingsJsonContext.Default.ShellSettings);

        written.Should().Contain($"\"schemaVersion\": {ShellSettings.CurrentVersion}");
    }

    [Fact]
    public void Install_root_round_trips_through_json()
    {
        // #959: InstallRoot is an additive field (no schema bump). It must persist
        // and reload distinctly from GameRoot (data dir vs install dir).
        var written = JsonSerializer.Serialize(
            new ShellSettings { GameRoot = @"C:\Data\PG", InstallRoot = @"D:\Steam\common\Project Gorgon" },
            ShellSettingsJsonContext.Default.ShellSettings);

        var loaded = JsonSerializer.Deserialize(
            written, ShellSettingsJsonContext.Default.ShellSettings)!;

        loaded.InstallRoot.Should().Be(@"D:\Steam\common\Project Gorgon");
        loaded.GameRoot.Should().Be(@"C:\Data\PG");
    }

    [Fact]
    public void Install_root_defaults_to_empty_when_absent_from_legacy_json()
    {
        // A pre-#959 shell.json has no installRoot key → "" on load (additive field).
        var loaded = JsonSerializer.Deserialize(
            """{ "gameRoot": "C:/PG" }""",
            ShellSettingsJsonContext.Default.ShellSettings)!;

        loaded.InstallRoot.Should().BeEmpty();
    }

    [Fact]
    public void DumpCalibrationBundles_round_trips_through_json()
    {
        // #984: the bundle-dump toggle must persist and reload with the value intact.
        var written = JsonSerializer.Serialize(
            new ShellSettings { DumpCalibrationBundles = true },
            ShellSettingsJsonContext.Default.ShellSettings);

        var loaded = JsonSerializer.Deserialize(
            written, ShellSettingsJsonContext.Default.ShellSettings)!;

        loaded.DumpCalibrationBundles.Should().BeTrue();
    }

    [Fact]
    public void DumpCalibrationBundles_defaults_to_false_when_absent_from_json()
    {
        // A shell.json without the key defaults to false (additive bool field).
        var loaded = JsonSerializer.Deserialize(
            """{ "gameRoot": "C:/PG" }""",
            ShellSettingsJsonContext.Default.ShellSettings)!;

        loaded.DumpCalibrationBundles.Should().BeFalse();
    }
}
