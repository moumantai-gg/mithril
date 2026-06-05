namespace Legolas.Domain;

public sealed record Survey(
    Guid Id,
    string Name,
    MetreOffset Offset,
    OverlayPixel? PixelPos,
    OverlayPixel? ManualOverride,
    int GridIndex,
    bool Collected,
    bool Skipped,
    int? RouteOrder)
{
    /// <summary>
    /// Absolute world coordinate (#454) when this pin came from a Player.log
    /// <c>ProcessMapFx</c> target rather than a relative chat <c>[Status]</c>
    /// offset. Drives <c>(X,Z)</c> dedupe and diagnostics; null for the legacy
    /// relative path (init-only so the positional ctor / <c>with</c> stay
    /// source-compatible).
    /// </summary>
    public WorldCoord? World { get; init; }

    public OverlayPixel? EffectivePixel => ManualOverride ?? PixelPos;

    public bool IsCorrected => ManualOverride.HasValue;

    public static Survey Create(string name, MetreOffset offset, int gridIndex) =>
        new(Guid.NewGuid(), name, offset, null, null, gridIndex, false, false, null);

    /// <summary>
    /// A pin from an absolute <c>ProcessMapFx</c> target: the world coord is
    /// authoritative and already projected to <paramref name="pixel"/>. No
    /// metre offset (the relative model doesn't apply to absolute targets).
    /// </summary>
    public static Survey CreateAbsolute(string name, WorldCoord world, OverlayPixel pixel, int gridIndex) =>
        new(Guid.NewGuid(), name, MetreOffset.Zero, pixel, null, gridIndex, false, false, null)
        {
            World = world,
        };
}
