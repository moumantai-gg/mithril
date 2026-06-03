using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Composite that implements <see cref="IMapState"/> by delegating to the
/// individual map-scoped handlers (<see cref="Map"/>, <see cref="Position"/>,
/// <see cref="Weather"/>, <see cref="MapPins"/>, <see cref="MapAssetLoader"/>).
/// Registered as a singleton; consumers inject <see cref="IMapState"/> for a
/// flat view of all map state.
/// </summary>
internal sealed class MapScope(
    Map map,
    Position position,
    Weather weather,
    MapPins pins,
    MapAssetLoader mapAsset) : IMapState
{
    public string? CurrentArea => map.CurrentArea;
    public string? PreviousArea => map.PreviousArea;
    public DateTimeOffset? TransitionedAt => map.TransitionedAt;

    public string? CurrentMapAsset => mapAsset.CurrentMapAsset;
    public string? CurrentSceneFriendlyName => mapAsset.CurrentSceneFriendlyName;
    public DateTimeOffset? MapAssetMeasuredAt => mapAsset.MapAssetMeasuredAt;

    public double? X => position.X;
    public double? Y => position.Y;
    public double? Z => position.Z;
    public DateTimeOffset? PositionMeasuredAt => position.MeasuredAt;
    public PositionSource? PositionSource => position.Source;

    public string? CurrentWeather => weather.CurrentWeather;
    public DateTimeOffset? WeatherMeasuredAt => weather.MeasuredAt;

    public IReadOnlyList<MapPinEntry> Pins => pins.Pins;
}
