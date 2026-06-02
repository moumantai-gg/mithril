using System.ComponentModel;
using Arda.Contracts;
using Arda.World.Player;
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
/// the extracted OnAreaChangedAsync seam.</summary>
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
    private readonly MapRect? _rect;
    public FakeRefiner(MapRect? rect) => _rect = rect;
    public MapRect? Refine(GrayImage capturedGray, GrayImage baseTexture, double minScore) => _rect;
}

internal sealed class FakeBaseTextureProvider : IBaseTextureProvider
{
    private readonly GrayImage? _tex;
    public FakeBaseTextureProvider(GrayImage? tex) => _tex = tex;
    public GrayImage? TryGetBaseTexture(string areaKey) => _tex;
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
    private readonly IReadOnlyList<LandmarkReference> _refs;
    public FakeAreaRefs(IReadOnlyList<LandmarkReference> refs) => _refs = refs;
    public IReadOnlyList<LandmarkReference> ForArea(string areaKey) => _refs;
}

internal sealed class SpySolver : IMapCalibrationSolver
{
    private readonly CalibrationSolveResult _result;
    public SpySolver(CalibrationSolveResult result) => _result = result;
    public bool Called { get; private set; }
    public CalibrationSolveResult Solve(DetectionRequest request, IReadOnlyList<LandmarkReference> references)
    {
        Called = true;
        return _result;
    }
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
    public Dictionary<string, AreaCalibration> Saved { get; } = new(StringComparer.Ordinal);

    public void Seed(string areaKey, AreaCalibration cal) => _prior[areaKey] = cal;

    public bool IsCalibrated(string areaKey) => Saved.ContainsKey(areaKey) || _prior.ContainsKey(areaKey);
    public PixelPoint? WorldToWindow(string areaKey, WorldCoord world, double currentZoom) => null;
    public WorldCoord? WindowToWorld(string areaKey, PixelPoint pixel, double currentZoom) => null;
    public AreaCalibration? GetCalibration(string areaKey) =>
        Saved.TryGetValue(areaKey, out var s) ? s : (_prior.TryGetValue(areaKey, out var p) ? p : null);
    public IReadOnlyDictionary<string, AreaCalibration> AllCalibrations => Saved;
    public IReadOnlyList<AreaCalibration> GetAllSources(string areaKey) => Array.Empty<AreaCalibration>();
    public void SaveUserRefinement(string areaKey, AreaCalibration calibration)
    {
        Saved[areaKey] = calibration;
        Changed?.Invoke(this, areaKey);
    }
    public void ClearUserRefinement(string areaKey) => Saved.Remove(areaKey);
    public int ImportUserRefinements(IReadOnlyDictionary<string, AreaCalibration> source) => 0;
    public event EventHandler<string>? Changed;
}
