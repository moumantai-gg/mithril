using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Mithril.MapCalibration;

/// <summary>
/// Three-state toggle for the synthesis-J re-rank (spec §Q2).
/// <list type="bullet">
/// <item><c>Off</c> — no L_t build, legacy inlier-count gate is the source of truth, zero cost.</item>
/// <item><c>Shadow</c> — L_t built, synthesis-J computed + emitted as telemetry, but the legacy gate is still the source of truth for accept/reject (and therefore for persistence). Safe-to-deploy default while Phase-C telemetry accumulates.</item>
/// <item><c>Enabled</c> — synthesis-J is the gate; accept iff <c>J ≥ J_min AND refs_above_0.5 ≥ N_min</c>. Legacy inlier count + residual remain informational.</item>
/// </list>
/// </summary>
public enum SynthesisRerankMode
{
    Off,
    Shadow,
    Enabled,
}

/// <summary>
/// Runtime-flippable knobs for <see cref="Detection.MapCalibrationSolveEngine"/>'s
/// synthesis-J re-rank (spec §Q2). Mirrors the
/// <see cref="Mithril.MapCalibration.Capture.CaptureDiagnosticsOptions"/> pattern:
/// DI singleton, plain mutable POCO, INotifyPropertyChanged so a settings UI
/// can bind without re-resolving the graph.
///
/// <para>Default <see cref="SynthesisRerankMode"/> is <see cref="SynthesisRerankMode.Shadow"/> —
/// the engine computes synthesis-J + emits telemetry but the legacy
/// inlier-count gate remains the source of truth (spec §Q2 "Why Shadow is the
/// default"). The <see cref="SynthesisJMin"/> / <see cref="SynthesisNMin"/>
/// defaults are anchored to PR #993's post-rim 4-bundle dataset (Bundle A=19/21,
/// B-truth=15.5/16, C=14/13 accept; B-wrong-fit=2.5/4 rejects); recalibrate
/// against real telemetry per spec §Q3 Phase C before flipping the default.</para>
/// </summary>
public sealed class MapCalibrationSolverOptions : INotifyPropertyChanged
{
    private SynthesisRerankMode _synthesisRerankMode = SynthesisRerankMode.Shadow;
    private double _synthesisJMin = 8.0;
    private int _synthesisNMin = 8;
    private int _ransacTopK = 8;

    /// <summary>Active re-rank mode. Default <see cref="SynthesisRerankMode.Shadow"/>.</summary>
    public SynthesisRerankMode SynthesisRerankMode
    {
        get => _synthesisRerankMode;
        set { if (_synthesisRerankMode != value) { _synthesisRerankMode = value; OnChanged(); } }
    }

    /// <summary>J floor for the <see cref="SynthesisRerankMode.Enabled"/> gate. Default 8.0.</summary>
    public double SynthesisJMin
    {
        get => _synthesisJMin;
        set { if (_synthesisJMin != value) { _synthesisJMin = value; OnChanged(); } }
    }

    /// <summary>Floor on <c>refs whose sampled L_t ≥ 0.5</c> for the <see cref="SynthesisRerankMode.Enabled"/> gate. Default 8.</summary>
    public int SynthesisNMin
    {
        get => _synthesisNMin;
        set { if (_synthesisNMin != value) { _synthesisNMin = value; OnChanged(); } }
    }

    /// <summary>Number of RANSAC candidates the re-rank scores per orientation. Default 8.</summary>
    public int RansacTopK
    {
        get => _ransacTopK;
        set { if (_ransacTopK != value) { _ransacTopK = value; OnChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
