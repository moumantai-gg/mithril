using System.Globalization;
using Arda.Contracts;
using Arda.World.Player;
using Arda.World.Player.Events;
using Arda.Wpf;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;

namespace Palantir.ViewModels;

/// <summary>
/// Debug surface over Arda's live world state: position, area, map pins,
/// celestial (moon phase), and weather. State is read from Arda state
/// interfaces (<see cref="IPositionState"/>, <see cref="IAreaState"/>, etc.)
/// and kept current via domain event subscriptions through
/// <see cref="IUiEventSubscriber"/> — every handler runs on the WPF
/// Dispatcher thread inside a SafeInvoke try/catch, so a misbehaving
/// handler cannot crash the process via an unobserved-task finalizer
/// rethrow.
///
/// <para>Pin rendering is delegated to <see cref="WpfMapPinPresenter"/>;
/// the VM only carries the count + observed-at timestamp for display.</para>
/// </summary>
public sealed partial class WorldStateViewModel : ObservableObject, IDisposable
{
    private readonly IPositionState _position;
    private readonly IAreaState _area;
    private readonly IMapPinState _pinState;
    private readonly ICelestialState _celestial;
    private readonly IWeatherState _weather;
    private readonly IReferenceDataService? _refData;
    private readonly WpfMapPinPresenter _pinPresenter;

    private IDisposable? _positionSub;
    private IDisposable? _areaSub;
    private IDisposable? _pinAddedSub;
    private IDisposable? _pinRemovedSub;
    private IDisposable? _celestialSub;
    private IDisposable? _weatherSub;
    private readonly System.Collections.Specialized.NotifyCollectionChangedEventHandler _pinsChangedHandler;

    [ObservableProperty] private string _areaKey = "(unknown)";
    [ObservableProperty] private string _areaFriendlyName = "(area not yet known)";
    [ObservableProperty] private string _areaShortName = "";
    [ObservableProperty] private bool _areaResolved;

    [ObservableProperty] private bool _hasPosition;
    [ObservableProperty] private string _positionText = "(no position observed yet)";
    [ObservableProperty] private string _measuredAtText = "—";
    [ObservableProperty] private string _positionSourceText = "—";

    [ObservableProperty] private string _pinsObservedAtText = "—";

    [ObservableProperty] private bool _hasMoonPhase;
    [ObservableProperty] private string _moonPhaseText = "(no celestial info observed yet)";
    [ObservableProperty] private string _moonPhaseRawText = "—";
    [ObservableProperty] private string _moonMeasuredAtText = "—";

    [ObservableProperty] private bool _hasWeather;
    [ObservableProperty] private string _weatherConditionText = "(weather unknown for this map)";
    [ObservableProperty] private string _weatherObservedAtText = "—";

    /// <summary>The presenter's UI-thread <see cref="WpfMapPinPresenter.Pins"/>.
    /// XAML binds directly; the VM no longer rebuilds a parallel collection.</summary>
    public System.Collections.ObjectModel.ObservableCollection<MapPinEntry> Pins => _pinPresenter.Pins;

    public int PinCount => Pins.Count;
    public bool HasPins => Pins.Count > 0;

    public WorldStateViewModel(
        IPositionState position,
        IAreaState area,
        IMapPinState pins,
        ICelestialState celestial,
        IWeatherState weather,
        IUiEventSubscriber bus,
        WpfMapPinPresenter pinPresenter,
        IReferenceDataService? refData = null)
    {
        _position = position;
        _area = area;
        _pinState = pins;
        _celestial = celestial;
        _weather = weather;
        _refData = refData;
        _pinPresenter = pinPresenter;

        SeedFromState();

        _positionSub = bus.Subscribe<PlayerPositionChanged>(OnPosition);
        _areaSub = bus.Subscribe<AreaChanged>(OnAreaChanged);
        _pinAddedSub = bus.Subscribe<MapPinAdded>(OnPinAdded);
        _pinRemovedSub = bus.Subscribe<MapPinRemoved>(OnPinRemoved);
        _celestialSub = bus.Subscribe<CelestialInfoChanged>(OnCelestial);
        _weatherSub = bus.Subscribe<WeatherChanged>(OnWeather);

        // Pin count/HasPins shadow the presenter's collection; flip change
        // notification when its size changes. Handler stored in a field so
        // Dispose can unsubscribe — the presenter is a singleton, so leaving
        // the inline lambda subscribed would pin the VM in memory.
        _pinsChangedHandler = (_, _) =>
        {
            OnPropertyChanged(nameof(PinCount));
            OnPropertyChanged(nameof(HasPins));
        };
        _pinPresenter.Pins.CollectionChanged += _pinsChangedHandler;
    }

    private void SeedFromState()
    {
        RefreshArea();

        if (_position.X is not null)
        {
            HasPosition = true;
            PositionText = FormatPosition(_position.X.Value, _position.Y ?? 0, _position.Z ?? 0);
        }

        if (_celestial.Phase != MoonPhase.Unknown || _celestial.CurrentPhaseRaw is not null)
        {
            HasMoonPhase = true;
            MoonPhaseText = _celestial.DisplayName ?? "(unknown phase)";
            MoonPhaseRawText = _celestial.Phase == MoonPhase.Unknown
                ? $"{_celestial.CurrentPhaseRaw} (unrecognised token)"
                : _celestial.CurrentPhaseRaw ?? "—";
            MoonMeasuredAtText = FormatTimestamp(_celestial.MeasuredAt);
        }

        if (_weather.CurrentWeather is { } w)
        {
            HasWeather = true;
            WeatherConditionText = w;
        }
    }

    private void OnPosition(PlayerPositionChanged e)
    {
        HasPosition = true;
        PositionText = FormatPosition(e.X, e.Y, e.Z);
        MeasuredAtText = FormatTimestamp(e.Metadata.Timestamp);
        PositionSourceText = e.Source switch
        {
            PositionSource.Spawn => "Spawn / zone-in (ProcessAddPlayer)",
            PositionSource.Movement => "Movement / teleport (ProcessNewPosition)",
            _ => e.Source.ToString(),
        };
        RefreshArea();
    }

    private void OnAreaChanged(AreaChanged e) => RefreshArea();

    private void OnPinAdded(MapPinAdded e) => PinsObservedAtText = FormatTimestamp(e.Metadata.Timestamp);

    private void OnPinRemoved(MapPinRemoved e) => PinsObservedAtText = FormatTimestamp(e.Metadata.Timestamp);

    private void OnCelestial(CelestialInfoChanged e)
    {
        HasMoonPhase = true;
        MoonPhaseText = e.DisplayName;
        MoonPhaseRawText = e.Phase == MoonPhase.Unknown
            ? $"{e.RawPhase} (unrecognised token)"
            : e.RawPhase;
        MoonMeasuredAtText = FormatTimestamp(e.Metadata.Timestamp);
    }

    private void OnWeather(WeatherChanged e)
    {
        HasWeather = e.Current is not null;
        WeatherConditionText = e.Current ?? "(weather unknown for this map)";
        WeatherObservedAtText = FormatTimestamp(e.Metadata.Timestamp);
    }

    [RelayCommand]
    private void Refresh() => RefreshArea();

    private void RefreshArea()
    {
        var key = _area.CurrentArea;
        if (string.IsNullOrEmpty(key))
        {
            AreaKey = "(none)";
            AreaFriendlyName = "(not in a game area)";
            AreaShortName = "";
            AreaResolved = false;
            return;
        }

        AreaKey = key;
        if (_refData is not null && _refData.Areas.TryGetValue(key, out var entry))
        {
            AreaFriendlyName = entry.FriendlyName;
            AreaShortName = string.Equals(entry.ShortFriendlyName, entry.FriendlyName, StringComparison.Ordinal)
                ? ""
                : entry.ShortFriendlyName;
            AreaResolved = true;
        }
        else
        {
            AreaFriendlyName = key;
            AreaShortName = "";
            AreaResolved = false;
        }
    }

    public void Dispose()
    {
        _pinPresenter.Pins.CollectionChanged -= _pinsChangedHandler;
        _positionSub?.Dispose(); _positionSub = null;
        _areaSub?.Dispose(); _areaSub = null;
        _pinAddedSub?.Dispose(); _pinAddedSub = null;
        _pinRemovedSub?.Dispose(); _pinRemovedSub = null;
        _celestialSub?.Dispose(); _celestialSub = null;
        _weatherSub?.Dispose(); _weatherSub = null;
    }

    private static string FormatPosition(double x, double y, double z) =>
        string.Format(CultureInfo.InvariantCulture, "X {0:0.00}   Y {1:0.00}   Z {2:0.00}", x, y, z);

    private static string FormatTimestamp(DateTimeOffset? ts) =>
        ts?.UtcDateTime.ToString("u", CultureInfo.InvariantCulture) ?? "—";
}
