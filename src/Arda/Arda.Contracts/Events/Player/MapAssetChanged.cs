using Arda.Abstractions.Logs;
using Mithril.MapCalibration;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when PG's asset loader fetches a per-scene map texture
/// (the unbracketed "Downloading Map [GUID] ... runtime key GUID[Map_&lt;X&gt;]"
/// Player.log line). Carries the previous + current composite scene identity
/// (<see cref="MapSceneRef"/>) — subscribers can diff fields directly via record
/// equality. For aggregator <c>AreaX</c> entries (e.g. <c>AreaCave1</c>),
/// <see cref="MapSceneRef.SceneFriendlyName"/> identifies the specific sub-scene
/// where the parent area's <c>FriendlyName</c> would not.
/// </summary>
public readonly record struct MapAssetChanged(
    MapSceneRef? PreviousScene,
    MapSceneRef? CurrentScene,
    LogLineMetadata Metadata);
