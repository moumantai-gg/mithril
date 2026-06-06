using System.Text.Json;
using FluentAssertions;
using Legolas.Domain;

namespace Legolas.Tests.Settings;

public class LegolasSettingsMigrationTests
{
    [Fact]
    public void Migrates_v1_pinPending_into_outer_stroke_and_center_fill()
    {
        var json = """
            {
              "colors": {
                "pinPending": "#FF00FF00",
                "pinFinalized": "#FFFF8800",
                "playerMarker": "#FFAA00AA",
                "routeLine": "#FFFFD700",
                "bearingWedgeFill": "#33FFFF80",
                "bearingWedgeStroke": "#55FFFF80"
              }
            }
            """;
        var loaded = JsonSerializer.Deserialize(json, LegolasSettingsJsonContext.Default.LegolasSettings)!;

        // SchemaVersion missing in v1 JSON → C# default of 1; Migrate should
        // detect the gap and run the upgrade.
        loaded.SchemaVersion.Should().Be(1);
        loaded.Colors.LegacyPinPending.Should().Be("#FF00FF00");
        loaded.Colors.LegacyPinFinalized.Should().Be("#FFFF8800");
        loaded.Colors.LegacyPlayerMarker.Should().Be("#FFAA00AA");

        var migrated = LegolasSettings.Migrate(loaded);

        migrated.PinStyle.Outer.StrokeColor.Should().Be("#FF00FF00");
        migrated.PinStyle.Center.FillColor.Should().Be("#FF00FF00");
        migrated.ActivePinStyle.Color.Should().Be("#FFFF8800");
        migrated.PlayerPinStyle.Center.FillColor.Should().Be("#FFAA00AA");
        migrated.Colors.LegacyPinPending.Should().BeNull();
        migrated.Colors.LegacyPinFinalized.Should().BeNull();
        migrated.Colors.LegacyPlayerMarker.Should().BeNull();
    }

    [Fact]
    public void Migrating_a_v2_instance_is_a_no_op()
    {
        var fresh = new LegolasSettings();
        fresh.PinStyle.Outer.StrokeColor = "#FFAA0000"; // user customisation
        fresh.PinStyle.Center.FillColor = "#FF00AA00";
        fresh.ActivePinStyle.Color = "#FF0000AA";

        var migrated = LegolasSettings.Migrate(fresh);

        // SchemaVersion was already current → no copy / no overwrite of customisations.
        migrated.PinStyle.Outer.StrokeColor.Should().Be("#FFAA0000");
        migrated.PinStyle.Center.FillColor.Should().Be("#FF00AA00");
        migrated.ActivePinStyle.Color.Should().Be("#FF0000AA");
    }

    [Fact]
    public void V1_blob_with_only_pinPending_leaves_active_pin_color_at_default()
    {
        var json = """
            {
              "colors": {
                "pinPending": "#FF00FF00"
              }
            }
            """;
        var loaded = JsonSerializer.Deserialize(json, LegolasSettingsJsonContext.Default.LegolasSettings)!;
        var defaultActiveColor = new LegolasActivePinStyle().Color;
        var defaultPlayerCenterFill = LegolasPinStyle.PlayerDefaults().Center.FillColor;

        var migrated = LegolasSettings.Migrate(loaded);

        migrated.PinStyle.Outer.StrokeColor.Should().Be("#FF00FF00");
        migrated.PinStyle.Center.FillColor.Should().Be("#FF00FF00");
        migrated.ActivePinStyle.Color.Should().Be(defaultActiveColor);
        migrated.PlayerPinStyle.Center.FillColor.Should().Be(defaultPlayerCenterFill);
    }

    [Fact]
    public void V1_blob_with_only_playerMarker_migrates_into_player_pin_center_fill()
    {
        var json = """
            {
              "colors": {
                "playerMarker": "#FF4488FF"
              }
            }
            """;
        var loaded = JsonSerializer.Deserialize(json, LegolasSettingsJsonContext.Default.LegolasSettings)!;

        var migrated = LegolasSettings.Migrate(loaded);

        migrated.PlayerPinStyle.Center.FillColor.Should().Be("#FF4488FF");
        // Outer player defaults preserved (white stroke from PlayerDefaults factory).
        migrated.PlayerPinStyle.Outer.StrokeColor.Should().Be("#FFFFFFFF");
    }

    [Fact]
    public void Migrated_settings_round_trip_through_json_drop_legacy_fields()
    {
        var v1 = """
            {
              "colors": {
                "pinPending": "#FF00FF00",
                "pinFinalized": "#FFFF8800",
                "playerMarker": "#FFAA00AA"
              }
            }
            """;
        var loaded = JsonSerializer.Deserialize(v1, LegolasSettingsJsonContext.Default.LegolasSettings)!;
        var migrated = LegolasSettings.Migrate(loaded);
        migrated.SchemaVersion = LegolasSettings.CurrentVersion;

        var written = JsonSerializer.Serialize(migrated, LegolasSettingsJsonContext.Default.LegolasSettings);

        // Legacy field keys should not reappear in saved JSON now that they
        // were cleared.
        written.Should().NotContain("pinPending");
        written.Should().NotContain("pinFinalized");
        written.Should().NotContain("playerMarker");
        written.Should().Contain($"\"schemaVersion\": {LegolasSettings.CurrentVersion}");
    }

    [Fact]
    public void V2_json_without_areaCalibrations_migrates_to_empty_dictionary()
    {
        // A v2 blob: has schemaVersion 2, no areaCalibrations key at all.
        var v2 = """
            {
              "schemaVersion": 2,
              "pinStyle": { "outer": { "strokeColor": "#FFAA0000" } }
            }
            """;
        var loaded = JsonSerializer.Deserialize(v2, LegolasSettingsJsonContext.Default.LegolasSettings)!;
        loaded.SchemaVersion.Should().Be(2);

        var migrated = LegolasSettings.Migrate(loaded);

        // v2 → v3 is a no-op: new dict defaults to empty (= "no area calibrated"),
        // and the user's prior customisation is untouched.
        migrated.AreaCalibrations.Should().BeEmpty();
        migrated.PinStyle.Outer.StrokeColor.Should().Be("#FFAA0000");
    }

    [Fact]
    public void V3_json_without_calibrationPinStyle_migrates_to_default_no_op()
    {
        // A v3 blob: schemaVersion 3, a customised pinStyle, no
        // calibrationPinStyle key at all (it didn't exist before #478).
        var v3 = """
            {
              "schemaVersion": 3,
              "pinStyle": { "outer": { "strokeColor": "#FFAA0000" } },
              "areaCalibrations": {}
            }
            """;
        var loaded = JsonSerializer.Deserialize(v3, LegolasSettingsJsonContext.Default.LegolasSettings)!;
        loaded.SchemaVersion.Should().Be(3);

        var migrated = LegolasSettings.Migrate(loaded);

        // v3 → v4 is a no-op: the new sub-object defaults to the pre-#478
        // hardcoded look and nothing else is touched (the always-run v1→v2
        // colour block is itself a no-op on a v3 blob — legacy fields absent).
        var defaults = LegolasPinStyle.CalibrationDefaults();
        migrated.CalibrationPinStyle.Outer.StrokeColor.Should().Be(defaults.Outer.StrokeColor);
        migrated.CalibrationPinStyle.Outer.Size.Should().Be(defaults.Outer.Size);
        migrated.CalibrationPinStyle.Center.FillColor.Should().Be(defaults.Center.FillColor);
        migrated.CalibrationPinStyle.Center.Size.Should().Be(defaults.Center.Size);
        // User's prior customisation untouched.
        migrated.PinStyle.Outer.StrokeColor.Should().Be("#FFAA0000");
    }

    [Fact]
    public void CalibrationPinStyle_round_trips_through_json()
    {
        var settings = new LegolasSettings { SchemaVersion = LegolasSettings.CurrentVersion };
        settings.CalibrationPinStyle.Outer.StrokeColor = "#FF112233";
        settings.CalibrationPinStyle.Outer.Size = 30.0;
        settings.CalibrationPinStyle.Center.FillColor = "#80445566";
        settings.CalibrationPinStyle.Center.Shape = PinShape.Diamond;

        var written = JsonSerializer.Serialize(settings, LegolasSettingsJsonContext.Default.LegolasSettings);
        var reloaded = JsonSerializer.Deserialize(written, LegolasSettingsJsonContext.Default.LegolasSettings)!;

        reloaded.CalibrationPinStyle.Outer.StrokeColor.Should().Be("#FF112233");
        reloaded.CalibrationPinStyle.Outer.Size.Should().Be(30.0);
        reloaded.CalibrationPinStyle.Center.FillColor.Should().Be("#80445566");
        reloaded.CalibrationPinStyle.Center.Shape.Should().Be(PinShape.Diamond);
        written.Should().Contain($"\"schemaVersion\": {LegolasSettings.CurrentVersion}");
    }

    [Fact]
    public void AreaCalibrations_round_trip_through_json()
    {
        var settings = new LegolasSettings { SchemaVersion = LegolasSettings.CurrentVersion };
        settings.AreaCalibrations["AreaEltibule"] =
            new AreaCalibration(Scale: 2.5, RotationRadians: 0.3, OriginX: 120, OriginY: 340,
                ReferenceCount: 3, ResidualPixels: 1.75);

        var written = JsonSerializer.Serialize(settings, LegolasSettingsJsonContext.Default.LegolasSettings);
        var reloaded = JsonSerializer.Deserialize(written, LegolasSettingsJsonContext.Default.LegolasSettings)!;

        reloaded.AreaCalibrations.Should().ContainKey("AreaEltibule");
        var c = reloaded.AreaCalibrations["AreaEltibule"];
        c.Scale.Should().BeApproximately(2.5, 1e-9);
        c.RotationRadians.Should().BeApproximately(0.3, 1e-9);
        c.OriginX.Should().BeApproximately(120, 1e-9);
        c.OriginY.Should().BeApproximately(340, 1e-9);
        c.ReferenceCount.Should().Be(3);
        c.ResidualPixels.Should().BeApproximately(1.75, 1e-9);
        // mithril#1095: SchemaVersion bumped to 3 (skip 2) to mark the
        // no-CalibrationZoom invariant unambiguously.
        c.SchemaVersion.Should().Be(3);
    }

    [Fact]
    public void V5_blob_with_mapOverlay_loads_and_drops_the_retired_key_on_save()
    {
        // #957: MapOverlay was retired (the survey overlay reads its frame from the
        // shell capture rect now). A v5 blob still has the orphaned mapOverlay key;
        // it must load without error, Migrate is a no-op for v5+, and the re-saved
        // v6 JSON no longer carries the key (STJ ignores it on load + drops on save).
        var v5 = """
            {
              "schemaVersion": 5,
              "mapOverlay": { "left": 220, "top": 140, "width": 960, "height": 540 },
              "pinStyle": { "outer": { "strokeColor": "#FFAA0000" } }
            }
            """;
        var loaded = JsonSerializer.Deserialize(v5, LegolasSettingsJsonContext.Default.LegolasSettings)!;
        loaded.SchemaVersion.Should().Be(5);

        var migrated = LegolasSettings.Migrate(loaded);
        migrated.SchemaVersion = LegolasSettings.CurrentVersion;

        // v5 → v6 leaves everything else untouched.
        migrated.PinStyle.Outer.StrokeColor.Should().Be("#FFAA0000");

        var written = JsonSerializer.Serialize(migrated, LegolasSettingsJsonContext.Default.LegolasSettings);
        written.Should().NotContain("mapOverlay", "the retired field is no longer declared, so STJ drops it");
        written.Should().Contain($"\"schemaVersion\": {LegolasSettings.CurrentVersion}");
        // Sibling overlay layouts are unaffected by the retire.
        written.Should().Contain("inventoryOverlay");
    }

    [Fact]
    public void MotherlodeMultiMapMode_defaults_true_and_round_trips()
    {
        // Additive (#488): a v4 blob without the key loads the true default,
        // so no SchemaVersion bump / Migrate branch is needed.
        var legacy = JsonSerializer.Deserialize(
            "{ \"schemaVersion\": 4 }", LegolasSettingsJsonContext.Default.LegolasSettings)!;
        legacy.MotherlodeMultiMapMode.Should().BeTrue();

        var settings = new LegolasSettings { MotherlodeMultiMapMode = false };
        var written = JsonSerializer.Serialize(settings, LegolasSettingsJsonContext.Default.LegolasSettings);
        var reloaded = JsonSerializer.Deserialize(written, LegolasSettingsJsonContext.Default.LegolasSettings)!;
        reloaded.MotherlodeMultiMapMode.Should().BeFalse();
    }
}
