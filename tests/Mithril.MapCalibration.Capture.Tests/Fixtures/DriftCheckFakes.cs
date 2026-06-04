using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture.Tests.Fixtures;

/// <summary>
/// Six landmark references for drift-check tests (mithril#1046 §6).
/// With Stored() calibration (Scale=1, Origin=(100,100), MirrorNorth=false,
/// Rotation=0) the world-to-texture projection is:
///   screenX = 100 + worldX
///   screenY = 100 - worldZ
/// </summary>
internal static class TestReferences
{
    public static readonly IReadOnlyList<LandmarkReference> Six = new[]
    {
        new LandmarkReference("Landmark", "Landmark1", new WorldCoord( 10, 0,   5)),
        new LandmarkReference("Landmark", "Landmark2", new WorldCoord(-20, 0,  15)),
        new LandmarkReference("Landmark", "Landmark3", new WorldCoord( 30, 0, -25)),
        new LandmarkReference("Landmark", "Landmark4", new WorldCoord(-40, 0, -10)),
        new LandmarkReference("Landmark", "Landmark5", new WorldCoord(  5, 0,  35)),
        new LandmarkReference("Landmark", "Landmark6", new WorldCoord(-15, 0,  20)),
    };
}

/// <summary>
/// Typed detection factory for drift-check test scenarios (mithril#1046 §6).
/// Coordinates are placed at the predicted screen positions (100 + worldX,
/// 100 - worldZ) with an optional uniform offset added to both axes to simulate
/// drift.
/// </summary>
internal static class TestDetections
{
    /// <summary>
    /// Detections placed at predicted positions for all six references, with an
    /// optional uniform offset on both axes (simulates sub-pixel noise or drift).
    /// </summary>
    public static IReadOnlyList<TypedDetection> AtPredictedPositions(double offsetPx)
    {
        var list = new List<TypedDetection>();
        foreach (var r in TestReferences.Six)
        {
            var x = 100.0 + r.World.X + offsetPx;
            var y = 100.0 - r.World.Z + offsetPx;
            list.Add(new TypedDetection(r.Type, r.Name, AnchorX: x, AnchorY: y, Score: 0.95));
        }
        return list;
    }

    /// <summary>
    /// Detections placed only at the predicted positions for the first
    /// <paramref name="n"/> references. Used to drive the "too few matched"
    /// (Inconclusive) path.
    /// </summary>
    public static IReadOnlyList<TypedDetection> AtFirstNPredictions(int n, double offsetPx)
    {
        var list = new List<TypedDetection>();
        foreach (var r in TestReferences.Six.Take(n))
        {
            var x = 100.0 + r.World.X + offsetPx;
            var y = 100.0 - r.World.Z + offsetPx;
            list.Add(new TypedDetection(r.Type, r.Name, AnchorX: x, AnchorY: y, Score: 0.95));
        }
        return list;
    }
}

/// <summary>
/// Fake refiner for drift-check tests; equivalent to <see cref="FakeRefiner"/>
/// but with named factory methods that express intent clearly in test bodies.
/// Uses a 400x400 capture/texture frame to give locator scale=1, Tx=0, Ty=0
/// so screen coords == texture-pixel coords.
/// </summary>
internal sealed class FakeMapRegionRefinerDrift : IMapRegionRefiner
{
    private readonly MapRegionRefineResult _result;
    private FakeMapRegionRefinerDrift(MapRegionRefineResult result) => _result = result;

    public MapRegionRefineResult Refine(GrayImage capturedGray, GrayImage baseTexture) => _result;

    /// <summary>
    /// Accept with a 400x400 rect (texture 400x400) so screen == texture pixel.
    /// LocateMetrics scale=1 means the locator recovered a 1:1 screenshot-texture
    /// mapping; Tx=0, Ty=0 so the origin is at screen (0,0) — predictions from
    /// AreaCalibration.WorldToWindow already land in texture-pixel space and
    /// coincide with screen space when MapRect origin is (0,0).
    /// </summary>
    public static FakeMapRegionRefinerDrift Accept() =>
        new(new MapRegionRefineResult(
            AcceptedRect: new MapRect(0, 0, 400, 400, 400, 400),
            RawFitRect: new MapRect(0, 0, 400, 400, 400, 400),
            Metrics: new LocateMetrics(
                InlierCount: 30, CandidateCount: 40, InlierRatio: 0.75,
                Scale: 1.0, RotationDegrees: 0, Mirror: false,
                Tx: 0, Ty: 0, ResidualPixels: 0.5)));

    public static FakeMapRegionRefinerDrift Reject(string reason) =>
        new(new MapRegionRefineResult(
            AcceptedRect: null,
            RawFitRect: null,
            Metrics: null));
}

/// <summary>
/// Fake solver for drift-check tests. Returns <see cref="SeededDetections"/> from
/// <see cref="DetectOnly"/>; throws on <see cref="Solve"/> to verify the drift-check
/// path does not invoke the geometric solve.
/// </summary>
internal sealed class FakeCalibrationSolverDrift : IMapCalibrationSolver
{
    public IReadOnlyList<TypedDetection> SeededDetections { get; init; } = Array.Empty<TypedDetection>();

    public IReadOnlyList<TypedDetection> DetectOnly(DetectionRequest request) => SeededDetections;

    public CalibrationSolveResult Solve(DetectionRequest request, IReadOnlyList<LandmarkReference> references) =>
        throw new InvalidOperationException("Drift check path must not invoke Solve.");
}

/// <summary>
/// Fake reference provider that returns <see cref="TestReferences.Six"/> for any
/// scene, used by drift-check tests.
/// </summary>
internal sealed class FakeDriftAreaRefs : IAreaReferenceProvider
{
    private readonly IReadOnlyList<LandmarkReference> _refs;

    public FakeDriftAreaRefs(IReadOnlyList<LandmarkReference>? refs = null)
        => _refs = refs ?? TestReferences.Six;

    public IReadOnlyList<LandmarkReference> ForArea(MapSceneRef sceneRef) => _refs;
}

/// <summary>
/// Capturing ILogger for asserting that specific log messages were emitted.
/// </summary>
internal sealed class CapturingLogger : ILogger
{
    public readonly List<(LogLevel Level, string Message)> Entries = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
        NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
