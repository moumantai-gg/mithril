using System.Text.Json;
using FluentAssertions;
using Mithril.Shell;
using Xunit;

namespace Mithril.Shell.Tests;

/// <summary>
/// #984 Task 9 — schema v1 → v2 migration.
/// Old JSON with <c>dumpCalibrationCaptureFrames: true</c> must deserialize and
/// migrate to <see cref="ShellSettings.DumpCalibrationBundles"/> == true via the
/// obsolete-shim setter on <see cref="ShellSettings.DumpCalibrationCaptureFrames"/>
/// (invoked by STJ during deserialization of the old key), followed by a
/// <see cref="ShellSettings.Migrate"/> call that stamps the schema version.
/// </summary>
public sealed class ShellSettingsCalibrationBundleMigrationTests
{
    [Fact]
    public void Migrates_DumpCalibrationCaptureFrames_to_DumpCalibrationBundles()
    {
        // Old-shape JSON (v1): renamed field present + dropped gray field present.
        var oldJson = """
        {
            "schemaVersion": 1,
            "dumpCalibrationCaptureFrames": true,
            "dumpCalibrationGrayFrames": true
        }
        """;

        // Deserialize — STJ calls the obsolete shim setter which lifts the value.
        var settings = JsonSerializer.Deserialize<ShellSettings>(
            oldJson, ShellSettingsJsonContext.Default.ShellSettings)!;

        // Then migrate — stamps the current schema version.
        settings = ShellSettings.Migrate(settings);

        settings.DumpCalibrationBundles.Should().BeTrue(
            "the v1 dumpCalibrationCaptureFrames=true should be lifted into DumpCalibrationBundles");
        settings.SchemaVersion.Should().Be(ShellSettings.Version);
    }

    [Fact]
    public void Old_json_with_capture_frames_false_migrates_to_false()
    {
        var oldJson = """
        {
            "schemaVersion": 1,
            "dumpCalibrationCaptureFrames": false
        }
        """;

        var settings = JsonSerializer.Deserialize<ShellSettings>(
            oldJson, ShellSettingsJsonContext.Default.ShellSettings)!;
        settings = ShellSettings.Migrate(settings);

        settings.DumpCalibrationBundles.Should().BeFalse();
    }

    [Fact]
    public void Old_json_without_capture_frames_key_migrates_to_false()
    {
        var oldJson = """{ "schemaVersion": 1, "gameRoot": "C:/PG" }""";

        var settings = JsonSerializer.Deserialize<ShellSettings>(
            oldJson, ShellSettingsJsonContext.Default.ShellSettings)!;
        settings = ShellSettings.Migrate(settings);

        settings.DumpCalibrationBundles.Should().BeFalse(
            "absent key → false (additive bool default)");
    }

    [Fact]
    public void V1_gray_only_enabled_migrates_to_bundles_false()
    {
        // Gray-only-enabled users lose the diagnostic dump on migration — intentional; see spec design rationale.
        var oldJson = """
        {
            "schemaVersion": 1,
            "dumpCalibrationGrayFrames": true,
            "dumpCalibrationCaptureFrames": false
        }
        """;

        var settings = JsonSerializer.Deserialize<ShellSettings>(
            oldJson, ShellSettingsJsonContext.Default.ShellSettings)!;
        settings = ShellSettings.Migrate(settings);

        settings.DumpCalibrationBundles.Should().BeFalse(
            "the bundle toggle is driven only by the capture-frames flag; the gray flag is silently dropped");
        settings.SchemaVersion.Should().Be(ShellSettings.Version);
    }
}
