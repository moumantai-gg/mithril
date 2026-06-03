using System.IO;
using FluentAssertions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// Stacked-source precedence: user-refinement &gt; community-sync (future) &gt;
/// bundled-baseline, with the residual threshold downgrading a bad user
/// refinement.
/// </summary>
public sealed class MapCalibrationServiceTests : IDisposable
{
    private readonly string _tempDir;

    public MapCalibrationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mithril-mapcal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* leave it; CI temp dir gets reaped */ }
    }

    private static MapSceneRef Scene(string asset) =>
        new(ParentAreaKey: string.Empty, SceneFriendlyName: null, MapAssetKey: asset);

    [Fact]
    public void Good_user_refinement_wins_over_baseline()
    {
        // New picker: lower residual wins when both candidates meet the ref-count floor.
        // User residual (2.0) < baseline residual (4.0) → user wins.
        var baseline = new Dictionary<string, AreaCalibration>
        {
            ["Map_AreaEltibule"] = MakeCal(residual: 4.0, scale: 1.0) with { Source = CalibrationSource.BundledBaseline },
        };
        var store = new UserRefinementStore(_tempDir);
        store.Save("Map_AreaEltibule", MakeCal(residual: 2.0, scale: 2.0));

        var svc = new MapCalibrationService(baseline, store, goodResidualThresholdPx: 12.0, logger: null);

        var active = svc.GetCalibration(Scene("Map_AreaEltibule"));
        active.Should().NotBeNull();
        active!.Source.Should().Be(CalibrationSource.UserRefinement);
        active.Scale.Should().Be(2.0);
    }

    [Fact]
    public void Bad_user_refinement_falls_through_to_baseline()
    {
        var baseline = new Dictionary<string, AreaCalibration>
        {
            ["Map_AreaEltibule"] = MakeCal(residual: 5.0, scale: 1.0) with { Source = CalibrationSource.BundledBaseline },
        };
        var store = new UserRefinementStore(_tempDir);
        // Above the threshold of 12 — the resolver should prefer the baseline.
        store.Save("Map_AreaEltibule", MakeCal(residual: 25.0, scale: 2.0));

        var svc = new MapCalibrationService(baseline, store, goodResidualThresholdPx: 12.0, logger: null);

        var active = svc.GetCalibration(Scene("Map_AreaEltibule"));
        active.Should().NotBeNull();
        active!.Source.Should().Be(CalibrationSource.BundledBaseline);
        active.Scale.Should().Be(1.0);
    }

    [Fact]
    public void Bad_user_refinement_with_no_baseline_still_returns_user()
    {
        var baseline = new Dictionary<string, AreaCalibration>(); // none
        var store = new UserRefinementStore(_tempDir);
        store.Save("Map_AreaEltibule", MakeCal(residual: 25.0, scale: 2.0));

        var svc = new MapCalibrationService(baseline, store, goodResidualThresholdPx: 12.0, logger: null);

        var active = svc.GetCalibration(Scene("Map_AreaEltibule"));
        active.Should().NotBeNull();
        active!.Source.Should().Be(CalibrationSource.UserRefinement);
    }

    [Fact]
    public void IsCalibrated_returns_false_when_no_source_exists()
    {
        var svc = new MapCalibrationService(
            new Dictionary<string, AreaCalibration>(),
            new UserRefinementStore(_tempDir),
            goodResidualThresholdPx: 12.0);

        svc.IsCalibrated(Scene("Map_AreaEltibule")).Should().BeFalse();
        svc.GetCalibration(Scene("Map_AreaEltibule")).Should().BeNull();
        svc.WorldToWindow(Scene("Map_AreaEltibule"), new WorldCoord(1, 0, 1), 1.0).Should().BeNull();
        svc.WindowToWorld(Scene("Map_AreaEltibule"), new PixelPoint(1, 1), 1.0).Should().BeNull();
    }

    [Fact]
    public void GetAllSources_returns_user_and_baseline_separately()
    {
        var baseline = new Dictionary<string, AreaCalibration>
        {
            ["Map_AreaEltibule"] = MakeCal(residual: 4.0, scale: 1.0) with { Source = CalibrationSource.BundledBaseline },
        };
        var store = new UserRefinementStore(_tempDir);
        store.Save("Map_AreaEltibule", MakeCal(residual: 8.0, scale: 2.0));

        var svc = new MapCalibrationService(baseline, store, goodResidualThresholdPx: 12.0);

        var sources = svc.GetAllSources(Scene("Map_AreaEltibule"));
        sources.Should().HaveCount(2);
        sources.Should().Contain(c => c.Source == CalibrationSource.UserRefinement && c.ResidualPixels == 8.0);
        sources.Should().Contain(c => c.Source == CalibrationSource.BundledBaseline && c.ResidualPixels == 4.0);
    }

    [Fact]
    public void SaveUserRefinement_persists_across_service_instances()
    {
        var svc1 = new MapCalibrationService(
            new Dictionary<string, AreaCalibration>(),
            new UserRefinementStore(_tempDir),
            goodResidualThresholdPx: 12.0);

        svc1.SaveUserRefinement(Scene("Map_AreaEltibule"), MakeCal(residual: 3.0, scale: 1.7));

        // New service instance reading from the same directory should see it.
        var svc2 = new MapCalibrationService(
            new Dictionary<string, AreaCalibration>(),
            new UserRefinementStore(_tempDir),
            goodResidualThresholdPx: 12.0);

        var loaded = svc2.GetCalibration(Scene("Map_AreaEltibule"));
        loaded.Should().NotBeNull();
        loaded!.Scale.Should().Be(1.7);
        loaded.Source.Should().Be(CalibrationSource.UserRefinement);
    }

    [Fact]
    public void ClearUserRefinement_returns_to_baseline()
    {
        // User residual (2.0) < baseline residual (4.0) → user wins before clear;
        // after clear, only baseline remains.
        var baseline = new Dictionary<string, AreaCalibration>
        {
            ["Map_AreaEltibule"] = MakeCal(residual: 4.0, scale: 1.0) with { Source = CalibrationSource.BundledBaseline },
        };
        var store = new UserRefinementStore(_tempDir);
        var svc = new MapCalibrationService(baseline, store, goodResidualThresholdPx: 12.0);

        svc.SaveUserRefinement(Scene("Map_AreaEltibule"), MakeCal(residual: 2.0, scale: 2.0));
        svc.GetCalibration(Scene("Map_AreaEltibule"))!.Source.Should().Be(CalibrationSource.UserRefinement);

        svc.ClearUserRefinement(Scene("Map_AreaEltibule"));
        svc.GetCalibration(Scene("Map_AreaEltibule"))!.Source.Should().Be(CalibrationSource.BundledBaseline);
    }

    [Fact]
    public void Changed_fires_for_save_and_clear()
    {
        var store = new UserRefinementStore(_tempDir);
        var svc = new MapCalibrationService(
            new Dictionary<string, AreaCalibration>(),
            store,
            goodResidualThresholdPx: 12.0);

        var notifications = new List<MapSceneRef>();
        svc.Changed += (_, scene) => notifications.Add(scene);

        svc.SaveUserRefinement(Scene("Map_AreaEltibule"), MakeCal(residual: 5.0, scale: 1.0));
        svc.ClearUserRefinement(Scene("Map_AreaEltibule"));

        notifications.Should().HaveCount(2);
        notifications[0].MapAssetKey.Should().Be("Map_AreaEltibule");
        notifications[1].MapAssetKey.Should().Be("Map_AreaEltibule");
    }

    [Fact]
    public void Active_calibration_after_high_residual_save_with_baseline_falls_to_baseline()
    {
        // Round-1 review #1 → round-2 review #1 + #5: the SHARED service's
        // GetCalibration honours stacking precedence and returns the baseline
        // when the user's solve has too-high residual. The wizard does NOT
        // call GetCalibration to surface "your solve was good" — it consumes
        // the AreaCalibrationService.CalibrateCurrentArea return value, which
        // is the solver output (covered by the Legolas-side test). The two
        // questions ("what did you solve" vs "what's rendered") are
        // intentionally separate; this test pins the GetCalibration side.
        var baseline = new Dictionary<string, AreaCalibration>
        {
            ["Map_AreaEltibule"] = MakeCal(residual: 4.0, scale: 1.0) with { Source = CalibrationSource.BundledBaseline },
        };
        var svc = new MapCalibrationService(baseline, new UserRefinementStore(_tempDir), goodResidualThresholdPx: 12.0);

        svc.SaveUserRefinement(Scene("Map_AreaEltibule"), MakeCal(residual: 25.0, scale: 2.0));

        var active = svc.GetCalibration(Scene("Map_AreaEltibule"));
        active.Should().NotBeNull();
        active!.Source.Should().Be(CalibrationSource.BundledBaseline);
        active.Scale.Should().Be(1.0);

        // The losing user refinement is still discoverable via GetAllSources
        // (debug surface for "what did the user actually solve").
        svc.GetAllSources(Scene("Map_AreaEltibule"))
            .Should().ContainSingle(s => s.Source == CalibrationSource.UserRefinement && s.Scale == 2.0);
    }

    [Fact]
    public void Save_rolls_back_in_memory_state_when_Persist_throws()
    {
        // Round-2 review #2 (deeper concern): Save mutates _refinements before
        // Persist. If Persist throws (disk full / AV lock / OneDrive
        // placeholder) the in-memory state must roll back so same-session
        // reads see the failure, not a value that vanishes on next process
        // boot. We provoke the throw by locking the file open externally for
        // exclusive write — the temp-write attempt inside Persist fails.
        var store = new UserRefinementStore(_tempDir);
        var initial = MakeCal(residual: 5.0, scale: 1.0);
        store.Save("Map_AreaEltibule", initial);

        // Hold the .tmp path exclusively so the next Save's File.WriteAllText
        // throws when it tries to open it.
        var tmpPath = Path.Combine(_tempDir, "refinements.json.tmp");
        using (var lockHandle = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            FluentActions.Invoking(() => store.Save("Map_AreaEltibule", MakeCal(residual: 5.0, scale: 99.0)))
                .Should().Throw<IOException>();
        }

        // Same-session read returns the original value, not the rolled-back attempt.
        store.TryGet("Map_AreaEltibule", out var current).Should().BeTrue();
        current.Scale.Should().Be(1.0);
    }

    [Fact]
    public void AllCalibrations_returns_asset_keyed_dictionary()
    {
        // The persistence horizon stays string-keyed by MapAssetKey, NOT MapSceneRef-keyed —
        // because the store doesn't know parent-area/friendly-name. Consumers needing
        // parent-area resolution use ISceneAssetCache.
        var baseline = new Dictionary<string, AreaCalibration>
        {
            ["Map_AreaSerbule"] = MakeCal(residual: 4.0, scale: 1.0) with { Source = CalibrationSource.BundledBaseline },
            ["Map_AreaEltibule"] = MakeCal(residual: 4.0, scale: 1.0) with { Source = CalibrationSource.BundledBaseline },
        };
        var svc = new MapCalibrationService(baseline, new UserRefinementStore(_tempDir), goodResidualThresholdPx: 12.0);

        var all = svc.AllCalibrations;
        all.Should().HaveCount(2);
        all.Should().ContainKey("Map_AreaSerbule");
        all.Should().ContainKey("Map_AreaEltibule");
    }

    private static AreaCalibration MakeCal(double residual, double scale) =>
        new(
            Scale: scale,
            RotationRadians: 0,
            OriginX: 100,
            OriginY: 100,
            ReferenceCount: 6,
            ResidualPixels: residual);
}
