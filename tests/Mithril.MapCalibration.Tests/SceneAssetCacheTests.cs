using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public sealed class SceneAssetCacheTests : IDisposable
{
    private readonly string _tempDir;

    public SceneAssetCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mithril-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private SceneAssetCache Build() =>
        new(new SceneAssetCacheStore(_tempDir, NullLogger.Instance), NullLogger.Instance);

    [Fact]
    public void Record_Then_Resolve_RoundtripsTheMapSceneRef()
    {
        var cache = Build();
        var scene = new MapSceneRef("AreaCave1", "Hogan's Basement", "Map_HogansKeepBasement");
        cache.Record(scene, DateTimeOffset.UtcNow);

        var resolved = cache.TryResolve("AreaCave1", "Hogan's Basement");

        resolved.Should().NotBeNull();
        resolved!.Value.Should().Be(scene);
    }

    [Fact]
    public void Record_OverwritesExisting_LiveWinsOverSeeded()
    {
        var cache = Build();
        var stale = new MapSceneRef("AreaSerbule", null, "Map_AreaSerbuleOld");
        var fresh = new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule");
        cache.Record(stale, DateTimeOffset.UtcNow.AddMinutes(-5));
        cache.Record(fresh, DateTimeOffset.UtcNow);

        var resolved = cache.TryResolve("AreaSerbule", null);

        resolved!.Value.MapAssetKey.Should().Be("Map_AreaSerbule");
    }

    [Fact]
    public void TryResolve_WithNonNullFriendly_DoesNotMatchEntryStoredWithNullFriendly()
    {
        var cache = Build();
        cache.Record(new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule"), DateTimeOffset.UtcNow);

        var resolved = cache.TryResolve("AreaSerbule", "Some Sub-Zone");

        resolved.Should().BeNull(); // composite-key strictness
    }

    [Fact]
    public void TryResolve_WithNullFriendly_DoesNotMatchEntryStoredWithNonNullFriendly()
    {
        var cache = Build();
        cache.Record(new MapSceneRef("AreaCave1", "Hogan's Basement", "Map_HogansKeepBasement"), DateTimeOffset.UtcNow);

        var resolved = cache.TryResolve("AreaCave1", null);

        resolved.Should().BeNull();
    }

    [Fact]
    public void TryResolve_EmptyParentArea_ReturnsNull()
    {
        var cache = Build();
        cache.TryResolve(string.Empty, null).Should().BeNull();
    }

    [Fact]
    public void Record_EmptyParentAreaOrAssetKey_SilentNoOp()
    {
        var cache = Build();
        cache.Record(new MapSceneRef(string.Empty, null, "Map_X"), DateTimeOffset.UtcNow);
        cache.Record(new MapSceneRef("AreaX", null, string.Empty), DateTimeOffset.UtcNow);
        // Neither should poison the cache.
        cache.TryResolve("AreaX", null).Should().BeNull();
    }
}
