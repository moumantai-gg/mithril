using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Mithril.MapCalibration.Diagnostics;

/// <summary>
/// Local <see cref="ActivitySource"/> + <see cref="Meter"/> catalog for the
/// <c>Mithril.MapCalibration</c> Detection layer. Defined here (and NOT in
/// <c>Mithril.Shared/Diagnostics/Telemetry/MithrilActivitySources.cs</c> +
/// <c>MithrilMeters.cs</c>) because <c>Mithril.MapCalibration.csproj</c>
/// deliberately doesn't reference <c>Mithril.Shared</c> — the same constraint
/// Arda's catalogs work around (see <c>ArdaActivitySources</c> / <c>ArdaMeters</c>).
///
/// <para>Names follow the <c>"Mithril.…"</c> prefix convention so listeners
/// subscribing to the prefix receive both the Shared catalogs and this one
/// uniformly. The Capture layer already emits <c>"Mithril.MapCalibration.Capture"</c>
/// spans through <see cref="Mithril.Shared.Diagnostics.Telemetry.MithrilActivitySources.MapCalibration"/>;
/// this catalog adds <c>"Mithril.MapCalibration.Detection"</c> so the per-layer
/// vocabulary is unambiguous when a Seq waterfall surfaces both at once.</para>
/// </summary>
public static class MapCalibrationDiagnostics
{
    /// <summary>
    /// Spans emitted from the Detection layer's solve / synthesis-J path.
    /// Parent span (<c>calibration.solve</c>) lives in the Capture layer's
    /// <see cref="Mithril.Shared.Diagnostics.Telemetry.MithrilActivitySources.MapCalibration"/>;
    /// when both are listened-to, this source's children (<c>calibration.synthesis_rerank</c>)
    /// nest under the Capture parent naturally.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new("Mithril.MapCalibration.Detection");

    /// <summary>Map auto-calibration synthesis-J re-rank instruments (spec §Q2 telemetry contract).</summary>
    public static class Meters
    {
        public static readonly Meter Meter = new("Mithril.MapCalibration.Detection");

        /// <summary>Winning candidate's <c>J(T_k)</c>. Tag: <c>verdict</c> ∈ {accept, reject} (per synthesis-J).</summary>
        public static readonly Histogram<double> SynthesisJ =
            Meter.CreateHistogram<double>("mithril.map_calibration.synthesis.j");

        /// <summary>Refs whose sampled <c>L_t(T·r) ≥ 0.5</c> for the winning candidate. Tag: <c>verdict</c>.</summary>
        public static readonly Histogram<long> SynthesisRefsAboveThreshold =
            Meter.CreateHistogram<long>("mithril.map_calibration.synthesis.refs_above_threshold");

        /// <summary>Synthesis-J disagreed with the legacy inlier-count gate. Tag: <c>change</c> ∈ {accept_to_reject, reject_to_accept}.</summary>
        public static readonly Counter<long> SynthesisDisagree =
            Meter.CreateCounter<long>("mithril.map_calibration.synthesis.disagree");
    }

    /// <summary>
    /// Picker telemetry for <c>MapCalibrationService.PickByFrame</c> (#1093 spec §5.1).
    ///
    /// <para>The spec catalogs this counter under
    /// <c>MithrilMeters.LegolasCalibration.PickerOutcomes</c> in <c>Mithril.Shared</c>,
    /// but <c>Mithril.MapCalibration.csproj</c> deliberately doesn't reference
    /// <c>Mithril.Shared</c> (layering). We mirror the spec's external observability
    /// contract by declaring a sibling <see cref="Meter"/> with the SAME name
    /// (<c>"Mithril.Legolas.Calibration"</c>) and the SAME instrument name
    /// (<c>"mithril.legolas.calibration.picker.outcomes"</c>) here. A
    /// <see cref="System.Diagnostics.Metrics.MeterListener"/> subscribing to either
    /// the producer-side or the Shared-side <see cref="Meter"/> instance with this
    /// name receives the picker measurements transparently.</para>
    /// </summary>
    public static class LegolasCalibrationPickerMeter
    {
        public static readonly Meter Meter = new("Mithril.Legolas.Calibration");

        /// <summary>Every <c>PickByFrame</c> call. Tags: <c>frame</c> ∈ {texture, overlay},
        /// <c>outcome</c> ∈ {hit, miss, fallback_below_floor}.</summary>
        public static readonly Counter<long> PickerOutcomes =
            Meter.CreateCounter<long>("mithril.legolas.calibration.picker.outcomes");
    }
}
