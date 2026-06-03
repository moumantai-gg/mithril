using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public void Record_UnderDefinedComposite_EmitsTraceDiagnostic()
    {
        // mithril#1053: the Downloading-Map-before-Initializing-area race
        // (see MapAssetLoader) hands SceneAssetCache a composite with an
        // empty ParentAreaKey. The drop itself is correct (don't poison the
        // cache) but a support investigation can't tell whether THIS guard
        // fired vs. some other path failed silently. LogTrace surfaces it
        // in the diagnostics ring buffer without spamming Information.
        var capture = new CapturingLogger();
        var cache = new SceneAssetCache(new SceneAssetCacheStore(_tempDir, NullLogger.Instance), capture);

        cache.Record(new MapSceneRef(string.Empty, null, "Map_X"), DateTimeOffset.UtcNow);

        capture.Traces.Should().Contain(m =>
            m.Contains("dropped under-defined composite"));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Traces { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Trace) Traces.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
