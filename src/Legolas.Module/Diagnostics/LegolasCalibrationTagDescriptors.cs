using System.Collections.Generic;
using Mithril.Shared.Telemetry.Abstractions;

namespace Legolas.Diagnostics;

/// <summary>
/// Declares tag keys emitted on spans (<c>MithrilActivitySources.LegolasCalibration</c>)
/// and metric instruments (<c>MithrilMeters.LegolasCalibration</c>) from the Legolas
/// calibration consumer chain (#1093). Co-located with the consumer-site call shapes
/// in <c>docs/planning/calibration-logging-pass-1093/spec.md</c>. Loaded into the
/// telemetry <c>TagCatalog</c> via DI from <c>LegolasModule.Register</c>.
/// </summary>
/// <remarks>
/// <para>
/// The catalog dedups <c>TagDescriptor</c> rows by <see cref="TagDescriptor.Key"/>
/// alone — see <c>Mithril.Shared.Telemetry.Catalog.TagCatalog</c>, which throws
/// on key conflicts where any field (classification, subsystem, description) differs.
/// Spec D11 ("every (key, scope) pair is its own row") is therefore not literally
/// realisable for keys already declared elsewhere; we declare each key once,
/// scoped to the new <c>Mithril.Legolas.Calibration</c> subsystem.
/// </para>
/// <para>
/// <b>Intentional omission — <c>outcome</c>.</b> Already declared at
/// <c>Mithril.Reference</c> (Safe) by <c>MithrilSharedTagDescriptors</c>. Re-declaring
/// it here would conflict on the Subsystem field. The Legolas use shares the same
/// classification (Safe) and the export decision is per-Key, so the existing row
/// covers both producers; the value vocabulary (hit | miss | fallback_below_floor)
/// is documented on the producer-side meter instrument and span call shapes
/// rather than the descriptor.
/// </para>
/// </remarks>
public sealed class LegolasCalibrationTagDescriptors : ITagDescriptorProvider
{
    private const string Subsystem = "Mithril.Legolas.Calibration";

    private static readonly TagDescriptor[] Descriptors =
    {
        new("area",                  PiiClassification.Safe, Subsystem, "Scene MapAssetKey on the consumer-side calibration span / counter (e.g. Map_AreaSerbule)."),
        new("scene.asset_key",       PiiClassification.Safe, Subsystem, "Scene MapAssetKey on AreaCalibrationService.SelectScene / CalibrateCurrentArea spans."),
        new("scene.parent_area_key", PiiClassification.Safe, Subsystem, "Parent area key when the active scene is a sub-scene (e.g. AreaCave1 for Map_HogansKeepBasement)."),
        new("cal.source",            PiiClassification.Safe, Subsystem, "Calibration record source: user | baseline | composed."),
        new("cal.frame",             PiiClassification.Safe, Subsystem, "Calibration frame: texture | overlay."),
        new("cal.residual_px",       PiiClassification.Safe, Subsystem, "Residual error in pixels for the picked calibration record."),
        new("cal.refs",              PiiClassification.Safe, Subsystem, "Reference-landmark count backing the picked calibration."),
        new("cal.path",              PiiClassification.Safe, Subsystem, "Projection path taken: direct_overlay | none (the composed-cal migration adds composed)."),
        new("consumer",              PiiClassification.Safe, Subsystem, "VM-side projection consumer: ghosts | motherlode_markers | motherlode_guidance | survey_pin | survey_anchor | wizard_landmarks."),
        new("frame",                 PiiClassification.Safe, Subsystem, "PickByFrame request frame: texture | overlay."),
        new("refs_count",            PiiClassification.Safe, Subsystem, "Reference-landmark count input to RebuildCalibrationGhosts."),
        new("ghosts_built",          PiiClassification.Safe, Subsystem, "Ghost-pin count produced by RebuildCalibrationGhosts."),
        new("from",                  PiiClassification.Safe, Subsystem, "Ghost-drawer prior bucket: hidden | empty | drawing | brush_null."),
        new("to",                    PiiClassification.Safe, Subsystem, "Ghost-drawer next bucket: hidden | empty | drawing | brush_null."),
        new("placements",            PiiClassification.Safe, Subsystem, "Placement count submitted to AreaCalibrationService.CalibrateCurrentArea."),
    };

    /// <inheritdoc />
    public IReadOnlyCollection<TagDescriptor> Describe() => Descriptors;
}
