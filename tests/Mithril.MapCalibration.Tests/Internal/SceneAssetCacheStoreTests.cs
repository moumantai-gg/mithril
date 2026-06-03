using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Internal;

public sealed class SceneAssetCacheStoreTests : IDisposable
{
    private readonly string _tempDir;

    public SceneAssetCacheStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mithril-cache-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* */ }
    }

    [Fact]
    public void Roundtrip_WriteThenReread_RestoresEntries()
    {
        var store = new SceneAssetCacheStore(_tempDir, NullLogger.Instance);
        store.Record("AreaSerbule", null, "Map_AreaSerbule", DateTimeOffset.UtcNow);
        store.Record("AreaCave1", "Hogan's Basement", "Map_HogansKeepBasement", DateTimeOffset.UtcNow);

        var reloaded = new SceneAssetCacheStore(_tempDir, NullLogger.Instance);
        reloaded.TryGet("AreaSerbule", null, out var serbule).Should().BeTrue();
        serbule.MapAssetKey.Should().Be("Map_AreaSerbule");
        reloaded.TryGet("AreaCave1", "Hogan's Basement", out var hogans).Should().BeTrue();
        hogans.MapAssetKey.Should().Be("Map_HogansKeepBasement");
    }

    [Fact]
    public void Load_MissingFile_StartsEmpty()
    {
        var store = new SceneAssetCacheStore(_tempDir, NullLogger.Instance);
        store.TryGet("AnyArea", null, out _).Should().BeFalse();
    }

    [Fact]
    public void Load_GarbageJson_StartsEmptyAndDoesNotThrow()
    {
        var filePath = Path.Combine(_tempDir, "scene-asset-cache.json");
        File.WriteAllText(filePath, "{ this is not valid json");

        var act = () => new SceneAssetCacheStore(_tempDir, NullLogger.Instance);
        act.Should().NotThrow();
    }

    [Fact]
    public void Load_PoisonedEntry_SkipsButLoadsOthers()
    {
        var filePath = Path.Combine(_tempDir, "scene-asset-cache.json");
        File.WriteAllText(filePath, """
            {
                "schemaVersion": 1,
                "entries": [
                    { "parentArea": "AreaSerbule", "sceneFriendlyName": null, "mapAssetKey": "Map_AreaSerbule", "lastObservedAt": "2026-06-03T20:01:17+00:00" },
                    { "parentArea": "Bad", "sceneFriendlyName": null, "lastObservedAt": "this isn't a date" },
                    { "parentArea": "AreaEltibule", "sceneFriendlyName": null, "mapAssetKey": "Map_AreaEltibule", "lastObservedAt": "2026-06-03T20:01:17+00:00" }
                ]
            }
            """);

        var store = new SceneAssetCacheStore(_tempDir, NullLogger.Instance);
        store.TryGet("AreaSerbule", null, out _).Should().BeTrue();
        store.TryGet("AreaEltibule", null, out _).Should().BeTrue();
        store.TryGet("Bad", null, out _).Should().BeFalse(); // missing mapAssetKey is a poisoned entry
    }

    [Fact]
    public void Record_RollsBack_InMemoryState_When_Persist_Throws()
    {
        var store = new SceneAssetCacheStore(_tempDir, NullLogger.Instance);
        store.Record("AreaSerbule", null, "Map_AreaSerbule", DateTimeOffset.UtcNow);

        var tmpPath = Path.Combine(_tempDir, "scene-asset-cache.json.tmp");
        using (var lockHandle = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            FluentActions.Invoking(() =>
                store.Record("AreaSerbule", null, "Map_AreaSerbuleOverwritten", DateTimeOffset.UtcNow))
                .Should().Throw<IOException>();
        }

        // Same-session read sees the original value, NOT the failed overwrite.
        store.TryGet("AreaSerbule", null, out var existing).Should().BeTrue();
        existing.MapAssetKey.Should().Be("Map_AreaSerbule");
    }
}
