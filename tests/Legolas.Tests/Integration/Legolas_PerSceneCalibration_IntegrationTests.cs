using System.IO;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Internal;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Misc;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Xunit;
using Npc = Mithril.Reference.Models.Npcs.Npc;
using Quest = Mithril.Reference.Models.Quests.Quest;

namespace Legolas.Tests.Integration;

/// <summary>Headline regression-fix proof per mithril#1041 spec §5.8.
/// Verifies the three states of the resolution cascade against real
/// AreaCalibrationService + MapCalibrationService + SceneAssetCache wiring
/// using the actual bundled baseline.
///
/// <para>The test variants mirror the manual smoke-test cells from spec §8:
/// live truth, cache fallback (cold-start for a directly-registered area
/// without a <c>Downloading Map</c> observation this session), and the
/// strict gate for an unrecognised area.</para>
/// </summary>
public sealed class Legolas_PerSceneCalibration_IntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public Legolas_PerSceneCalibration_IntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mithril-headline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void LiveTruth_AreaSerbule_RendersAgainstBaseline()
    {
        using var harness = Harness.Build(_tempDir);
        var scene = new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule");

        // SelectScene drives the AreaCalibrationService the way PlayerLogIngestionService
        // would on a live MapAssetChanged event.
        harness.AreaCalibration.SelectScene(scene);

        harness.AreaCalibration.CurrentScene.Should().NotBeNull();
        harness.AreaCalibration.IsCurrentAreaCalibrated.Should().BeTrue(
            "AreaSerbule has a bundled baseline calibration that the live MapSceneRef resolves to");
        harness.AreaCalibration.CurrentCalibration.Should().NotBeNull();
    }

    [Fact]
    public void CacheFallback_AreaSerbule_RendersAgainstBaseline()
    {
        using var harness = Harness.Build(_tempDir);
        // Simulate cold-start: no live CurrentMapScene observation yet.
        harness.MapState.CurrentArea = "AreaSerbule";
        harness.MapState.CurrentMapScene = null;

        // The cache is pre-seeded by Harness.Build via SceneAssetCacheSeeder
        // (baseline ∩ areaKeys). Resolution falls through to the cache and
        // synthesises a MapSceneRef for the calibration lookup.
        var resolved = SceneResolution.ResolveCurrentScene(harness.MapState, harness.SceneAssetCache);
        resolved.Should().NotBeNull(
            "the cache seeder populated AreaSerbule from baseline ∩ areas.json");
        harness.AreaCalibration.SelectScene(resolved!.Value);

        harness.AreaCalibration.IsCurrentAreaCalibrated.Should().BeTrue();
        harness.AreaCalibration.CurrentCalibration.Should().NotBeNull();
    }

    [Fact]
    public void StrictGate_UnknownArea_ReturnsNull()
    {
        using var harness = Harness.Build(_tempDir);
        harness.MapState.CurrentArea = "AreaUnknownNeverSeen";
        harness.MapState.CurrentMapScene = null;

        var resolved = SceneResolution.ResolveCurrentScene(harness.MapState, harness.SceneAssetCache);
        resolved.Should().BeNull(
            "the strict gate refuses unrecognised areas — neither the cache nor live IMapState supplies a scene");

        // AreaCalibrationService.SelectScene was not called → CurrentScene stays null.
        harness.AreaCalibration.CurrentScene.Should().BeNull();
        harness.AreaCalibration.IsCurrentAreaCalibrated.Should().BeFalse();
    }

    private sealed class Harness : IDisposable
    {
        public FakeMapState MapState { get; init; } = null!;
        public ISceneAssetCache SceneAssetCache { get; init; } = null!;
        public IMapCalibrationService MapCalibration { get; init; } = null!;
        public IAreaCalibrationService AreaCalibration { get; init; } = null!;

        public static Harness Build(string tempDir)
        {
            // Real BundledBaselineLoader + UserRefinementStore (clean dir → no
            // pre-existing refinements). The baseline ships with AreaSerbule
            // anchored.
            var baseline = BundledBaselineLoader.Load(NullLogger.Instance);
            var userStore = new UserRefinementStore(directory: tempDir, logger: NullLogger.Instance);
            var mapCal = new MapCalibrationService(baseline, userStore, NullLogger.Instance);

            // Real SceneAssetCacheStore + SceneAssetCache. Seeded against a
            // minimal areaKeys set covering the three directly-registered areas
            // with bundled baselines.
            var cacheStore = new SceneAssetCacheStore(directory: tempDir, logger: NullLogger.Instance);
            var sceneCache = new SceneAssetCache(cacheStore, NullLogger.Instance);
            var areaKeys = new HashSet<string>(StringComparer.Ordinal)
            {
                "AreaSerbule", "AreaEltibule", "AreaKurMountains",
            };
            SceneAssetCacheSeeder.Seed(cacheStore, baseline, areaKeys, NullLogger.Instance);

            // Real AreaCalibrationService against a minimal IReferenceDataService stub.
            var refData = new MinimalRefData(areaKeys);
            var projector = new MinimalProjector();
            var areaCal = new AreaCalibrationService(refData, projector, mapCal);

            return new Harness
            {
                MapState = new FakeMapState(),
                SceneAssetCache = sceneCache,
                MapCalibration = mapCal,
                AreaCalibration = areaCal,
            };
        }

        public void Dispose() { /* tempDir cleaned by outer class */ }
    }

    /// <summary>Mutable <see cref="Arda.World.Player.IMapState"/> stub so tests
    /// can drive CurrentArea + CurrentMapScene independently. Other properties
    /// are inert defaults (not consumed by the resolution cascade).</summary>
    internal sealed class FakeMapState : Arda.World.Player.IMapState
    {
        public string? CurrentArea { get; set; }
        public string? PreviousArea { get; set; }
        public DateTimeOffset? TransitionedAt { get; set; }
        public MapSceneRef? CurrentMapScene { get; set; }
        public DateTimeOffset? MapSceneMeasuredAt { get; set; }
        public double? X => null;
        public double? Y => null;
        public double? Z => null;
        public DateTimeOffset? PositionMeasuredAt => null;
        public Arda.World.Player.Events.PositionSource? PositionSource => null;
        public string? CurrentWeather => null;
        public DateTimeOffset? WeatherMeasuredAt => null;
        public IReadOnlyList<Arda.World.Player.MapPinEntry> Pins =>
            Array.Empty<Arda.World.Player.MapPinEntry>();
    }

    /// <summary>Minimal IReferenceDataService stub — mirrors the established
    /// Legolas test pattern (Services/AreaCalibrationServiceTests.FakeRefData).
    /// Only Areas is consumed by the paths under test; everything else is
    /// inert.</summary>
    internal sealed class MinimalRefData : IReferenceDataService
    {
        public MinimalRefData(IReadOnlySet<string> areaKeys)
        {
            Areas = areaKeys.ToDictionary(
                k => k,
                k => new AreaEntry(Key: k, FriendlyName: k.Replace("Area", ""), ShortFriendlyName: k.Replace("Area", "")),
                StringComparer.Ordinal);
        }

        public IReadOnlyDictionary<string, AreaEntry> Areas { get; }
        public IReadOnlyDictionary<string, Npc> NpcsByInternalName { get; } = new Dictionary<string, Npc>();
        public IReadOnlyDictionary<string, IReadOnlyList<Landmark>> Landmarks { get; }
            = new Dictionary<string, IReadOnlyList<Landmark>>();

        public IReadOnlyList<string> Keys { get; } = Array.Empty<string>();
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>();
        public ItemKeywordIndex KeywordIndex => ItemKeywordIndex.Empty;
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Quest> Quests { get; } = new Dictionary<string, Quest>();
        public IReadOnlyDictionary<string, Quest> QuestsByInternalName { get; } = new Dictionary<string, Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public event EventHandler<string>? FileUpdated { add { } remove { } }
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
    }

    /// <summary>No-op coordinate projector — the integration test asserts on
    /// AreaCalibrationService observable behaviour, not on projection output.</summary>
    internal sealed class MinimalProjector : ICoordinateProjector
    {
        public AreaCalibration? LastApplied { get; private set; }
        public double Scale => 1;
        public double RotationRadians => 0;
        public PixelPoint Origin => PixelPoint.Zero;
        public PixelPoint Project(MetreOffset offset) => PixelPoint.Zero;
        public void SetOrigin(PixelPoint origin) { }
        public void CalibrateFromClick(PixelPoint playerPixel, PixelPoint click, MetreOffset offset) { }
        public void Refit(IReadOnlyList<(MetreOffset Offset, PixelPoint Pixel)> corrections) { }
        public void ApplyCalibration(AreaCalibration calibration) => LastApplied = calibration;
    }
}
