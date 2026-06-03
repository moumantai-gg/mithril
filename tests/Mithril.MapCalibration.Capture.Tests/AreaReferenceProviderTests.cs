using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Mithril.Reference.Models.Misc;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// Task 22 (#914): builds the area's landmark/NPC reference points for the
/// solver. The <see cref="Landmark.Type"/> strings + <see cref="Landmark.Loc"/>
/// format are confirmed against live landmarks.json (v470):
/// Type ∈ { Portal, MeditationPillar, TeleportationPlatform }; Loc = "x:N y:N z:N".
/// </summary>
public sealed class AreaReferenceProviderTests
{
    [Fact]
    public void Maps_landmarks_and_npcs_to_typed_references()
    {
        var refData = new FakeAreaReferenceData()
            .WithLandmarks("AreaSerbule",
                new Landmark { Name = "Serbule Portal", Loc = "x:10 y:0 z:-20", Type = "Portal" },
                new Landmark { Name = "Medi Pillar", Loc = "x:5 y:0 z:5", Type = "MeditationPillar" },
                new Landmark { Name = "Teleport Circle", Loc = "x:1 y:0 z:2", Type = "TeleportationPlatform" })
            .WithNpc("AreaSerbule", "Marna", pos: "x:30 y:0 z:40");

        var refs = new ReferenceDataAreaReferenceProvider(refData).ForArea(new MapSceneRef("AreaSerbule", null));

        // mithril#974: Type carries the raw PG vocabulary (the same the detector
        // keys IconTemplate.LandmarkType by); the landmark_* sprite strings are
        // template NAMES, never types.
        refs.Should().Contain(r => r.Type == "Portal" && r.World.X == 10 && r.World.Z == -20);
        refs.Should().Contain(r => r.Type == "MeditationPillar");
        refs.Should().Contain(r => r.Type == "TeleportationPlatform");
        refs.Should().Contain(r => r.Type == "Npc" && r.Name == "Marna" && r.World.X == 30 && r.World.Z == 40);
    }

    [Fact]
    public void Skips_landmarks_with_a_malformed_or_missing_loc()
    {
        var refData = new FakeAreaReferenceData()
            .WithLandmarks("AreaSerbule",
                new Landmark { Name = "Good", Loc = "x:1 y:2 z:3", Type = "Portal" },
                new Landmark { Name = "NoLoc", Loc = null, Type = "Portal" },
                new Landmark { Name = "Garbage", Loc = "not a position", Type = "Portal" });

        var refs = new ReferenceDataAreaReferenceProvider(refData).ForArea(new MapSceneRef("AreaSerbule", null));

        refs.Should().ContainSingle(r => r.Type == "Portal");
        refs.Should().OnlyContain(r => r.Name == "Good");
    }

    [Fact]
    public void Positionless_entries_are_skipped_without_a_coord_shape_warning()
    {
        // npcs.json carries non-spatial table entries (the "Work Orders" sign, the
        // "Sacrificial Bowl" pedestal) with no Pos. They were never calibration
        // references; skipping them is not a coord-shape regression, so the
        // shape-change Warning must NOT fire (Fix E — kills the recurring false alarm).
        var refData = new FakeAreaReferenceData()
            .WithNpc("AreaEltibule", "Braigon", pos: "x:1099 y:37 z:1398")
            .WithPositionlessNpc("AreaEltibule", "Work Orders")
            .WithPositionlessNpc("AreaEltibule", "Sacrificial Bowl");

        var logger = new ListLogger();
        var refs = new ReferenceDataAreaReferenceProvider(refData, logger).ForArea(new MapSceneRef("AreaEltibule", null));

        refs.Should().ContainSingle(r => r.Type == "Npc" && r.Name == "Braigon");
        logger.Warnings.Should().NotContain(m => m.Contains("coord", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Present_but_malformed_coords_trigger_the_shape_change_warning()
    {
        // A position string that IS present but doesn't parse is the genuine
        // shape-change signal — that, and only that, warrants the Warning.
        var refData = new FakeAreaReferenceData()
            .WithLandmarks("AreaSerbule",
                new Landmark { Name = "Good", Loc = "x:1 y:2 z:3", Type = "Portal" },
                new Landmark { Name = "Garbage", Loc = "not a position", Type = "Portal" });

        var logger = new ListLogger();
        new ReferenceDataAreaReferenceProvider(refData, logger).ForArea(new MapSceneRef("AreaSerbule", null));

        logger.Warnings.Should().ContainSingle(m =>
            m.Contains("malformed coords") && m.Contains("1"));
    }

    [Fact]
    public void Drops_an_unmapped_landmark_type_rather_than_guessing_a_token()
    {
        // An unknown Type must not be silently coerced to a wrong token; it is
        // dropped (and warned, verification-owed). A wrong token would mispair
        // the detection and corrupt the solve.
        var refData = new FakeAreaReferenceData()
            .WithLandmarks("AreaSerbule",
                new Landmark { Name = "Mystery", Loc = "x:1 y:0 z:1", Type = "SomeFutureLandmark" });

        new ReferenceDataAreaReferenceProvider(refData).ForArea(new MapSceneRef("AreaSerbule", null)).Should().BeEmpty();
    }

    [Fact]
    public void Unknown_area_yields_empty()
        => new ReferenceDataAreaReferenceProvider(new FakeAreaReferenceData()).ForArea(new MapSceneRef("AreaNope", null)).Should().BeEmpty();

    [Fact]
    public void Emitted_types_are_a_subset_of_the_canonical_vocabulary()
    {
        // mithril#974 seam guard: every reference Type the provider emits must be
        // in CanonicalLandmarkTypes.All — the same vocabulary the detector keys
        // IconTemplate.LandmarkType by — so the type-constrained solver can pair
        // them. A stray landmark_* token here would re-open the 0-inliers bug.
        var refData = new FakeAreaReferenceData()
            .WithLandmarks("AreaSerbule",
                new Landmark { Name = "P", Loc = "x:1 y:0 z:1", Type = "Portal" },
                new Landmark { Name = "M", Loc = "x:2 y:0 z:2", Type = "MeditationPillar" },
                new Landmark { Name = "T", Loc = "x:3 y:0 z:3", Type = "TeleportationPlatform" })
            .WithNpc("AreaSerbule", "Marna", pos: "x:4 y:0 z:4");

        var refs = new ReferenceDataAreaReferenceProvider(refData).ForArea(new MapSceneRef("AreaSerbule", null));

        refs.Select(r => r.Type).Distinct().Should().OnlyContain(t => CanonicalLandmarkTypes.All.Contains(t));
        refs.Should().NotBeEmpty();
    }

    // ── mithril#1021 per-scene keying: composite (ParentAreaKey, SceneFriendlyName?) ──

    [Fact]
    public void ForArea_AggregatorSceneFriendlyName_NarrowsNpcsToThatSubzone()
    {
        // AreaCave1 is an aggregator under which multiple sub-zones live; the
        // SceneFriendlyName narrows NPCs to the matching AreaFriendlyName only.
        var refData = new FakeAreaReferenceData()
            .WithNpc("AreaCave1", "Hogan's Basement", "Gorvessa", pos: "x:100 y:0 z:200")
            .WithNpc("AreaCave1", "Goblin Dungeon", "Goblin", pos: "x:300 y:0 z:400");

        var refs = new ReferenceDataAreaReferenceProvider(refData)
            .ForArea(new MapSceneRef("AreaCave1", "Hogan's Basement"));

        refs.Should().ContainSingle(r => r.Name == "Gorvessa");
        refs.Should().NotContain(r => r.Name == "Goblin");
    }

    [Fact]
    public void ForArea_NullSceneFriendlyName_CollapsesToAreaOnlyFilter()
    {
        // Back-compat: a directly-registered area passes SceneFriendlyName = null,
        // so the filter collapses to AreaName-only (pre-#1021 behaviour).
        var refData = new FakeAreaReferenceData()
            .WithNpc("AreaSerbule", "Serbule", "A", pos: "x:1 y:0 z:1")
            .WithNpc("AreaSerbule", "Serbule", "B", pos: "x:2 y:0 z:2");

        var refs = new ReferenceDataAreaReferenceProvider(refData)
            .ForArea(new MapSceneRef("AreaSerbule", SceneFriendlyName: null));

        refs.Should().HaveCount(2);
    }

    [Fact]
    public void ForArea_AggregatorWithoutMatchingFriendlyName_ReturnsEmpty()
    {
        // Aggregator scene whose SceneFriendlyName matches no NPC's
        // AreaFriendlyName → empty NPC set (the gate then rejects on no inliers).
        var refData = new FakeAreaReferenceData()
            .WithNpc("AreaCave1", "Goblin Dungeon", "X", pos: "x:1 y:0 z:1");

        var refs = new ReferenceDataAreaReferenceProvider(refData)
            .ForArea(new MapSceneRef("AreaCave1", "Hogan's Basement"));

        refs.Should().BeEmpty();
    }

    /// <summary>Minimal <see cref="ILogger"/> that collects Warning messages for assertions.</summary>
    private sealed class ListLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
