using System.ComponentModel;
using System.Runtime.CompilerServices;
using Mithril.Shared.Character;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Runtime-flippable knobs for <see cref="FeatureMatchingRefiner"/>
/// (spec §"Gate criteria") and the mithril#1061 Sobel-padded-pyramid fallback.
/// DI singleton, plain mutable POCO, <see cref="INotifyPropertyChanged"/> so a
/// settings UI can bind without re-resolving the graph. Defaults derived from
/// the ORB prototype's Kur/Eltibule/Serbule corpus + the #1061 round-5
/// indoor sub-scene corpus (real wins clear by a wide margin).
///
/// <para><b>Persistence (mithril#1061).</b> Implements
/// <see cref="IVersionedState{T}"/> so the JSON-store loader dispatches through
/// <see cref="Migrate"/> on every load. Stored at
/// <c>%LocalAppData%/Mithril/map-calibration-locate.json</c> via the canonical
/// <c>AddMithrilVersionedSettings&lt;T&gt;</c> extension in <c>Mithril.Shared</c>;
/// auto-save is wired by <c>SettingsAutoSaver&lt;T&gt;</c> on every
/// <see cref="INotifyPropertyChanged.PropertyChanged"/> emit.</para>
/// </summary>
public sealed class MapCalibrationLocateOptions : INotifyPropertyChanged, IVersionedState<MapCalibrationLocateOptions>
{
    public const int Version = 1;
    public static int CurrentVersion => Version;

    /// <summary>
    /// Persisted schema version. Defaults to <c>1</c> so a v1 JSON file (no
    /// pre-existing schema field) deserialises as v1; fresh in-memory instances
    /// also start at <c>1</c> — <see cref="Migrate"/> is a no-op for v1.
    ///
    /// <para>Future deltas document themselves in this comment block, mirroring
    /// the <c>LegolasSettings</c> convention. The first time a property is
    /// renamed/removed or a new dependent default needs back-filling, bump
    /// <see cref="Version"/> and add a branch to <see cref="Migrate"/>.</para>
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// v1 is the first persisted version. Identity passthrough today; future
    /// schema changes add branches here (e.g. v1 → v2: rename
    /// <c>FallbackPadPx</c> → <c>FallbackPaddingPx</c> would carry the old
    /// value into the new property name). Caller (the versioned-settings
    /// loader) writes the bumped <see cref="SchemaVersion"/> back after this
    /// returns, so Migrate doesn't need to stamp it.
    /// </summary>
    public static MapCalibrationLocateOptions Migrate(MapCalibrationLocateOptions loaded)
    {
        if (loaded.SchemaVersion >= Version) return loaded;
        return loaded;
    }

    private int _inlierFloor = 50;
    private double _inlierRatioFloor = 0.50;
    private double _maxRotationDegrees = 0.5;
    private int _orbNFeatures = 8000;
    private double _loweRatio = 0.75;
    private double _ransacReprojectionThresholdPx = 3.0;
    private double _fallbackNccFloor = 0.20;
    private int _fallbackPadPx = 100;
    private double _scaleMin = 0.20;
    private double _scaleMax = 1.20;
    private double _scaleStep = 0.02;
    private int _minScaledDim = 20;
    private int _minScaledDimHalf = 10;
    private int _minScaledDimCoarse = 5;

    /// <summary>Reject any fit with fewer than this many RANSAC inliers. Default 50.</summary>
    public int InlierFloor
    {
        get => _inlierFloor;
        set { if (_inlierFloor != value) { _inlierFloor = value; OnChanged(); } }
    }

    /// <summary>Reject any fit whose RANSAC inlier ratio is below this. Default 0.50.</summary>
    public double InlierRatioFloor
    {
        get => _inlierRatioFloor;
        set { if (_inlierRatioFloor != value) { _inlierRatioFloor = value; OnChanged(); } }
    }

    /// <summary>Reject any fit whose recovered rotation exceeds this magnitude. PG's UI is axis-aligned; anything &gt; 0.5° is a wrong fit, not a rotated map. Default 0.5°.</summary>
    public double MaxRotationDegrees
    {
        get => _maxRotationDegrees;
        set { if (_maxRotationDegrees != value) { _maxRotationDegrees = value; OnChanged(); } }
    }

    /// <summary>Cap on ORB keypoints per image. Default 8000 (prototype baseline).</summary>
    public int OrbNFeatures
    {
        get => _orbNFeatures;
        set { if (_orbNFeatures != value) { _orbNFeatures = value; OnChanged(); } }
    }

    /// <summary>Lowe's ratio-test threshold. Match m kept iff m.distance &lt; LoweRatio * second.distance. Default 0.75.</summary>
    public double LoweRatio
    {
        get => _loweRatio;
        set { if (_loweRatio != value) { _loweRatio = value; OnChanged(); } }
    }

    /// <summary>RANSAC reprojection threshold in screenshot pixels. Default 3.0.</summary>
    public double RansacReprojectionThresholdPx
    {
        get => _ransacReprojectionThresholdPx;
        set { if (_ransacReprojectionThresholdPx != value) { _ransacReprojectionThresholdPx = value; OnChanged(); } }
    }

    /// <summary>
    /// Reject any Sobel-padded-pyramid fallback fit whose refined NCC is below
    /// this floor. Default 0.20 — round-5 corpus: real recoveries hit 0.45+,
    /// input-pathology cases sit at 0.20–0.32 (mithril#1061).
    /// </summary>
    public double FallbackNccFloor
    {
        get => _fallbackNccFloor;
        set { if (_fallbackNccFloor != value) { _fallbackNccFloor = value; OnChanged(); } }
    }

    /// <summary>
    /// Zero padding (px, all four sides) applied to the capture's Sobel
    /// magnitude before matchTemplate runs in the fallback. Default 100 px —
    /// enough headroom for the corpus's worst spill (HogansKeep-223119 = 34 px)
    /// without ballooning the pyramid's coarse stage (mithril#1061).
    /// </summary>
    public int FallbackPadPx
    {
        get => _fallbackPadPx;
        set { if (_fallbackPadPx != value) { _fallbackPadPx = value; OnChanged(); } }
    }

    /// <summary>Lower bound of the fallback's scale ladder. Default 0.20 (mithril#1061).</summary>
    public double ScaleMin
    {
        get => _scaleMin;
        set { if (_scaleMin != value) { _scaleMin = value; OnChanged(); } }
    }

    /// <summary>Upper bound of the fallback's scale ladder. Default 1.20 (mithril#1061).</summary>
    public double ScaleMax
    {
        get => _scaleMax;
        set { if (_scaleMax != value) { _scaleMax = value; OnChanged(); } }
    }

    /// <summary>Step between rungs of the fallback's coarse + fine ladders. Default 0.02 (mithril#1061).</summary>
    public double ScaleStep
    {
        get => _scaleStep;
        set { if (_scaleStep != value) { _scaleStep = value; OnChanged(); } }
    }

    /// <summary>Minimum scaled template dimension (px) at the fallback's full-resolution stage. Default 20 (mithril#1061).</summary>
    public int MinScaledDim
    {
        get => _minScaledDim;
        set { if (_minScaledDim != value) { _minScaledDim = value; OnChanged(); } }
    }

    /// <summary>Minimum scaled template dimension (px) at the fallback's half-resolution stage. Default 10 (mithril#1061).</summary>
    public int MinScaledDimHalf
    {
        get => _minScaledDimHalf;
        set { if (_minScaledDimHalf != value) { _minScaledDimHalf = value; OnChanged(); } }
    }

    /// <summary>Minimum scaled template dimension (px) at the fallback's quarter-resolution stage. Default 5 (mithril#1061).</summary>
    public int MinScaledDimCoarse
    {
        get => _minScaledDimCoarse;
        set { if (_minScaledDimCoarse != value) { _minScaledDimCoarse = value; OnChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
