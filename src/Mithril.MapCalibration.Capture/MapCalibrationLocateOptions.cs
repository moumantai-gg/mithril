using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Runtime-flippable knobs for <see cref="FeatureMatchingRefiner"/>
/// (spec §"Gate criteria"). DI singleton, plain mutable POCO,
/// <see cref="INotifyPropertyChanged"/> so a settings UI can bind without
/// re-resolving the graph. Defaults derived from the prototype's
/// cross-validation evidence on the Kur/Eltibule/Serbule corpus
/// (spec §"Cross-validation evidence"): all real wins clear by a wide margin.
/// </summary>
public sealed class MapCalibrationLocateOptions : INotifyPropertyChanged
{
    private int _inlierFloor = 50;
    private double _inlierRatioFloor = 0.50;
    private double _maxRotationDegrees = 0.5;
    private int _orbNFeatures = 8000;
    private double _loweRatio = 0.75;
    private double _ransacReprojectionThresholdPx = 3.0;

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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
