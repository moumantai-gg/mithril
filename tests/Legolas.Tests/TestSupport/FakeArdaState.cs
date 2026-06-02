using Arda.World.Player;

namespace Legolas.Tests.TestSupport;

/// <summary>Minimal <see cref="IAreaState"/> test double.</summary>
internal sealed class FakeAreaState : IAreaState
{
    public string? CurrentArea { get; set; }
}

/// <summary>Minimal <see cref="IPositionState"/> test double.</summary>
internal sealed class FakePositionState : IPositionState
{
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
}

/// <summary>Minimal <see cref="IMapPinState"/> test double.</summary>
internal sealed class FakeMapPinState : IMapPinState
{
    private readonly List<MapPinEntry> _pins = new();

    public IReadOnlyList<MapPinEntry> Pins => _pins.AsReadOnly();

    public void Add(MapPinEntry pin) => _pins.Add(pin);

    public void Add(double x, double z, string label = "") => _pins.Add(new MapPinEntry(x, z, label, 0, 0));

    public void Remove(double x, double z)
    {
        _pins.RemoveAll(p => p.X == x && p.Z == z);
    }

    public void Clear() => _pins.Clear();

    /// <summary>Pre-seed the pin state (replaces the old FakePlayerPinTracker.SeedExisting).</summary>
    public void SeedExisting(params MapPinEntry[] pins)
    {
        _pins.Clear();
        _pins.AddRange(pins);
    }

    public static MapPinEntry Pin(double x, double z, string label = "", int shape = 0, int color = 0) =>
        new(x, z, label, shape, color);
}
