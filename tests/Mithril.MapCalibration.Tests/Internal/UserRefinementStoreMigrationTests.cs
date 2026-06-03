using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Internal;

/// <summary>
/// Task 18 (#1021): <see cref="UserRefinementStore"/> load-time v1&#8594;v2 prefix
/// migrator. A v1 file (absent top-level <c>schemaVersion</c> field, bare area
/// keys like <c>AreaSerbule</c>) loads as <c>Map_</c>-prefixed in memory and is
/// rewritten with <c>schemaVersion: 2</c>. A v2 file is byte-untouched on disk.
/// A v1 file whose key is already <c>Map_</c>-prefixed (defensive) is not
/// double-prefixed. A missing file is a no-op.
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
    public void Load_V1File_PrefixesKeysWithMapAndPersistsAsV2()
    {
        File.WriteAllText(Path_, V1Json);

        var store = new UserRefinementStore(_dir);

        store.TryGet("Map_AreaSerbule", out var cal).Should().BeTrue();
        cal.Scale.Should().BeApproximately(0.82, 1e-9);

        // File rewritten with schemaVersion 2, Map_-prefixed key.
        using var doc = JsonDocument.Parse(File.ReadAllText(Path_));
        doc.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("calibrations").EnumerateObject()
            .Select(p => p.Name).Should().ContainSingle().Which.Should().Be("Map_AreaSerbule");
    }

    [Fact]
    public void Load_V2File_NoMutation()
    {
        const string v2 = """
            {
              "schemaVersion": 2,
              "calibrations": {
                "Map_AreaSerbule": {
                  "scale": 0.82, "rotationRadians": 0.0, "originX": 100.0, "originY": 200.0,
                  "referenceCount": 4, "residualPixels": 0.5,
                  "source": "UserRefinement", "schemaVersion": 1, "calibrationZoom": 1.0, "mirrorNorth": false
                }
              }
            }
            """;
        File.WriteAllText(Path_, v2);
        var before = File.ReadAllBytes(Path_);

        var store = new UserRefinementStore(_dir);
        store.TryGet("Map_AreaSerbule", out _).Should().BeTrue();

        // Idempotent: file unchanged byte-for-byte (no rewrite triggered).
        File.ReadAllBytes(Path_).Should().Equal(before);
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
        store.TryGet("Map_AreaSerbule", out _).Should().BeTrue();
        store.TryGet("Map_Map_AreaSerbule", out _).Should().BeFalse();
    }

    [Fact]
    public void Load_MissingFile_NoMigration_NoCrash()
    {
        var store = new UserRefinementStore(_dir);
        store.All.Should().BeEmpty();
    }
}
