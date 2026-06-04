using System.Collections.Generic;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Thin seam over the Phase-1 <see cref="MapCalibrationSolveEngine"/> so the
/// orchestrator depends on an interface (mockable in branch tests) rather than a
/// concrete sealed engine. The default implementation
/// (<see cref="MapCalibrationSolveEngineAdapter"/>) delegates verbatim; the real
/// detect→solve→gate path is integration-tested in the engine's own test project
/// (<c>SyntheticEndToEndTests</c>). Mirrors the <see cref="IMapRegionRefiner"/>
/// seam-over-static pattern.
/// </summary>
public interface IMapCalibrationSolver
{
    CalibrationSolveResult Solve(DetectionRequest request, IReadOnlyList<LandmarkReference> references);

    /// <summary>
    /// Run the detector phase only (no geometric solve). Returns all typed
    /// detections as a flat list, suitable for the drift-check path (mithril#1046
    /// §6.2) which needs to compare predicted positions to detections without
    /// paying for RANSAC.
    /// </summary>
    IReadOnlyList<TypedDetection> DetectOnly(DetectionRequest request);
}

/// <summary>Production adapter: delegates to the shipped <see cref="MapCalibrationSolveEngine"/>.</summary>
public sealed class MapCalibrationSolveEngineAdapter : IMapCalibrationSolver
{
    private readonly MapCalibrationSolveEngine _engine;

    public MapCalibrationSolveEngineAdapter(MapCalibrationSolveEngine engine) => _engine = engine;

    public CalibrationSolveResult Solve(DetectionRequest request, IReadOnlyList<LandmarkReference> references) =>
        _engine.Solve(request, references);

    /// <inheritdoc/>
    public IReadOnlyList<TypedDetection> DetectOnly(DetectionRequest request) =>
        _engine.DetectOnly(request);
}
