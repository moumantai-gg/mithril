using Arda.Contracts;
using Arda.World.Player;
using Arda.World.Player.Events;
using Mithril.MapCalibration;

namespace Mithril.Overlay.Tests.Fakes;

/// <summary>Mutable test stub for <see cref="IAreaState"/>. The scene-hook
/// tests flip <see cref="CurrentArea"/> to exercise the uncalibrated-area
/// gate and area-key plumbing into <see cref="IOverlaySceneContext"/>.</summary>
internal sealed class StubAreaState : IAreaState
{
    public string? CurrentArea { get; set; }
}

/// <summary>Minimal <see cref="IPositionState"/> stub. The scene-hook
/// tests don't read player position; the service requires the dependency
/// to satisfy the Decision-C consumption-side ctor shape.</summary>
internal sealed class StubPositionState : IPositionState
{
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
}

/// <summary>Minimal <see cref="IMapState"/> stub. The scene-hook tests
/// drive the scene through <c>DriveSceneForTest</c>'s synthesised path,
/// which uses the synth fallback when <see cref="CurrentMapScene"/> is
/// null — so the default null-everywhere stub is exactly what the tests
/// want.</summary>
internal sealed class StubMapState : IMapState
{
    public string? CurrentArea { get; set; }
    public string? PreviousArea => null;
    public DateTimeOffset? TransitionedAt => null;
    public MapSceneRef? CurrentMapScene { get; set; }
    public DateTimeOffset? MapSceneMeasuredAt => null;
    public double? X => null;
    public double? Y => null;
    public double? Z => null;
    public DateTimeOffset? PositionMeasuredAt => null;
    public PositionSource? PositionSource => null;
    public string? CurrentWeather => null;
    public DateTimeOffset? WeatherMeasuredAt => null;
    public IReadOnlyList<MapPinEntry> Pins => Array.Empty<MapPinEntry>();
}

/// <summary>Minimal <see cref="ISceneAssetCache"/> stub. The scene-hook tests
/// don't exercise cache learning — the synth fallback in
/// <c>DriveSceneForTest</c> covers cold-start. Always returns null (miss).</summary>
internal sealed class StubSceneAssetCache : ISceneAssetCache
{
    public MapSceneRef? TryResolve(string parentAreaKey, string? sceneFriendlyName) => null;
    public void Record(MapSceneRef scene, DateTimeOffset observedAt) { }
}

/// <summary>Minimal no-op <see cref="IDomainEventSubscriber"/> for tests
/// whose code path never publishes. The scene-hook tests drive the service
/// via <c>DriveSceneForTest</c> rather than the bus, so no subscriber needs
/// to fire — the stub just hands back a no-op disposable.</summary>
internal sealed class StubDomainEventSubscriber : IDomainEventSubscriber
{
    public IDisposable Subscribe<T>(Action<T> handler) where T : struct => NoopDisposable.Instance;

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
