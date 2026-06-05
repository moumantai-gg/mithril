using System.IO;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;
using Mithril.MapCalibration;
using Mithril.MapCalibration.DependencyInjection;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Misc;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Npc = Mithril.Reference.Models.Npcs.Npc;
using Quest = Mithril.Reference.Models.Quests.Quest;

namespace Legolas.Tests.Services;

public class AreaCalibrationServiceTests
{
    private static (AreaCalibrationService svc, FakeProjector proj, IMapCalibrationService mapCal)
        Build(FakeRefData refData)
    {
        var proj = new FakeProjector();
        var mapCalDir = Path.Combine(Path.GetTempPath(), "mithril-mapcal-tests", Guid.NewGuid().ToString("N"));
        var mapCal = MapCalibrationServiceCollectionExtensions.Build(mapCalDir);
        var svc = new AreaCalibrationService(refData, proj, mapCal);
        return (svc, proj, mapCal);
    }

    private static MapSceneRef SceneFor(string areaKey) =>
        AreaCalibrationService.MapSceneRefForDirectlyRegisteredArea(areaKey);

    /// <summary>
    /// Seed a persisted calibration via the shared <see cref="IMapCalibrationService"/>.
    /// mithril#1041 (D6) retired the legacy <c>LegolasSettings.AreaCalibrations</c>
    /// dual-write — the shared service is the sole calibration store, so the
    /// seed-helper writes only there.
    /// </summary>
    private static void Seed(IMapCalibrationService mapCal, string areaKey, AreaCalibration cal)
    {
        mapCal.SaveUserRefinement(SceneFor(areaKey), cal);
    }

    [Fact]
    public void Unknown_area_key_records_key_as_friendly_name_with_no_refs()
    {
        // SelectScene with a key that isn't in the gazetteer falls back to using
        // the key as the friendly name verbatim — the area-picker bypass path
        // (PlayerAreaTracker keys are always in-game-real, but a test/dev key
        // shouldn't crash the consumer).
        var (svc, _, _) = Build(new FakeRefData());

        svc.SelectScene(SceneFor("AreaNowheresville"));

        svc.CurrentScene?.ParentAreaKey.Should().Be("AreaNowheresville");
        svc.CurrentAreaFriendlyName.Should().Be("AreaNowheresville");
        svc.CurrentAreaReferences.Should().BeEmpty();
        svc.IsCurrentAreaCalibrated.Should().BeFalse();
    }

    [Fact]
    public void Entering_a_calibrated_area_applies_the_persisted_calibration_to_the_projector()
    {
        var refData = new FakeRefData
        {
            AreasByKey = { ["AreaEltibule"] = new AreaEntry("AreaEltibule", "Eltibule", "") },
        };
        var (svc, proj, mapCal) = Build(refData);
        // residual (0.3) must beat the bundled Eltibule baseline (0.65 px); refCount ≥ 4.
        var persisted = new AreaCalibration(3.0, 0.5, 11, 22, 5, 0.3);
        Seed(mapCal, "AreaEltibule", persisted);

        // PlayerLogIngestionService bridges Arda MapAssetChanged → SelectScene
        // with a typed MapSceneRef. Mirror the directly-registered area shape.
        svc.SelectScene(SceneFor("AreaEltibule"));

        svc.IsCurrentAreaCalibrated.Should().BeTrue();
        svc.CurrentCalibration.Should().Be(persisted);
        proj.LastApplied.Should().Be(persisted);
    }

    [Fact]
    public void Entering_an_uncalibrated_area_builds_references_and_does_not_touch_projector()
    {
        // Use an area with NO bundled baseline (Serbule/Eltibule/KurMountains now
        // ship gate-study baselines, #916) so "uncalibrated" is genuinely true:
        // otherwise SelectScene applies the baseline fallback and LastApplied is set.
        const string area = "AreaTestVille";
        var refData = new FakeRefData
        {
            AreasByKey = { [area] = new AreaEntry(area, "Testville", "") },
            NpcsByKey =
            {
                ["NPC_Marn"] = new Npc { Name = "Marn", AreaName = area, Pos = "x:10 y:0 z:20" },
                ["NPC_NoPos"] = new Npc { Name = "Ghost", AreaName = area, Pos = null },
                ["NPC_Other"] = new Npc { Name = "Far", AreaName = "AreaSerbule", Pos = "x:1 y:0 z:1" },
            },
            LandmarksByArea =
            {
                [area] = new List<Landmark>
                {
                    new() { Name = "Teleport Circle", Type = "TeleportationPlatform", Loc = "x:5 y:1 z:6" },
                    new() { Name = "Broken", Type = "Portal", Loc = "not-a-loc" },
                },
            },
        };
        var (svc, proj, _) = Build(refData);

        svc.SelectScene(SceneFor(area));

        proj.LastApplied.Should().BeNull(); // no persisted calibration → projector untouched
        svc.CurrentAreaReferences.Select(r => r.Name)
            .Should().BeEquivalentTo(new[] { "Marn", "Teleport Circle" });
        svc.CurrentAreaReferences.Should().ContainSingle(r => r.Kind == "NPC");
        svc.CurrentAreaReferences.Should().ContainSingle(r => r.Kind == "TeleportationPlatform");
    }

    [Fact]
    public void CalibrateCurrentArea_solves_persists_applies_and_raises_changed()
    {
        var refData = new FakeRefData
        {
            AreasByKey = { ["AreaEltibule"] = new AreaEntry("AreaEltibule", "Eltibule", "") },
        };
        var (svc, proj, mapCal) = Build(refData);
        var scene = SceneFor("AreaEltibule");
        svc.SelectScene(scene);

        var changed = 0;
        svc.Changed += (_, _) => changed++;

        // Identity-ish transform: pixel == world ground plane (scale 1, rot 0).
        // Four placements so ReferenceCount ≥ MinReferences (4) and the solved
        // calibration lands in the eligible set under the residual-ordered picker.
        var placements = new (WorldCoord, PixelPoint)[]
        {
            (new WorldCoord(0, 0, 0), new PixelPoint(0, 0)),
            (new WorldCoord(100, 0, 0), new PixelPoint(100, 0)),
            (new WorldCoord(0, 0, 100), new PixelPoint(0, -100)), // north → up
            (new WorldCoord(50, 0, 50), new PixelPoint(50, -50)),
        };

        var cal = svc.CalibrateCurrentArea(placements, calibrationZoom: 0.39);

        cal.Should().NotBeNull();
        cal!.Scale.Should().BeApproximately(1.0, 1e-6);
        cal.CalibrationZoom.Should().BeApproximately(0.39, 1e-9); // stamped + persisted
        // mithril#1041 D6: calibrations land in the shared service only.
        mapCal.GetCalibration(scene).Should().Be(cal);
        proj.LastApplied.Should().Be(cal);
        changed.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void CalibrateCurrentArea_returns_null_with_fewer_than_two_placements_or_no_area()
    {
        // Use a baseline-free area (#916: Eltibule now ships a bundled baseline,
        // so mapCal.GetCalibration would return the baseline transform even
        // after we abort the solve — masking the "nothing was persisted" intent).
        const string area = "AreaTestVille";
        var refData = new FakeRefData
        {
            AreasByKey = { [area] = new AreaEntry(area, "Testville", "") },
        };
        var (svc, _, mapCal) = Build(refData);
        var scene = SceneFor(area);

        // No current area yet.
        svc.CalibrateCurrentArea(new (WorldCoord, PixelPoint)[]
        {
            (new WorldCoord(0, 0, 0), new PixelPoint(0, 0)),
            (new WorldCoord(1, 0, 1), new PixelPoint(1, 1)),
        }).Should().BeNull();

        svc.SelectScene(scene);
        svc.CalibrateCurrentArea(new (WorldCoord, PixelPoint)[]
        {
            (new WorldCoord(0, 0, 0), new PixelPoint(0, 0)),
        }).Should().BeNull();

        mapCal.GetCalibration(scene).Should().BeNull();
    }

    [Fact]
    public void SelectScene_sets_current_area_builds_refs_and_applies_persisted()
    {
        var refData = new FakeRefData
        {
            AreasByKey = { ["AreaEltibule"] = new AreaEntry("AreaEltibule", "Eltibule", "") },
            NpcsByKey = { ["NPC_Marn"] = new Npc { Name = "Marn", AreaName = "AreaEltibule", Pos = "x:1 y:0 z:2" } },
        };
        var (svc, proj, mapCal) = Build(refData);
        // residual (0.3) must beat the bundled Eltibule baseline (0.65 px); refCount ≥ 4.
        var persisted = new AreaCalibration(2, 0.1, 5, 6, 5, 0.3);
        Seed(mapCal, "AreaEltibule", persisted);

        svc.SelectScene(SceneFor("AreaEltibule"));

        svc.CurrentScene?.ParentAreaKey.Should().Be("AreaEltibule");
        svc.CurrentAreaFriendlyName.Should().Be("Eltibule");
        svc.CurrentAreaReferences.Should().ContainSingle(r => r.Name == "Marn");
        proj.LastApplied.Should().Be(persisted);
    }

    [Fact]
    public void AllAreas_lists_every_area_sorted_by_friendly_name()
    {
        var refData = new FakeRefData
        {
            AreasByKey =
            {
                ["AreaServbule"] = new AreaEntry("AreaServbule", "Serbule", ""),
                ["AreaEltibule"] = new AreaEntry("AreaEltibule", "Eltibule", ""),
                ["AreaAnagoge"] = new AreaEntry("AreaAnagoge", "Anagoge Island", ""),
            },
        };
        var (svc, _, _) = Build(refData);

        svc.AllAreas.Select(a => a.FriendlyName)
            .Should().ContainInOrder("Anagoge Island", "Eltibule", "Serbule");
    }

    [Fact]
    public void NoteSurvey_reraises_as_SurveyObserved()
    {
        var (svc, _, _) = Build(new FakeRefData());
        CalibrationSurveyObservation? seen = null;
        svc.SurveyObserved += (_, o) => seen = o;

        svc.NoteSurvey("Iron Vein", new MetreOffset(12, -7));

        seen.Should().NotBeNull();
        seen!.Name.Should().Be("Iron Vein");
        seen.Offset.Should().Be(new MetreOffset(12, -7));
    }

    [Fact]
    public void ClearCurrentAreaCalibration_removes_and_raises_changed()
    {
        // Use a baseline-free area (#916: Eltibule now ships a bundled baseline,
        // so after clearing the user refinement the service would fall back to
        // the baseline and IsCurrentAreaCalibrated would stay true — correct
        // production behavior, but it masks the clear semantics this test pins).
        const string area = "AreaTestVille";
        var refData = new FakeRefData
        {
            AreasByKey = { [area] = new AreaEntry(area, "Testville", "") },
        };
        var (svc, _, mapCal) = Build(refData);
        var scene = SceneFor(area);
        Seed(mapCal, area, new AreaCalibration(1, 0, 0, 0, 2, 0));
        svc.SelectScene(scene);
        svc.IsCurrentAreaCalibrated.Should().BeTrue();

        var changed = 0;
        svc.Changed += (_, _) => changed++;

        svc.ClearCurrentAreaCalibration();

        mapCal.GetCalibration(scene).Should().BeNull();
        svc.IsCurrentAreaCalibrated.Should().BeFalse();
        changed.Should().Be(1);
    }

    [Fact]
    public void Calibrate_throw_from_shared_service_propagates_without_leaving_state()
    {
        // mithril#1041 D6 retired the legacy LegolasSettings.AreaCalibrations
        // dual-write. The remaining contract is: when IMapCalibrationService.
        // SaveUserRefinement throws, the exception propagates and no persisted
        // calibration is left behind in the (now sole) shared store.
        var refData = new FakeRefData
        {
            AreasByKey = { ["AreaEltibule"] = new AreaEntry("AreaEltibule", "Eltibule", "") },
        };
        var proj = new FakeProjector();
        var throwingMapCal = new ThrowingMapCalibrationService();
        var svc = new AreaCalibrationService(refData, proj, throwingMapCal);
        svc.SelectScene(SceneFor("AreaEltibule"));

        var placements = new[]
        {
            (new WorldCoord(0, 0, 0), new PixelPoint(0, 0)),
            (new WorldCoord(100, 0, 0), new PixelPoint(100, 0)),
            (new WorldCoord(0, 0, 100), new PixelPoint(0, -100)),
        };

        FluentActions.Invoking(() => svc.CalibrateCurrentArea(placements))
            .Should().Throw<System.IO.IOException>();

        throwingMapCal.SavesAttempted.Should().Be(1,
            "the service must reach SaveUserRefinement before failing — that's the " +
            "single write site post-#1041.");
    }

    [Fact]
    public void Clear_throw_from_shared_service_propagates_without_leaving_state()
    {
        // mithril#1041 D6 same invariant for ClearCurrentAreaCalibration.
        var refData = new FakeRefData
        {
            AreasByKey = { ["AreaEltibule"] = new AreaEntry("AreaEltibule", "Eltibule", "") },
        };
        var proj = new FakeProjector();
        var throwingMapCal = new ThrowingMapCalibrationService();
        var svc = new AreaCalibrationService(refData, proj, throwingMapCal);
        svc.SelectScene(SceneFor("AreaEltibule"));

        FluentActions.Invoking(() => svc.ClearCurrentAreaCalibration())
            .Should().Throw<System.IO.IOException>();

        throwingMapCal.ClearsAttempted.Should().Be(1);
    }

    [Fact]
    public void OnMapCalChanged_for_current_scene_asset_key_reapplies_projector_and_raises_Changed()
    {
        // mithril#1041 regression pin: the equality fix at AreaCalibrationService.OnMapCalChanged
        // compares payload.MapAssetKey == _currentScene.MapAssetKey. Pre-#1041 the comparison was
        // (areaKey vs CurrentAreaKey), so the engine-emitted Map_<X> never matched the bare
        // areas.json key and every change event dropped. This test fires Changed with a matching
        // MapSceneRef and asserts both the projector re-applies and the service's own Changed
        // event re-broadcasts.
        var refData = new FakeRefData
        {
            AreasByKey = { ["AreaEltibule"] = new AreaEntry("AreaEltibule", "Eltibule", "") },
        };
        var (svc, proj, mapCal) = Build(refData);
        var scene = SceneFor("AreaEltibule");

        // Seed via the shared service first so SelectScene's initial ApplyCalibration runs.
        // refCount ≥ 4 and residual (0.3) beats the bundled Eltibule baseline (0.65 px)
        // so the user refinement lands in the eligible set and wins the picker.
        var initial = new AreaCalibration(
            Scale: 1.0, RotationRadians: 0, OriginX: 10, OriginY: 20,
            ReferenceCount: 5, ResidualPixels: 0.3);
        mapCal.SaveUserRefinement(scene, initial);

        svc.SelectScene(scene);
        proj.LastApplied.Should().NotBeNull();
        proj.LastApplied!.OriginX.Should().Be(10);

        // Now mutate the calibration via the shared service. The Changed event fires
        // with payload.MapAssetKey == scene.MapAssetKey ("Map_AreaEltibule"), which the
        // equality fix in OnMapCalChanged should now recognise.
        var changedFired = 0;
        svc.Changed += (_, _) => changedFired++;
        var updated = initial with { OriginX = 99, OriginY = 88 };
        mapCal.SaveUserRefinement(scene, updated);

        proj.LastApplied.Should().NotBeNull();
        proj.LastApplied!.OriginX.Should().Be(99, "OnMapCalChanged must re-apply the new calibration to the projector");
        proj.LastApplied!.OriginY.Should().Be(88);
        changedFired.Should().BeGreaterThan(0, "OnMapCalChanged must re-broadcast IAreaCalibrationService.Changed for UI subscribers");
    }

    // ---- fakes ------------------------------------------------------------

    /// <summary>
    /// IMapCalibrationService that throws IOException on every write — used to
    /// verify AreaCalibrationService's propagation of shared-service failures.
    /// Reads return null (uncalibrated) which is fine for the ordering tests.
    /// </summary>
    private sealed class ThrowingMapCalibrationService : IMapCalibrationService
    {
        public int SavesAttempted { get; private set; }
        public int ClearsAttempted { get; private set; }

        public event EventHandler<MapSceneRef>? Changed { add { } remove { } }
        public bool IsCalibrated(MapSceneRef scene) => false;
        public AreaCalibration? GetCalibration(MapSceneRef scene) => null;
        public PixelPoint? WorldToWindow(MapSceneRef scene, WorldCoord world, double currentZoom) => null;
        public WorldCoord? WindowToWorld(MapSceneRef scene, PixelPoint pixel, double currentZoom) => null;
        public TexturePixel? WorldToTexture(MapSceneRef scene, WorldCoord world, double currentZoom) => null;
        public WorldCoord? TextureToWorld(MapSceneRef scene, TexturePixel pixel, double currentZoom) => null;
        public OverlayPixel? WorldToOverlay(MapSceneRef scene, WorldCoord world, double currentZoom) => null;
        public WorldCoord? OverlayToWorld(MapSceneRef scene, OverlayPixel pixel, double currentZoom) => null;
        public WorldToTextureCalibration? GetTextureCalibration(MapSceneRef scene) => null;
        public IReadOnlyDictionary<string, AreaCalibration> AllCalibrations { get; } =
            new Dictionary<string, AreaCalibration>(StringComparer.Ordinal);
        public IReadOnlyList<AreaCalibration> GetAllSources(MapSceneRef scene) => Array.Empty<AreaCalibration>();
        public void SaveUserRefinement(MapSceneRef scene, AreaCalibration calibration)
        {
            SavesAttempted++;
            throw new System.IO.IOException("simulated disk failure");
        }
        public void ClearUserRefinement(MapSceneRef scene)
        {
            ClearsAttempted++;
            throw new System.IO.IOException("simulated disk failure");
        }
    }

    private sealed class FakeProjector : ICoordinateProjector
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

    private sealed class FakeRefData : IReferenceDataService
    {
        public Dictionary<string, AreaEntry> AreasByKey { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Npc> NpcsByKey { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<Landmark>> LandmarksByArea { get; } = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, AreaEntry> Areas => AreasByKey;
        public IReadOnlyDictionary<string, Npc> NpcsByInternalName => NpcsByKey;
        public IReadOnlyDictionary<string, IReadOnlyList<Landmark>> Landmarks =>
            LandmarksByArea.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Landmark>)kv.Value, StringComparer.Ordinal);

        // Required (non-default) members — empty, mirrors the established Legolas
        // test stub pattern (LegolasReportServiceTests.StubRefData).
        public IReadOnlyList<string> Keys { get; } = [];
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
        public event EventHandler<string>? FileUpdated;
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        private void Suppress() => FileUpdated?.Invoke(this, "");
    }
}
