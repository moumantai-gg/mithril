using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Capture.Diagnostics;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
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

    /// <summary>The default per-scene map-asset key. Mirrors the
    /// <see cref="DefaultArea"/> in the cosmically-trivial 1:1 case the legacy
    /// happy-path tests assume (mithril#1021: real aggregator scenes carry a
    /// distinct asset name).</summary>
    public const string DefaultMapAsset = "Map_AreaEltibule";

    /// <summary>The default per-scene asset key the engine uses for persistence
    /// + lookup post-#1021. Equal to <see cref="DefaultMapAsset"/>; aliased
    /// separately so test sites that read/write the calibration store can name
    /// their intent ("the asset-key the engine persists under") without coupling
    /// to the texture-load semantics.</summary>
    public const string DefaultAssetKey = DefaultMapAsset;

    public string? CurrentArea { get; init; } = DefaultArea;

    /// <summary>Per-scene map-asset key for the engine's strict gate (mithril#1021 D3).
    /// Defaults to <see cref="DefaultMapAsset"/> so the legacy happy-path tests
    /// don't trip the gate. Set to <c>null</c> to model "Downloading Map line
    /// not yet observed in this session".</summary>
    public string? CurrentMapAsset { get; init; } = DefaultMapAsset;

    /// <summary>Sub-zone friendly name for aggregator scenes. Defaults to <c>null</c>
    /// (directly-registered area).</summary>
    public string? CurrentSceneFriendlyName { get; init; }

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

    /// <summary>Base-texture provider. Exposed so #1021 tests can assert
    /// <see cref="FakeBaseTextureProvider.Calls"/> reflects the per-scene asset
    /// key the engine looked up. Auto-constructed in <see cref="Engine"/> from
    /// <see cref="BaseTexture"/>.</summary>
    public FakeBaseTextureProvider BaseTextureProvider { get; private set; } = null!;

    /// <summary>Reference-data provider. Exposed so #1021 tests can assert
    /// <see cref="FakeAreaRefs.LastSceneRef"/> carries the composite scene
    /// identity. Auto-constructed in <see cref="Engine"/>.</summary>
    public FakeAreaRefs AreaRefs { get; private set; } = null!;

    // Refiner stub: defaults to the happy-path rect. Tests can override to:
    //   - new FakeRefiner(null) → drives the engine into the map-not-located path
    //   - a rect with origin >= frame dims → drives the engine into clamp-degenerate
    public IMapRegionRefiner Refiner { get; init; }
        = new FakeRefiner(new MapRect(0, 0, 64, 64, 64, 64));

    /// <summary>
    /// mithril#1163 — optional deviation-mask wiring. All three default to
    /// null; when null the engine skips the mask block and resolves
    /// <see cref="SceneClass.Outdoor"/> by safe-degrade. Tests that exercise
    /// the SceneClass dispatch wire all three:
    /// <see cref="DetectorOptions"/> with <c>DeviationMaskingEnabled=true</c>,
    /// the cache, and the fog detector. The cache is auto-constructed in
    /// <see cref="Engine"/> from the same <see cref="BaseTextureProvider"/>
    /// that the engine sees — set
    /// <see cref="FakeBaseTextureProvider.AlphaByKey"/> to populate alpha.
    /// </summary>
    public bool WireDeviationMaskDeps { get; init; }

    /// <summary>mithril#1163 — exposed so tests can flex
    /// <see cref="SceneClassOpaqueFractionThreshold"/>.</summary>
    public MapCalibrationDetectorOptions? DetectorOptions { get; private set; }

    /// <summary>mithril#1163 — the cache the engine resolves SceneClass through.
    /// Auto-constructed in <see cref="Engine"/> when
    /// <see cref="WireDeviationMaskDeps"/> is true.</summary>
    public FloorBoundaryMaskCache? BoundaryMaskCache { get; private set; }

    public AutoCalibrationEngine Engine()
    {
        Solver = new SpySolver(Solve);
        BaseTextureProvider = new FakeBaseTextureProvider(BaseTexture);
        AreaRefs = new FakeAreaRefs(new[] { new LandmarkReference("landmark_npc", "x", new WorldCoord(1, 0, 1)) });
        // Build the scene only when both CurrentArea and CurrentMapAsset are present;
        // either being null models "no scene resolved yet" (the strict-gate test path).
        // The scene-resolution helper returns null in that case, driving the engine's
        // not-in-world short-circuit.
        var mapState = new FakeMapState
        {
            CurrentArea = CurrentArea,
            CurrentMapScene = (CurrentArea is null || CurrentMapAsset is null)
                ? null
                : new MapSceneRef(CurrentArea, CurrentSceneFriendlyName, CurrentMapAsset),
        };
        var sceneCache = new FakeSceneAssetCache();
        FogOfWarDetector? fog = null;
        if (WireDeviationMaskDeps)
        {
            DetectorOptions = new MapCalibrationDetectorOptions();   // defaults: masking + fog enabled
            BoundaryMaskCache = new FloorBoundaryMaskCache(BaseTextureProvider, DetectorOptions);
            fog = new FogOfWarDetector(DetectorOptions);
        }
        return new AutoCalibrationEngine(
            mapState,
            sceneCache,
            new FakeWindowLocator(GameWindow),
            new FakeRegionProvider(Bbox),
            Capture,
            Refiner,
            BaseTextureProvider,
            AreaRefs,
            Solver,
            IconProvider,
            Service,
            logger: null,
            sinkSelector: SinkSelector,
            assetExtractor: Extractor,
            gameConfig: GameConfig,
            assetCacheDir: AssetCacheDir,
            detectorOptions: DetectorOptions,
            boundaryMaskCache: BoundaryMaskCache,
            fogOfWarDetector: fog);
    }
}
