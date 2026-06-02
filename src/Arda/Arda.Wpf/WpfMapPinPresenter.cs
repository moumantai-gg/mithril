using System.Collections.ObjectModel;
using Arda.Contracts;
using Arda.World.Player;
using Arda.World.Player.Events;

namespace Arda.Wpf;

/// <summary>
/// Bindable presenter over the active map pins. Maintains an
/// <see cref="ObservableCollection{MapPinEntry}"/> synchronized with the
/// player's current-area pins via domain events.
///
/// <para>On construction, seeds from <see cref="IMapPinState.Pins"/>, then
/// subscribes to <see cref="MapPinAdded"/>, <see cref="MapPinRemoved"/>, and
/// <see cref="AreaChanged"/> events. The collection is cleared when the player
/// transitions to a new area.</para>
///
/// <para>Coordinates are keyed by their centi-unit rounded values (rounding to
/// 2 decimal places) to match the <c>MapPins</c> event handler's 0.01 tolerance.</para>
/// </summary>
public sealed class WpfMapPinPresenter : IDisposable
{
    private readonly Dictionary<(long X, long Z), MapPinEntry> _byCoord = new();
    private IDisposable? _addedSub;
    private IDisposable? _removedSub;
    private IDisposable? _areaChangedSub;

    public ObservableCollection<MapPinEntry> Pins { get; } = new();

    public WpfMapPinPresenter(IMapPinState state, IUiEventSubscriber bus)
    {
        // Seed from initial state
        foreach (var pin in state.Pins)
        {
            Pins.Add(pin);
            var key = Key(pin.X, pin.Z);
            _byCoord[key] = pin;
        }

        // Subscribe to events
        _addedSub = bus.Subscribe<MapPinAdded>(OnAdded);
        _removedSub = bus.Subscribe<MapPinRemoved>(OnRemoved);
        _areaChangedSub = bus.Subscribe<AreaChanged>(OnAreaChanged);
    }

    /// <summary>
    /// Round coordinates to centi-units (0.01 tolerance) to produce the keying
    /// function. Matches the MapPins event handler's tolerance.
    /// </summary>
    private static (long X, long Z) Key(double x, double z)
    {
        return ((long)Math.Round(x * 100), (long)Math.Round(z * 100));
    }

    private void Upsert(MapPinEntry pin, bool addToCollection)
    {
        var key = Key(pin.X, pin.Z);

        if (_byCoord.TryGetValue(key, out var existing))
        {
            // Replace in place
            var idx = Pins.IndexOf(existing);
            if (idx >= 0)
            {
                Pins[idx] = pin;
            }
        }
        else if (addToCollection)
        {
            // New coordinate
            Pins.Add(pin);
        }

        _byCoord[key] = pin;
    }

    private void OnAdded(MapPinAdded e)
    {
        var pin = new MapPinEntry(e.X, e.Z, e.Label, e.Shape, e.Color);
        Upsert(pin, addToCollection: true);
    }

    private void OnRemoved(MapPinRemoved e)
    {
        var key = Key(e.X, e.Z);
        if (_byCoord.TryGetValue(key, out var pin))
        {
            Pins.Remove(pin);
            _byCoord.Remove(key);
        }
    }

    private void OnAreaChanged(AreaChanged _)
    {
        Pins.Clear();
        _byCoord.Clear();
    }

    public void Dispose()
    {
        _addedSub?.Dispose();
        _removedSub?.Dispose();
        _areaChangedSub?.Dispose();
    }
}
