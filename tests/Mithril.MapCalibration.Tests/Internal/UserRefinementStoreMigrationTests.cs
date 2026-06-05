using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Internal;

/// <summary>
/// <see cref="UserRefinementStore"/> load-time schema migration. Covers:
/// <list type="bullet">
///   <item>v1 → v2: bare area keys (e.g. <c>AreaSerbule</c>) prefixed with
///     <c>Map_</c> at load time (mithril#1021 task 18).</item>
///   <item>v1/v2 → v3: each <see cref="AreaCalibration"/> entry is nested under
///     the <see cref="SceneRefinements"/> slot named by its
///     <see cref="AreaCalibration.Frame"/> field (mithril#1082 §4).</item>
///   <item>v2 → v3 narrow-window fix-up: a record with
///     <c>Source=UserRefinement</c> + <c>Frame=Texture</c> routes to the Overlay
///     slot anyway (spec §4.1; ~24-hour window between #1077 and #1083).</item>
///   <item>v3 idempotence: a Schema-3 file loads without rewrite.</item>
/// </list>
/// </summary>
public sealed class UserRefinementStoreMigrationTests : IDisposable
{
    private readonly string _dir;

    public UserRefinementStoreMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mithril-refstore-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* CI temp dir gets reaped */ }
    }

    private string Path_ => Path.Combine(_dir, "refinements.json");

    private const string V1Json = """
        {
          "calibrations": {
            "AreaSerbule": {
              "scale": 0.82, "rotationRadians": 0.0, "originX": 100.0, "originY": 200.0,
              "referenceCount": 4, "residualPixels": 0.5,
              "source": "UserRefinement", "schemaVersion": 1, "calibrationZoom": 1.0, "mirrorNorth": false
            }
          }
        }
        """;

    [Fact]
    public void Load_V1File_PrefixesKeysWithMapAndPersistsAsV3()
    {
        // mithril#1082: v1 → v3 composes the v1 → v2 step (Map_ prefix +
        // Source-based Frame inference) with v2 → v3 (nest under typed slot).
        // The v1 record's source is UserRefinement → Frame inferred to Overlay
        // → record lands in the Overlay slot.
        File.WriteAllText(Path_, V1Json);

        var store = new UserRefinementStore(_dir);

        store.TryGet("Map_AreaSerbule", CalibrationFrame.Overlay, out var cal).Should().BeTrue();
        cal.Scale.Should().BeApproximately(0.82, 1e-9);
        cal.Frame.Should().Be(CalibrationFrame.Overlay);

        // File rewritten with schemaVersion 3, Map_-prefixed key, value nested
        // under "overlay" slot.
        using var doc = JsonDocument.Parse(File.ReadAllText(Path_));
        doc.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("calibrations").EnumerateObject()
            .Select(p => p.Name).Should().ContainSingle().Which.Should().Be("Map_AreaSerbule");
        var scene = doc.RootElement.GetProperty("calibrations").GetProperty("Map_AreaSerbule");
        scene.TryGetProperty("overlay", out _).Should().BeTrue("v1 UserRefinement record infers to Overlay frame");
        scene.TryGetProperty("texture", out _).Should().BeFalse("the v1 record was UserRefinement → not texture-frame");
    }

    [Fact]
    public void Load_V2File_HappyPath_MigratesToV3()
    {
        // mithril#1082 spec §4 happy path: a v2 record with explicit
        // Frame=Texture + Source=AutoCapture nests under the "texture" slot.
        const string v2 = """
            {
              "schemaVersion": 2,
              "calibrations": {
                "Map_AreaSerbule": {
                  "scale": 0.82, "rotationRadians": 0.0, "originX": 100.0, "originY": 200.0,
                  "referenceCount": 4, "residualPixels": 0.5,
                  "source": "AutoCapture", "schemaVersion": 1, "calibrationZoom": 1.0,
                  "mirrorNorth": false, "frame": "Texture"
                }
              }
            }
            """;
        File.WriteAllText(Path_, v2);

        var store = new UserRefinementStore(_dir);

        store.TryGet("Map_AreaSerbule", CalibrationFrame.Texture, out var cal).Should().BeTrue();
        cal.Source.Should().Be(CalibrationSource.AutoCapture);
        cal.Frame.Should().Be(CalibrationFrame.Texture);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path_));
        doc.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(3);
        var scene = doc.RootElement.GetProperty("calibrations").GetProperty("Map_AreaSerbule");
        scene.TryGetProperty("texture", out _).Should().BeTrue();
        scene.TryGetProperty("overlay", out _).Should().BeFalse("unused slot is dropped on write");
    }

    [Fact]
    public void Load_V2File_NarrowWindowFixup_RoutesUserRefinementTextureToOverlay()
    {
        // mithril#1082 spec §4.1: between #1077 (Frame field landed, defaulting
        // Texture) and #1083 (save sites stamp Frame explicitly), the Legolas
        // wizard could persist Source=UserRefinement + Frame=Texture even
        // though the fit is geometrically overlay-frame. The load path
        // recognises this combination and routes to the Overlay slot anyway +
        // emits a warn-log.
        const string narrowWindow = """
            {
              "schemaVersion": 2,
              "calibrations": {
                "Map_KhyruleksCrypt": {
                  "scale": 0.82, "rotationRadians": 0.0, "originX": 100.0, "originY": 200.0,
                  "referenceCount": 4, "residualPixels": 0.5,
                  "source": "UserRefinement", "schemaVersion": 1, "calibrationZoom": 1.0,
                  "mirrorNorth": false, "frame": "Texture"
                }
              }
            }
            """;
        File.WriteAllText(Path_, narrowWindow);

        var logger = new RecordingLogger();
        var store = new UserRefinementStore(_dir, logger);

        // The record landed in the Overlay slot, NOT Texture.
        store.TryGet("Map_KhyruleksCrypt", CalibrationFrame.Overlay, out var overlayCal).Should().BeTrue();
        overlayCal.Frame.Should().Be(CalibrationFrame.Overlay);
        store.TryGet("Map_KhyruleksCrypt", CalibrationFrame.Texture, out _).Should().BeFalse();

        // A warn-log fired calling out the fix-up.
        logger.Warnings.Should().Contain(w =>
            w.Contains("Map_KhyruleksCrypt", StringComparison.Ordinal) &&
            w.Contains("Overlay", StringComparison.Ordinal));

        // Persisted shape reflects the routed slot.
        using var doc = JsonDocument.Parse(File.ReadAllText(Path_));
        doc.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(3);
        var scene = doc.RootElement.GetProperty("calibrations").GetProperty("Map_KhyruleksCrypt");
        scene.TryGetProperty("overlay", out _).Should().BeTrue();
        scene.TryGetProperty("texture", out _).Should().BeFalse();
    }

    [Fact]
    public void Load_V1FileWithAlreadyPrefixedKey_NotDoublePrefixed()
    {
        const string weird = """
            {
              "calibrations": {
                "Map_AreaSerbule": {
                  "scale": 0.82, "rotationRadians": 0.0, "originX": 100.0, "originY": 200.0,
                  "referenceCount": 4, "residualPixels": 0.5,
                  "source": "UserRefinement", "schemaVersion": 1, "calibrationZoom": 1.0, "mirrorNorth": false
                }
              }
            }
            """;
        File.WriteAllText(Path_, weird);

        var store = new UserRefinementStore(_dir);
        store.TryGetAny("Map_AreaSerbule", out var slots).Should().BeTrue();
        store.TryGetAny("Map_Map_AreaSerbule", out _).Should().BeFalse();

        // The v1 record has Source=UserRefinement → Frame inferred to Overlay →
        // record lands in the Overlay slot (not Texture).
        slots.Overlay.Should().NotBeNull("v1 UserRefinement record infers to Overlay frame");
        slots.Texture.Should().BeNull("the v1 record was UserRefinement → not texture-frame");
    }

    [Fact]
    public void Load_MissingFile_NoMigration_NoCrash()
    {
        var store = new UserRefinementStore(_dir);
        store.All.Should().BeEmpty();
    }

    [Fact]
    public void Load_V3File_Idempotent_NoRewrite()
    {
        // mithril#1082: a pre-migrated v3 file loads and is NOT rewritten.
        // We assert this by writing a hand-crafted v3 file, snapshotting bytes,
        // and asserting bytes-equal after Load.
        const string v3 = """
            {
              "schemaVersion": 3,
              "calibrations": {
                "Map_AreaSerbule": {
                  "texture": {
                    "scale": 0.82,
                    "rotationRadians": 0.0,
                    "originX": 100.0,
                    "originY": 200.0,
                    "referenceCount": 4,
                    "residualPixels": 0.5,
                    "mirrorNorth": false,
                    "calibrationZoom": 1.0,
                    "source": "AutoCapture",
                    "schemaVersion": 1,
                    "frame": "Texture"
                  }
                }
              }
            }
            """;
        File.WriteAllText(Path_, v3);
        var before = File.ReadAllBytes(Path_);

        var store = new UserRefinementStore(_dir);

        // Sanity: record loaded into the right slot.
        store.TryGet("Map_AreaSerbule", CalibrationFrame.Texture, out var cal).Should().BeTrue();
        cal.Source.Should().Be(CalibrationSource.AutoCapture);

        // Bytes unchanged — no rewrite, no migration.
        File.ReadAllBytes(Path_).Should().Equal(before);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
