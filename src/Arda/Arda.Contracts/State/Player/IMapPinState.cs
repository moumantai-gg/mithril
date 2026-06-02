namespace Arda.World.Player;

/// <summary>
/// A single tracked map pin keyed by its ground-plane coordinate.
/// </summary>
public readonly record struct MapPinEntry(double X, double Z, string Label, int Shape, int Color);

/// <summary>
/// Read-only view of the player's current map pins in the active area.
/// Per-map scoped — pins reset on area transition.
/// </summary>
public interface IMapPinState
{
    /// <summary>
    /// Active pins in the current area, keyed by (X, Z) coordinate. Each call
    /// returns a fresh snapshot — consumers may enumerate the result from any
    /// thread without coordinating with the Arda ingest thread.
    /// </summary>
    IReadOnlyList<MapPinEntry> Pins { get; }
}
