using System.Collections.Generic;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Supplies the landmark/NPC world-anchor reference points the solver pairs
/// detections against, for one scene. Decoupled behind an interface so the
/// orchestrator depends on a seam (testable with a fake) rather than the
/// reference-data service directly.
/// </summary>
public interface IAreaReferenceProvider
{
    /// <summary>
    /// Landmark + NPC references for the scene identified by
    /// <paramref name="sceneRef"/>. NPCs are filtered on
    /// <c>(AreaName == ParentAreaKey)</c>, further narrowed by
    /// <c>AreaFriendlyName == SceneFriendlyName</c> when the latter is non-null.
    /// Landmarks are filtered on <c>ParentAreaKey</c> alone (landmarks.json
    /// has no sub-zone field). For directly-registered areas
    /// (<see cref="MapSceneRef.SceneFriendlyName"/> null) the filter collapses
    /// to the legacy area-only behaviour. Empty when the scene is unknown or
    /// carries no mappable references — which fail-soft yields no inliers →
    /// the gate rejects.
    /// </summary>
    IReadOnlyList<LandmarkReference> ForArea(MapSceneRef sceneRef);
}
