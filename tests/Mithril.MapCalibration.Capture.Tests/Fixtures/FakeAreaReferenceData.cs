using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Misc;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;

namespace Mithril.MapCalibration.Capture.Tests.Fixtures;

/// <summary>
/// Minimal in-memory <see cref="IReferenceDataService"/> for the area-reference
/// provider tests. Only <see cref="Landmarks"/> and <see cref="NpcsByInternalName"/>
/// are seeded; every other surface uses the interface default (empty).
/// </summary>
internal sealed class FakeAreaReferenceData : IReferenceDataService
{
    private readonly Dictionary<string, IReadOnlyList<Landmark>> _landmarks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Npc> _npcsByInternalName = new(StringComparer.Ordinal);

    public FakeAreaReferenceData WithLandmarks(string areaKey, params Landmark[] landmarks)
    {
        _landmarks[areaKey] = landmarks;
        return this;
    }

    /// <summary>Seed a full <see cref="Npc"/> POCO (which carries <see cref="Npc.Pos"/>) in an area.</summary>
    public FakeAreaReferenceData WithNpc(string areaKey, string name, string pos)
    {
        _npcsByInternalName["NPC_" + name] = new Npc { Name = name, AreaName = areaKey, Pos = pos };
        return this;
    }

    /// <summary>Seed an NPC with both <see cref="Npc.AreaName"/> (parent aggregator key) and
    /// <see cref="Npc.AreaFriendlyName"/> (sub-zone label) — used by the per-scene-keying
    /// tests (mithril#1021) to scope NPCs to the right sub-zone of an aggregator scene.</summary>
    public FakeAreaReferenceData WithNpc(string areaKey, string areaFriendlyName, string name, string pos)
    {
        _npcsByInternalName["NPC_" + name] = new Npc
        {
            Name = name,
            AreaName = areaKey,
            AreaFriendlyName = areaFriendlyName,
            Pos = pos,
        };
        return this;
    }

    /// <summary>Seed a positionless table entry (no <see cref="Npc.Pos"/>) — e.g. the
    /// "Work Orders" sign / "Sacrificial Bowl" pedestal that live in npcs.json without
    /// a map position. These must be skipped silently, not counted as a coord-shape change.</summary>
    public FakeAreaReferenceData WithPositionlessNpc(string areaKey, string name)
    {
        _npcsByInternalName["NPC_" + name] = new Npc { Name = name, AreaName = areaKey };
        return this;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<Landmark>> Landmarks => _landmarks;
    public IReadOnlyDictionary<string, Npc> NpcsByInternalName => _npcsByInternalName;

    // ── Required (no-default) interface surface — all empty for these tests ──
    public IReadOnlyList<string> Keys => [];
    public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
    public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>(StringComparer.Ordinal);
    public ItemKeywordIndex KeywordIndex { get; } = new(new Dictionary<long, Item>());
    public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, Quest> Quests { get; } = new Dictionary<string, Quest>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, Quest> QuestsByInternalName { get; } = new Dictionary<string, Quest>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "", null, 0);
    public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void BeginBackgroundRefresh() { }
    public event EventHandler<string>? FileUpdated;
    public void RaiseFileUpdated(string key) => FileUpdated?.Invoke(this, key);
}
