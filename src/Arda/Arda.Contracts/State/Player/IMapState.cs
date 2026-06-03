using Arda.World.Player.Events;
using Mithril.MapCalibration;

namespace Arda.World.Player;

/// <summary>
/// Flat read-only view of all map-scoped state: area, map asset, position, weather, pins.
/// Consumers needing change notifications subscribe to the corresponding domain
/// events (<see cref="AreaChanged"/>, <see cref="MapAssetChanged"/>,
/// <see cref="PlayerPositionChanged"/>, <see cref="WeatherChanged"/>,
/// <see cref="MapPinAdded"/>, <see cref="MapPinRemoved"/>)
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

    /// <summary>Composite map-scene identity (parent area + sub-zone friendly name + Unity asset key),
    /// or <c>null</c> until the first <c>Downloading Map</c> line is observed this session.
    /// <para>Live truth — preferred over <c>Mithril.MapCalibration.ISceneAssetCache</c>
    /// resolution. Source: Player.log's <c>Downloading Map ... runtime key ...[Map_&lt;X&gt;]</c>
    /// line, parsed by <c>Arda.World.Player.Internal.MapAssetLoader</c>.</para></summary>
    MapSceneRef? CurrentMapScene { get; }

    /// <summary>Timestamp of the most recent <c>Downloading Map</c> line.</summary>
    DateTimeOffset? MapSceneMeasuredAt { get; }

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
