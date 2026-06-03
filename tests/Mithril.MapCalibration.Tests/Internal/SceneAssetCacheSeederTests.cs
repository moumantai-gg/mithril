using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Internal;

public sealed class SceneAssetCacheSeederTests : IDisposable
{
    private readonly string _tempDir;

    public SceneAssetCacheSeederTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mithril-seeder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* */ }
    }

    [Fact]
    public void Seed_PopulatesIntersectionOfBaselineAndAreas()
    {
        var store = new SceneAssetCacheStore(_tempDir, NullLogger.Instance);
        var baseline = new Dictionary<string, AreaCalibration>(StringComparer.Ordinal)
        {
            ["Map_AreaSerbule"] = MakeCal(),
            ["Map_AreaEltibule"] = MakeCal(),
            ["Map_AreaKurMountains"] = MakeCal(),
            ["Map_HogansKeepBasement"] = MakeCal(), // no matching AreaX in areaKeys
        };
        var areaKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "AreaSerbule", "AreaEltibule", "AreaKurMountains",
            "AreaCave1", // no matching Map_AreaCave1 in baseline
        };

        SceneAssetCacheSeeder.Seed(store, baseline, areaKeys, NullLogger.Instance);

        store.TryGet("AreaSerbule", null, out var serbule).Should().BeTrue();
        serbule.MapAssetKey.Should().Be("Map_AreaSerbule");
        store.TryGet("AreaEltibule", null, out _).Should().BeTrue();
        store.TryGet("AreaKurMountains", null, out _).Should().BeTrue();

        // No spurious seeds.
        store.TryGet("AreaCave1", null, out _).Should().BeFalse();
        store.TryGet("HogansKeepBasement", null, out _).Should().BeFalse();
    }

    [Fact]
    public void Seed_DoesNotOverwriteEntriesFromObservation()
    {
        var store = new SceneAssetCacheStore(_tempDir, NullLogger.Instance);
        // Simulate a prior live observation that already populated the cache.
        store.Record("AreaSerbule", null, "Map_AreaSerbuleObservedFromLive", DateTimeOffset.UtcNow);

        var baseline = new Dictionary<string, AreaCalibration>(StringComparer.Ordinal)
        {
            ["Map_AreaSerbule"] = MakeCal(),
        };
        var areaKeys = new HashSet<string>(StringComparer.Ordinal) { "AreaSerbule" };

        SceneAssetCacheSeeder.Seed(store, baseline, areaKeys, NullLogger.Instance);

        store.TryGet("AreaSerbule", null, out var serbule).Should().BeTrue();
        // Observation wins — seed entry uses LastObservedAt = MinValue, observation has now.
        serbule.MapAssetKey.Should().Be("Map_AreaSerbuleObservedFromLive");
    }

    [Fact]
    public void Seed_SkipsBaselineEntriesWithoutMapPrefix()
    {
        var store = new SceneAssetCacheStore(_tempDir, NullLogger.Instance);
        var baseline = new Dictionary<string, AreaCalibration>(StringComparer.Ordinal)
        {
            // Hypothetical malformed baseline key — must not be seeded.
            ["AreaSerbule"] = MakeCal(),
        };
        var areaKeys = new HashSet<string>(StringComparer.Ordinal) { "AreaSerbule" };

        SceneAssetCacheSeeder.Seed(store, baseline, areaKeys, NullLogger.Instance);

        store.TryGet("AreaSerbule", null, out _).Should().BeFalse();
    }

    private static AreaCalibration MakeCal() => new(
        Scale: 1.0, RotationRadians: 0.0, OriginX: 0.0, OriginY: 0.0,
        ReferenceCount: 0, ResidualPixels: 0.0);
}
