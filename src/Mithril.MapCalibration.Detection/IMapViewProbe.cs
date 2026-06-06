using Mithril.MapCalibration;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Cross-correlates a current overlay screenshot against the cached base
/// texture for an area to produce a <see cref="MapViewFix"/> describing
/// PG's live world-map view state (pan + scale). The mechanism the
/// runtime engine uses to "ground the current view" so the durable
/// layer-1 cal (which projects to texture pixels) can be composed into
/// live overlay pixels — see <c>spec.md</c> §4.2.
///
/// <para><b>Fail-soft:</b> returns <c>null</c> when (a) the base texture is
/// missing, (b) the screenshot doesn't show enough of the map (UI overlay,
/// no map open), (c) the correlation peak fails the confidence gate, or
/// (d) the capture itself fails. Producers refuse to render rather than
/// rendering through a guessed layer-2.</para>
///
/// <para><b>Cost target:</b> sub-1s per call. Implementations should
/// coarse-to-fine and bound search ranges; callers invoke on a background
/// thread and marshal back to the UI thread.</para>
/// </summary>
public interface IMapViewProbe
{
    /// <summary>
    /// Probe for the current view state by correlating <paramref name="screenshot"/>
    /// against <paramref name="baseTexture"/>. Returns the measured fix, or
    /// <c>null</c> if no acceptable peak emerged.
    /// </summary>
    MapViewFix? TryProbe(GrayImage screenshot, GrayImage baseTexture);
}