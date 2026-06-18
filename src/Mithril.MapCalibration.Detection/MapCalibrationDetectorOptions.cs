using System.ComponentModel;
using System.Runtime.CompilerServices;
using Mithril.Shared.Character;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Runtime-flippable knobs for the mithril#1116 deviation-mask detector path
/// (spec §D5 boundary-dilation + §D6 fog-of-war detection). DI singleton, plain
/// mutable POCO, <see cref="INotifyPropertyChanged"/> so a settings UI can bind
/// without re-resolving the graph. Defaults come from the deviation-mask spec
/// (Task 0-deferred boundary-dilation + fog-variance experiments) and represent
/// the shipping values until the spec's measurement tasks publish a revised
/// curve.
///
/// <para><b>Persistence (mithril#1116).</b> Implements
/// <see cref="IVersionedState{T}"/> so the JSON-store loader dispatches through
/// <see cref="Migrate"/> on every load. Stored at
/// <c>%LocalAppData%/Mithril/map-calibration-detector.json</c> via the canonical
/// <c>AddMithrilVersionedSettings&lt;T&gt;</c> extension in <c>Mithril.Shared</c>;
/// auto-save is wired by <c>SettingsAutoSaver&lt;T&gt;</c> on every
/// <see cref="INotifyPropertyChanged.PropertyChanged"/> emit. Parallel to
/// <see cref="MapCalibrationLocateOptions"/> (the locate-stage knobs) but
/// scoped to the detector-stage deviation mask + fog filter — separate file
/// so the two surfaces can evolve their schemas independently.</para>
/// </summary>
public sealed class MapCalibrationDetectorOptions : INotifyPropertyChanged, IVersionedState<MapCalibrationDetectorOptions>
{
    public const int Version = 1;
    public static int CurrentVersion => Version;

    /// <summary>
    /// Persisted schema version. Defaults to <c>1</c> for fresh in-memory
    /// instances; a v1 JSON file (the only shape that exists today) deserialises
    /// as v1 and round-trips identity through <see cref="Migrate"/>.
    ///
    /// <para>Future deltas document themselves in this comment block, mirroring
    /// the <see cref="MapCalibrationLocateOptions"/> convention. The first time
    /// a property is renamed/removed or a new dependent default needs
    /// back-filling, bump <see cref="Version"/> and add a branch to
    /// <see cref="Migrate"/>.</para>
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Identity passthrough for v1 (mithril#1116, initial schema). Caller (the
    /// versioned-settings loader) writes the bumped <see cref="SchemaVersion"/>
    /// back after this returns, so Migrate doesn't need to stamp it.
    /// </summary>
    public static MapCalibrationDetectorOptions Migrate(MapCalibrationDetectorOptions loaded)
    {
        if (loaded.SchemaVersion >= Version) return loaded;
        return loaded;
    }

    private bool _deviationMaskingEnabled = true;
    private int _boundaryDilationPx = 8;
    private bool _fogOfWarDetectionEnabled = true;
    private double _fogVarianceThreshold = 30.0;
    private byte _fogColorMin = 110;
    private byte _fogColorMax = 140;
    private double _sceneClassOpaqueFractionThreshold = 0.95;

    /// <summary>
    /// Master switch for the mithril#1116 deviation-mask filter on the
    /// detector path. When <c>true</c>, the detector excludes screenshot pixels
    /// whose per-channel deviation from the canonical base texture falls below
    /// the mask threshold so the matcher sees only "this differs from baseline"
    /// regions (icons, pins, fog-of-war chrome). When <c>false</c>, the
    /// detector reverts to the pre-#1116 unmasked-screenshot behaviour.
    /// Default <c>true</c>.
    /// </summary>
    public bool DeviationMaskingEnabled
    {
        get => _deviationMaskingEnabled;
        set { if (_deviationMaskingEnabled != value) { _deviationMaskingEnabled = value; OnChanged(); } }
    }

    /// <summary>
    /// Radius (px) of the dilation kernel applied to the deviation mask before
    /// matching, so detected difference regions absorb sub-pixel boundary
    /// noise from the renderer/screenshot pipeline (spec §D5). Default
    /// <c>8</c>.
    ///
    /// <para><b>mithril#1174 / #1183 review C5:</b> this global is now the
    /// FALLBACK; the per-scene-class
    /// <see cref="SceneCalibrationProfile.BoundaryDilationPx"/> override wins
    /// when non-null. Indoor sets the override to <c>3</c> (sized to corridor
    /// width — the broader band wipes legitimate corridor icons). Outdoor
    /// leaves the override null so this global drives the dilation; for
    /// Outdoor scenes <c>opaqueFraction ≈ 1</c> makes the alpha-boundary band
    /// degenerate anyway (no edge to dilate), so changes to this global have
    /// essentially no observable effect in production unless a future scene
    /// class lands with both an alpha boundary AND a null profile override.
    /// Setting this global without also updating the relevant profile field
    /// will NOT affect Indoor scenes.</para>
    /// </summary>
    public int BoundaryDilationPx
    {
        get => _boundaryDilationPx;
        set { if (_boundaryDilationPx != value) { _boundaryDilationPx = value; OnChanged(); } }
    }

    /// <summary>
    /// Master switch for the mithril#1116 fog-of-war filter (spec §D6). When
    /// <c>true</c>, the detector drops mask candidates whose local colour
    /// variance is below <see cref="FogVarianceThreshold"/> AND whose mean
    /// colour sits inside the
    /// [<see cref="FogColorMin"/>, <see cref="FogColorMax"/>] grey band, so
    /// the matcher isn't dragged into uniformly-grey fog regions that masquerade
    /// as deviation. When <c>false</c>, the fog filter is bypassed (deviation
    /// mask only). Default <c>true</c>.
    /// </summary>
    public bool FogOfWarDetectionEnabled
    {
        get => _fogOfWarDetectionEnabled;
        set { if (_fogOfWarDetectionEnabled != value) { _fogOfWarDetectionEnabled = value; OnChanged(); } }
    }

    /// <summary>
    /// Per-channel variance ceiling for the fog-of-war classifier (spec §D6).
    /// Candidate mask regions with local variance ≤ this AND mean colour in
    /// the grey band are classified as fog and dropped. Default <c>30.0</c> —
    /// the spec's shipping default; revisited by the Task 0-deferred fog
    /// experiment.
    /// </summary>
    public double FogVarianceThreshold
    {
        get => _fogVarianceThreshold;
        set { if (_fogVarianceThreshold != value) { _fogVarianceThreshold = value; OnChanged(); } }
    }

    /// <summary>
    /// Lower bound (inclusive, 0–255) of the grey-band mean-colour check for
    /// the fog-of-war classifier (spec §D6). Default <c>110</c>.
    /// </summary>
    public byte FogColorMin
    {
        get => _fogColorMin;
        set { if (_fogColorMin != value) { _fogColorMin = value; OnChanged(); } }
    }

    /// <summary>
    /// Upper bound (inclusive, 0–255) of the grey-band mean-colour check for
    /// the fog-of-war classifier (spec §D6). Default <c>140</c>.
    /// </summary>
    public byte FogColorMax
    {
        get => _fogColorMax;
        set { if (_fogColorMax != value) { _fogColorMax = value; OnChanged(); } }
    }

    /// <summary>
    /// Minimum opaque-pixel fraction (alpha ≥ 128 / total px) for a base
    /// texture to be classified as <see cref="SceneClass.Outdoor"/>. Anything
    /// below this is <see cref="SceneClass.Indoor"/>. Default <c>0.95</c> —
    /// the
    /// [`scene-class-classification.md`](../../../docs/planning/calibration-1155-scene-class-profile/measurements/scene-class-classification.md)
    /// spike measured Outdoor opaque-fraction = 1.00 (3 scenes) versus Indoor
    /// 0.07–0.36 (10 scenes), so the gap is wide and the threshold has
    /// substantial margin on both sides.
    ///
    /// <para><b>mithril#1163 / spec §5.2.</b> Drives
    /// <c>FloorBoundaryMaskCache.GetSceneClass</c>. Fail-soft: when alpha is
    /// unavailable the classifier returns Outdoor (the Outdoor
    /// <see cref="SceneCalibrationProfile"/> carries today's universal
    /// constants, so safe-degrade is byte-identical to pre-#1163).</para>
    /// </summary>
    public double SceneClassOpaqueFractionThreshold
    {
        get => _sceneClassOpaqueFractionThreshold;
        set { if (_sceneClassOpaqueFractionThreshold != value) { _sceneClassOpaqueFractionThreshold = value; OnChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
