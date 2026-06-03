using Arda.World.Player.Events;

namespace Arda.World.Player;

/// <summary>
/// Flat read-only view of all map-scoped state: area, position, weather, pins.
/// Consumers needing change notifications subscribe to the corresponding domain
/// events (<see cref="AreaChanged"/>, <see cref="PlayerPositionChanged"/>,
/// <see cref="WeatherChanged"/>, <see cref="MapPinAdded"/>, <see cref="MapPinRemoved"/>)
/// via <see cref="Arda.Contracts.IDomainEventSubscriber"/>.
/// </summary>
public interface IMapState
{
    // --- Area ---

    /// <summary>Area key the player is currently in, or <c>null</c> if not in-world.</summary>
    string? CurrentArea { get; }

    /// <summary>Area key the player was previously in, or <c>null</c> on first zone.</summary>
    string? PreviousArea { get; }

    /// <summary>Timestamp of the most recent area transition.</summary>
    DateTimeOffset? TransitionedAt { get; }

    // --- Map asset (per-Unity-scene texture identity) ---

    /// <summary>Literal Unity Texture2D name for the currently-displayed map texture (e.g. <c>"Map_HogansKeepBasement"</c>),
    /// including the <c>Map_</c> prefix. Source: Player.log's <c>Downloading Map ... runtime key ...[Map_&lt;X&gt;]</c> line.
    /// <c>null</c> until the first such line is observed in this session.</summary>
    string? CurrentMapAsset { get; }

    /// <summary>Sub-zone-level friendly area name from the same line (e.g. <c>"Hogan's Basement"</c>), which for
    /// aggregator areas (<c>AreaCave1</c>, etc.) differs from <see cref="IMapState.CurrentArea"/>'s parent FriendlyName.
    /// Matches the per-NPC <c>AreaFriendlyName</c> field in npcs.json.</summary>
    string? CurrentSceneFriendlyName { get; }

    /// <summary>Timestamp of the most recent <c>Downloading Map</c> line.</summary>
    DateTimeOffset? MapAssetMeasuredAt { get; }

    // --- Position ---

    /// <summary>Engine X coordinate (ground plane), or <c>null</c> before first observation.</summary>
    double? X { get; }

    /// <summary>Engine Y coordinate (elevation), or <c>null</c> before first observation.</summary>
    double? Y { get; }

    /// <summary>Engine Z coordinate (ground plane), or <c>null</c> before first observation.</summary>
    double? Z { get; }

    /// <summary>Timestamp of the most recent position observation.</summary>
    DateTimeOffset? PositionMeasuredAt { get; }

    /// <summary>Source of the most recent position observation (movement vs spawn).</summary>
    PositionSource? PositionSource { get; }

    // --- Weather ---

    /// <summary>Current weather condition string, or <c>null</c> if none observed.</summary>
    string? CurrentWeather { get; }

    /// <summary>Timestamp of the most recent weather observation.</summary>
    DateTimeOffset? WeatherMeasuredAt { get; }

    // --- Pins ---

    /// <summary>Active pins in the current area.</summary>
    IReadOnlyList<MapPinEntry> Pins { get; }
}
