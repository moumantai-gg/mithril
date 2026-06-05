using System.Text.Json.Serialization;

namespace Mithril.MapCalibration.Internal;

/// <summary>
/// Per-scene typed-slot container in <see cref="UserRefinementStore"/>. Holds at
/// most one <see cref="AreaCalibration"/> per <see cref="CalibrationFrame"/> for
/// a single <c>MapAssetKey</c>; typed slots make &quot;at most one record per
/// (scene, frame)&quot; a compile-time invariant (mithril#1082 §3.1).
///
/// <para>Persisted on Schema-3 user-refinement files as nested objects, e.g.
/// <c>{"Map_X": {"texture": {…}, "overlay": {…}}}</c>. Unused slots are dropped
/// on write by the context-wide <see cref="JsonIgnoreCondition.WhenWritingDefault"/>
/// rule on <see cref="MapCalibrationJsonContext"/>.</para>
/// </summary>
internal sealed record SceneRefinements(
    [property: JsonPropertyName("texture")] AreaCalibration? Texture,
    [property: JsonPropertyName("overlay")] AreaCalibration? Overlay)
{
    /// <summary>Returns the slot for <paramref name="frame"/>, or null if empty.</summary>
    public AreaCalibration? Get(CalibrationFrame frame) => frame switch
    {
        CalibrationFrame.Texture => Texture,
        CalibrationFrame.Overlay => Overlay,
        _ => null,
    };

    /// <summary>Returns a copy with <paramref name="frame"/>'s slot set to <paramref name="cal"/>.</summary>
    public SceneRefinements With(CalibrationFrame frame, AreaCalibration cal) => frame switch
    {
        CalibrationFrame.Texture => this with { Texture = cal },
        CalibrationFrame.Overlay => this with { Overlay = cal },
        _ => throw new ArgumentOutOfRangeException(nameof(frame)),
    };

    /// <summary>Returns a copy with <paramref name="frame"/>'s slot cleared.</summary>
    public SceneRefinements Without(CalibrationFrame frame) => frame switch
    {
        CalibrationFrame.Texture => this with { Texture = null },
        CalibrationFrame.Overlay => this with { Overlay = null },
        _ => throw new ArgumentOutOfRangeException(nameof(frame)),
    };

    /// <summary>True when both slots are null (compaction signal for the store).</summary>
    public bool IsEmpty => Texture is null && Overlay is null;

    /// <summary>Shared empty instance for convenience (no slots populated).</summary>
    public static SceneRefinements Empty { get; } = new(Texture: null, Overlay: null);
}
