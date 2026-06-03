namespace Mithril.MapCalibration.Detection;

/// <summary>
/// How the blob detector excludes the irregular map rim — the stone border PG
/// draws around outdoor zones, which is a false-positive factory for icon
/// detection.
/// </summary>
public enum RimMaskMode
{
    /// <summary>No rim masking. Use for flat tan/desert areas with no stone border.</summary>
    None,

    /// <summary>
    /// Colour flood-fill from the image edge through the non-vegetation/water
    /// region (the lifted <c>BorderMask</c>). Requires the BGRA screenshot, so it
    /// is only available on the overload that takes one; the gate study found it
    /// over-masks the interior (Eltibule 67.6% vs deviation-flood 11.3%).
    /// </summary>
    ColourFlood,

    /// <summary>
    /// Edge-connected high-deviation flood. The rim is the foreground component
    /// that touches the image edge; interior icons are isolated foreground
    /// islands, and the matching interior terrain isn't foreground at all. Drops
    /// the rim without eating the interior the colour flood over-masks
    /// (mithril#897). This is the NEW algorithm Phase 1 ships.
    /// </summary>
    DeviationFlood,
}
