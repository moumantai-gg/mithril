using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Internal;

/// <summary>
/// Verifies that the legacy <c>calibrationZoom</c> JSON field on persisted
/// <see cref="AreaCalibration"/> records is:
/// <list type="bullet">
///   <item>Silently dropped on load (no crash, no data loss).</item>
///   <item>Logged at <see cref="LogLevel.Information"/> once per affected
///     entry so the migration is observable in the diagnostics stream
///     (mithril#1095 Task P2.6).</item>
/// </list>
/// Covers all three load paths where legacy records can appear:
/// v1 (bare-key, no schemaVersion), v2 (Map_-prefixed, schemaVersion=2),
/// and v3 (SceneRefinements typed-slot, schemaVersion=3).
/// </summary>
public sealed class AreaCalibrationLegacyZoomMigrationTests : IDisposable
{
    private readonly string _dir;

    public AreaCalibrationLegacyZoomMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mithril-legacyzoom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* CI temp dir gets reaped */ }
    }

    private string Path_ => Path.Combine(_dir, "refinements.json");

    // -------------------------------------------------------------------------
    // 1. STJ drop: AreaCalibration deserialises cleanly even with calibrationZoom
    // -------------------------------------------------------------------------

    [Fact]
    public void Deserialize_RecordWithLegacyCalibrationZoom_LoadsCleanlyAndDropsField()
    {
        // Matches the spec's §P2.6 assertion: the AreaCalibration type no longer
        // carries CalibrationZoom, so STJ's unknown-property handling drops it
        // silently. The remaining fields must round-trip correctly.
        const string json = """
            { "scale": 1.0, "rotationRadians": 0, "originX": 0, "originY": 0,
              "referenceCount": 4, "residualPixels": 0.5, "calibrationZoom": 0.42 }
            """;

        var cal = JsonSerializer.Deserialize(json, MapCalibrationJsonContext.Default.AreaCalibration);

        cal.Should().NotBeNull();
        cal!.Scale.Should().Be(1.0);
        cal.ReferenceCount.Should().Be(4);
        cal.ResidualPixels.Should().BeApproximately(0.5, 1e-9);
    }

    // -------------------------------------------------------------------------
    // 2. v1 file: calibrationZoom triggers Info log
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_V1FileWithCalibrationZoom_LogsInfoAndLoadsCleanly()
    {
        const string v1 = """
            {
              "calibrations": {
                "AreaSerbule": {
                  "scale": 0.82, "rotationRadians": 0.0, "originX": 100.0, "originY": 200.0,
                  "referenceCount": 4, "residualPixels": 0.5,
                  "source": "UserRefinement", "schemaVersion": 1,
                  "calibrationZoom": 1.0, "mirrorNorth": false
                }
              }
            }
            """;
        File.WriteAllText(Path_, v1);

        var logger = new RecordingLogger();
        var store = new UserRefinementStore(_dir, logger);

        // Record loads correctly into the Overlay slot (v1 UserRefinement → Overlay).
        store.TryGet("Map_AreaSerbule", CalibrationFrame.Overlay, out var cal).Should().BeTrue();
        cal.Scale.Should().BeApproximately(0.82, 1e-9);

        // One Info log mentions the area key and the legacy field.
        logger.Infos.Should().Contain(msg =>
            msg.Contains("AreaSerbule", StringComparison.Ordinal) &&
            msg.Contains("calibrationZoom", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------------------
    // 3. v2 file: calibrationZoom triggers Info log
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_V2FileWithCalibrationZoom_LogsInfoAndLoadsCleanly()
    {
        const string v2 = """
            {
              "schemaVersion": 2,
              "calibrations": {
                "Map_AreaEltibule": {
                  "scale": 0.76, "rotationRadians": 0.0, "originX": 50.0, "originY": 60.0,
                  "referenceCount": 5, "residualPixels": 0.65,
                  "source": "AutoCapture", "schemaVersion": 1,
                  "calibrationZoom": 1.5, "mirrorNorth": false, "frame": "Texture"
                }
              }
            }
            """;
        File.WriteAllText(Path_, v2);

        var logger = new RecordingLogger();
        var store = new UserRefinementStore(_dir, logger);

        store.TryGet("Map_AreaEltibule", CalibrationFrame.Texture, out var cal).Should().BeTrue();
        cal.Scale.Should().BeApproximately(0.76, 1e-9);

        logger.Infos.Should().Contain(msg =>
            msg.Contains("Map_AreaEltibule", StringComparison.Ordinal) &&
            msg.Contains("calibrationZoom", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------------------
    // 4. v3 file: calibrationZoom inside a typed slot triggers Info log
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_V3FileWithCalibrationZoomInTextureSlot_LogsInfoAndLoadsCleanly()
    {
        const string v3 = """
            {
              "schemaVersion": 3,
              "calibrations": {
                "Map_AreaSerbule": {
                  "texture": {
                    "scale": 0.82, "rotationRadians": 0.0, "originX": 100.0, "originY": 200.0,
                    "referenceCount": 8, "residualPixels": 0.3,
                    "source": "AutoCapture", "schemaVersion": 1,
                    "calibrationZoom": 1.0, "mirrorNorth": false, "frame": "Texture"
                  }
                }
              }
            }
            """;
        File.WriteAllText(Path_, v3);

        var logger = new RecordingLogger();
        var store = new UserRefinementStore(_dir, logger);

        store.TryGet("Map_AreaSerbule", CalibrationFrame.Texture, out var cal).Should().BeTrue();
        cal.Scale.Should().BeApproximately(0.82, 1e-9);

        logger.Infos.Should().Contain(msg =>
            msg.Contains("Map_AreaSerbule", StringComparison.Ordinal) &&
            msg.Contains("texture", StringComparison.OrdinalIgnoreCase) &&
            msg.Contains("calibrationZoom", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------------------
    // 5. Clean record (no calibrationZoom): no Info log emitted
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_RecordWithoutCalibrationZoom_DoesNotLog()
    {
        const string clean = """
            {
              "schemaVersion": 3,
              "calibrations": {
                "Map_AreaEltibule": {
                  "texture": {
                    "scale": 0.76, "rotationRadians": 0.0, "originX": 50.0, "originY": 60.0,
                    "referenceCount": 5, "residualPixels": 0.65,
                    "source": "AutoCapture", "schemaVersion": 3,
                    "mirrorNorth": false, "frame": "Texture",
                    "pixelSha256": "aabbcc"
                  }
                }
              }
            }
            """;
        File.WriteAllText(Path_, clean);

        var logger = new RecordingLogger();
        _ = new UserRefinementStore(_dir, logger);

        logger.Infos.Should().NotContain(msg =>
            msg.Contains("calibrationZoom", StringComparison.OrdinalIgnoreCase),
            because: "clean records carry no legacy field and must not spam the log");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Infos { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information)
                Infos.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
