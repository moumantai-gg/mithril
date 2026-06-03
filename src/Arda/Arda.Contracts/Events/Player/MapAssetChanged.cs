using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when PG's asset loader fetches a per-scene map texture
/// (the unbracketed "Downloading Map [GUID] ... runtime key GUID[Map_&lt;X&gt;]"
/// Player.log line). Carries both the literal Unity Texture2D name
/// (<paramref name="CurrentMapAsset"/>, including the <c>Map_</c> prefix)
/// and the sub-zone-level friendly name from the same line
/// (<paramref name="CurrentSceneFriendlyName"/>, matching npcs.json's
/// <c>AreaFriendlyName</c>). For aggregator <c>AreaX</c> entries (e.g.
/// <c>AreaCave1</c>), <see cref="CurrentSceneFriendlyName"/> identifies the
/// specific sub-scene where the parent area's <c>FriendlyName</c> would not.
/// </summary>
public readonly record struct MapAssetChanged(
    string? PreviousMapAsset,
    string? CurrentMapAsset,
    string? CurrentSceneFriendlyName,
    LogLineMetadata Metadata);
