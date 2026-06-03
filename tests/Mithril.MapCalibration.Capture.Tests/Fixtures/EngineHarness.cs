using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Capture.Diagnostics;
using Mithril.MapCalibration.Detection;
using Mithril.Shared.Game;

namespace Mithril.MapCalibration.Capture.Tests.Fixtures;

/// <summary>
/// Mutable harness for engine-level tests. Each property has a sensible
/// "happy path" default; a test overrides exactly the one input it exercises.
/// Setting a reference-type property to <c>null</c> models the absence of
/// that input.
///
/// <para>Originally lived privately inside <c>AutoCalibrationEngineTests</c>;
/// lifted to the Fixtures folder so new test classes (#1005 OutcomeCategory +
/// zoom-change regression tests) can share it without copy-pasting.</para>
/// </summary>
internal sealed class EngineHarness
{
    /// <summary>The default area key used by all engine harnesses unless the
    /// test overrides <see cref="CurrentArea"/>.</summary>
    public const string DefaultArea = "AreaEltibule";

    public string? CurrentArea { get; init; } = DefaultArea;
    public CaptureRect? Bbox { get; init; } = new CaptureRect(0, 0, 64, 64);
    public GameWindow? GameWindow { get; init; } = new GameWindow(1, new CaptureRect(0, 0, 1920, 1080));
    public GrayImage? BaseTexture { get; init; } = new GrayImage(64, 64, new byte[64 * 64]);
    public CalibrationSolveResult Solve { get; init; } = new(new AreaCalibration(1, 0, 0, 0, 6, 0.5), 6, null);
    public FakeCalibrationService Service { get; init; } = new();

    // #949: icon templates resolve per attempt via IIconTemplateProvider.
    public FakeIconTemplateProvider IconProvider { get; init; } = new();

    // Optional sidecar wiring for the same-session --icons demand-trigger path.
    // Leave null (default) to model "no extractor wired" (the unit-branch shape).
    public RecordingAssetExtractor? Extractor { get; init; }
    public GameConfig? GameConfig { get; init; }
    public string? AssetCacheDir { get; init; }

    // Optional sink selector for bundle-sink tests. Null → engine default (null sink).
    public CalibrationAttemptBundleSinkSelector? SinkSelector { get; init; }

    public SpyCapture Capture { get; } = new(new GrayImage(64, 64, new byte[64 * 64]));
    public SpySolver Solver { get; private set; } = null!;

    // Refiner stub: defaults to the happy-path rect. Tests can override to:
    //   - new FakeRefiner(null) → drives the engine into the map-not-located path
    //   - a rect with origin >= frame dims → drives the engine into clamp-degenerate
    public IMapRegionRefiner Refiner { get; init; }
        = new FakeRefiner(new MapRect(0, 0, 64, 64, 64, 64));

    public AutoCalibrationEngine Engine()
    {
        Solver = new SpySolver(Solve);
        return new AutoCalibrationEngine(
            new FakeAreaState(CurrentArea),
            new FakeWindowLocator(GameWindow),
            new FakeRegionProvider(Bbox),
            Capture,
            Refiner,
            new FakeBaseTextureProvider(BaseTexture),
            new FakeAreaRefs(new[] { new LandmarkReference("landmark_npc", "x", new WorldCoord(1, 0, 1)) }),
            Solver,
            IconProvider,
            Service,
            logger: null,
            sinkSelector: SinkSelector,
            assetExtractor: Extractor,
            gameConfig: GameConfig,
            assetCacheDir: AssetCacheDir);
    }
}
