using System.ComponentModel;
using Arda.Contracts;
using Arda.World.Player;
using Arda.World.Player.Events;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Detection;
using Mithril.Overlay;

namespace Mithril.MapCalibration.Capture.Tests.Fixtures;

/// <summary>Headless IOverlayWindow for the DI-resolution test. Never touched
/// during resolution; Window throws if a test accidentally dereferences it (no
/// WPF Window is created off the STA thread).</summary>
internal sealed class FakeOverlayWindow : IOverlayWindow
{
    public System.Windows.Window Window => throw new InvalidOperationException("FakeOverlayWindow.Window must not be touched in a headless test.");
    public bool IsReady => false;
    public string? StatusMessage { get; private set; }
    public void SetStatusMessage(string? message) { StatusMessage = message; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusMessage))); }
    public IDisposable RegisterScene(Action<IOverlaySceneContext> draw) => new Noop();
    public event PropertyChangedEventHandler? PropertyChanged;
    private sealed class Noop : IDisposable { public void Dispose() { } }
}

/// <summary>No-op event bus: the trigger's hosted-service Subscribe wiring is
/// exercised by the DI-resolution test; the gating logic is tested directly via
/// the extracted OnSceneChangedAsync seam.</summary>
internal sealed class FakeDomainEventSubscriber : IDomainEventSubscriber
{
    public IDisposable Subscribe<T>(Action<T> handler) where T : struct => new Noop();
    private sealed class Noop : IDisposable { public void Dispose() { } }
}

internal sealed class FakeAreaState : IAreaState
{
    public FakeAreaState(string? area) => CurrentArea = area;
    public string? CurrentArea { get; }
}

/// <summary>
/// Flat <see cref="IMapState"/> fake for engine tests (mithril#1041). Every
/// property is a settable init so tests construct what they need with an
/// object initializer; un-set properties default to <c>null</c> / empty.
/// </summary>
internal sealed class FakeMapState : IMapState
{
    // --- Area ---
    public string? CurrentArea { get; set; }
    public string? PreviousArea { get; set; }
    public DateTimeOffset? TransitionedAt { get; set; }

    // --- Map asset (composite) ---
    public MapSceneRef? CurrentMapScene { get; set; }
    public DateTimeOffset? MapSceneMeasuredAt { get; set; }

    // --- Position ---
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
    public DateTimeOffset? PositionMeasuredAt { get; set; }
    public PositionSource? PositionSource { get; set; }

    // --- Weather ---
    public string? CurrentWeather { get; set; }
    public DateTimeOffset? WeatherMeasuredAt { get; set; }

    // --- Pins ---
    public IReadOnlyList<MapPinEntry> Pins { get; set; } = Array.Empty<MapPinEntry>();
}

/// <summary>
/// In-memory <see cref="ISceneAssetCache"/> fake for engine tests (mithril#1041).
/// Tests prime entries via <see cref="Add"/>; <see cref="Record"/> writes through
/// as the real cache does.
/// </summary>
internal sealed class FakeSceneAssetCache : ISceneAssetCache
{
    private readonly Dictionary<(string, string?), MapSceneRef> _store = new();
    public void Add(MapSceneRef scene) => _store[(scene.ParentAreaKey, scene.SceneFriendlyName)] = scene;
    public MapSceneRef? TryResolve(string parentAreaKey, string? sceneFriendlyName) =>
        _store.TryGetValue((parentAreaKey, sceneFriendlyName), out var s) ? s : null;
    public void Record(MapSceneRef scene, DateTimeOffset observedAt) => Add(scene);
}

internal sealed class FakeWindowLocator : IGameWindowLocator
{
    private readonly GameWindow? _window;
    public FakeWindowLocator(GameWindow? window) => _window = window;
    public GameWindow? Locate() => _window;
}

internal sealed class FakeRegionProvider : IMapCaptureRegionProvider
{
    public FakeRegionProvider(CaptureRect? current) => Current = current;
    public CaptureRect? Current { get; }
}

internal sealed class SpyCapture : ICaptureService
{
    private readonly GrayImage? _result;
    public SpyCapture(GrayImage? result = null) => _result = result;
    public bool Called { get; private set; }
    public Task<CaptureMapResult> CaptureMapAsync(CaptureRect bbox, CancellationToken ct)
    {
        Called = true;
        // Produce a minimal CapturedFrame so the engine can assign RawCapture.
        // The color pixels don't need to be meaningful for unit tests.
        CapturedFrame? color = _result is not null
            ? new CapturedFrame(_result.Width, _result.Height, new byte[_result.Width * _result.Height * 4])
            : null;
        return Task.FromResult(new CaptureMapResult(color, _result));
    }
}

internal sealed class FakeRefiner : IMapRegionRefiner
{
    private readonly MapRegionRefineResult _result;
    public FakeRefiner(MapRect? rect, LocateMetrics? metrics = null)
        => _result = new MapRegionRefineResult(AcceptedRect: rect, RawFitRect: rect, Metrics: metrics);
    public FakeRefiner(MapRegionRefineResult result) => _result = result;
    public MapRegionRefineResult Refine(GrayImage capturedGray, GrayImage baseTexture) => _result;

    /// <summary>
    /// Convenience for tests that exercise the map-not-located reject path:
    /// returns a refiner that always reports "no fit". Equivalent to
    /// <c>new FakeRefiner((MapRect?)null)</c> but avoids the disambiguation
    /// cast that `new FakeRefiner(null)` requires (the two overloads of the
    /// constructor both accept reference types).
    /// </summary>
    public static FakeRefiner NotLocated() => new(rect: (MapRect?)null);
}

internal sealed class FakeBaseTextureProvider : IBaseTextureProvider
{
    /// <summary>
    /// Parameterless constructor for the mithril#1021 object-initializer style:
    /// <c>new FakeBaseTextureProvider { ResolveAs = ... }</c>.
    /// </summary>
    public FakeBaseTextureProvider() { }

    /// <summary>Legacy positional constructor — pre-#1021 tests pass the texture directly.</summary>
    public FakeBaseTextureProvider(GrayImage? tex) => ResolveAs = tex;

    /// <summary>The texture this provider returns for any <see cref="TryGetBaseTexture"/> call.</summary>
    public GrayImage? ResolveAs { get; set; }

    /// <summary>Every <see cref="TryGetBaseTexture"/> key, in call order. The strict-gate test
    /// (#1021) asserts this is empty when the engine refuses early.</summary>
    public List<string> Calls { get; } = new();

    public GrayImage? TryGetBaseTexture(string mapAssetKey)
    {
        Calls.Add(mapAssetKey);
        return ResolveAs;
    }
}

/// <summary>
/// Per-attempt icon-template provider fake (#949). Returns whatever set
/// <see cref="Set"/> currently holds (default: <see cref="IconTemplateSet.Empty"/>),
/// counts calls, and can flip its set to a populated one to model a same-session
/// populate (e.g. after the engine's <c>--icons</c> demand-trigger).
/// </summary>
internal sealed class FakeIconTemplateProvider : IIconTemplateProvider
{
    public FakeIconTemplateProvider(IconTemplateSet? set = null) => Set = set ?? IconTemplateSet.Empty;
    public IconTemplateSet Set { get; set; }
    public int Calls { get; private set; }
    public IconTemplateSet GetTemplates()
    {
        Calls++;
        return Set;
    }
}

internal sealed class FakeAreaRefs : IAreaReferenceProvider
{
    public FakeAreaRefs() => References = Array.Empty<LandmarkReference>();
    public FakeAreaRefs(IReadOnlyList<LandmarkReference> refs) => References = refs;

    /// <summary>References returned by every <see cref="ForArea"/> call. Settable
    /// so callers may use either the positional constructor or an
    /// object-initializer (mithril#1021 plan style).</summary>
    public IReadOnlyList<LandmarkReference> References { get; set; }

    /// <summary>The <see cref="MapSceneRef"/> from the most recent
    /// <see cref="ForArea"/> call, or <c>null</c> if not yet invoked. Used by
    /// the per-scene-keying tests (mithril#1021) to assert the composite
    /// scene identity flows through unmodified.</summary>
    public MapSceneRef? LastSceneRef { get; private set; }

    public IReadOnlyList<LandmarkReference> ForArea(MapSceneRef sceneRef)
    {
        LastSceneRef = sceneRef;
        return References;
    }
}

internal sealed class SpySolver : IMapCalibrationSolver
{
    private readonly CalibrationSolveResult _result;
    public SpySolver(CalibrationSolveResult result) => _result = result;
    public bool Called { get; private set; }
    /// <summary>Number of <see cref="Solve"/> calls. The mithril#1021 strict-gate
    /// test asserts this is zero when the engine refuses early.</summary>
    public int SolveCalls { get; private set; }
    public CalibrationSolveResult Solve(DetectionRequest request, IReadOnlyList<LandmarkReference> references)
    {
        Called = true;
        SolveCalls++;
        return _result;
    }

    /// <summary>Not used by the calibration path; throws to guard accidental invocation.</summary>
    public IReadOnlyList<TypedDetection> DetectOnly(DetectionRequest request) =>
        throw new InvalidOperationException("SpySolver.DetectOnly must not be called from the calibration path.");
}

/// <summary>
/// Records each <see cref="IAssetExtractor.ExtractAsync"/> call and returns a
/// configurable canned result, so the icon bootstrap's decision logic can be
/// tested headless (no real exe). Defaults to a success with no artifacts.
/// </summary>
internal sealed class RecordingAssetExtractor : IAssetExtractor
{
    private readonly ExtractResult _result;
    public RecordingAssetExtractor(ExtractResult? result = null) =>
        _result = result ?? new ExtractResult(true, 0, Array.Empty<ExtractedArtifact>(), null);

    public List<ExtractRequest> Calls { get; } = new();

    public Task<ExtractResult> ExtractAsync(ExtractRequest request, CancellationToken ct)
    {
        Calls.Add(request);
        return Task.FromResult(_result);
    }
}

/// <summary>An extractor whose ExtractAsync throws — proves the bootstrap fail-softs.</summary>
internal sealed class ThrowingAssetExtractor : IAssetExtractor
{
    public Task<ExtractResult> ExtractAsync(ExtractRequest request, CancellationToken ct) =>
        throw new InvalidOperationException("boom");
}

internal sealed class FakeCalibrationService : IMapCalibrationService
{
    private readonly Dictionary<string, AreaCalibration> _prior = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<AreaCalibration>> _allSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorldToTextureCalibration> _textureCals = new(StringComparer.Ordinal);
    public Dictionary<string, AreaCalibration> Saved { get; } = new(StringComparer.Ordinal);
    public List<MapSceneRef> SavedScenes { get; } = new();

    public void Seed(string mapAssetKey, AreaCalibration cal)
    {
        _prior[mapAssetKey] = cal;
        // Seed a parallel texture-frame entry by default — mirrors the
        // production picker's behaviour for AutoCapture/BundledBaseline records.
        // Tests that want the "overlay-frame only" path explicitly leave
        // _textureCals empty (don't call SeedTextureCalibration).
        if (cal.Source is CalibrationSource.AutoCapture or CalibrationSource.BundledBaseline)
        {
            _textureCals[mapAssetKey] = new WorldToTextureCalibration(
                cal.OriginX, cal.OriginY, cal.Scale, cal.RotationRadians,
                cal.MirrorNorth, cal.CalibrationZoom);
        }
    }

    /// <summary>
    /// #1076 explicit texture-frame seed for tests that need to control
    /// <see cref="GetTextureCalibration"/> independently of
    /// <see cref="GetCalibration"/> — e.g. the frame-aware refusal path
    /// where an overlay-frame UserRefinement record exists but no texture
    /// record does.
    /// </summary>
    public void SeedTextureCalibration(string mapAssetKey, WorldToTextureCalibration cal)
        => _textureCals[mapAssetKey] = cal;

    /// <summary>
    /// Seeds the list returned by <see cref="GetAllSources"/> independently from
    /// <see cref="GetCalibration"/>. Use this when the picker and the store should
    /// return different shapes (e.g. the picker prefers Baseline but the store also
    /// holds an AutoCapture record).
    /// </summary>
    public void SeedAllSources(string mapAssetKey, IReadOnlyList<AreaCalibration> sources)
        => _allSources[mapAssetKey] = sources;

    public bool IsCalibrated(MapSceneRef scene) =>
        Saved.ContainsKey(scene.MapAssetKey) || _prior.ContainsKey(scene.MapAssetKey);
    public PixelPoint? WorldToWindow(MapSceneRef scene, WorldCoord world, double currentZoom) => null;
    public WorldCoord? WindowToWorld(MapSceneRef scene, PixelPoint pixel, double currentZoom) => null;
    public TexturePixel? WorldToTexture(MapSceneRef scene, WorldCoord world, double currentZoom) =>
        _textureCals.TryGetValue(scene.MapAssetKey, out var c) ? c.ToTexture(world, currentZoom) : null;
    public WorldCoord? TextureToWorld(MapSceneRef scene, TexturePixel pixel, double currentZoom) =>
        _textureCals.TryGetValue(scene.MapAssetKey, out var c) ? c.FromTexture(pixel, currentZoom) : null;
    public OverlayPixel? WorldToOverlay(MapSceneRef scene, WorldCoord world, double currentZoom) => null;
    public WorldCoord? OverlayToWorld(MapSceneRef scene, OverlayPixel pixel, double currentZoom) => null;
    public WorldToTextureCalibration? GetTextureCalibration(MapSceneRef scene) =>
        _textureCals.TryGetValue(scene.MapAssetKey, out var c) ? c : null;
    public WorldToOverlayCalibration? GetOverlayCalibration(MapSceneRef scene) => null;
    public AreaCalibration? GetCalibration(MapSceneRef scene) =>
        Saved.TryGetValue(scene.MapAssetKey, out var s)
            ? s
            : (_prior.TryGetValue(scene.MapAssetKey, out var p) ? p : null);
    public IReadOnlyDictionary<string, AreaCalibration> AllCalibrations => Saved;
    public IReadOnlyList<AreaCalibration> GetAllSources(MapSceneRef scene) =>
        _allSources.TryGetValue(scene.MapAssetKey, out var sources)
            ? sources
            : Array.Empty<AreaCalibration>();
    public void SaveUserRefinement(MapSceneRef scene, AreaCalibration calibration)
    {
        Saved[scene.MapAssetKey] = calibration;
        SavedScenes.Add(scene);
        Changed?.Invoke(this, scene);
    }
    public void ClearUserRefinement(MapSceneRef scene) => Saved.Remove(scene.MapAssetKey);
    public event EventHandler<MapSceneRef>? Changed;
}
