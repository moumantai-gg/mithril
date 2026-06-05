using System;
using System.Collections.Generic;
using Mithril.MapCalibration;

namespace Legolas.Domain;

/// <summary>
/// #495: builds the "Validate calibration" markers from an area's references,
/// projecting each through the persisted calibration and deciding which ones
/// get a visible label so dense areas don't turn into an unreadable text pile.
///
/// <para>Greedy by input order: the caller passes
/// <see cref="AreaCalibrationService"/>'s
/// <c>CurrentAreaReferences</c>, which already emits NPCs before landmarks —
/// so NPC labels (denser, more recognisable) win a crowding contest for free
/// without a second sort here. Every reference still yields a marker (the dot
/// always draws); only the label is suppressed when its box would overlap an
/// already-placed label.</para>
///
/// <para>Pure + WPF-free so it is unit-testable. The label box is estimated in
/// the overlay-pixel frame <see cref="WorldToOverlayCalibration.ToOverlay(WorldCoord)"/>
/// produces (the WPF layer positions items by that pixel directly). Declutter is by
/// mutual label overlap only — the overlay's on-screen pixel bounds aren't
/// known VM-side, so off-screen markers are still listed (harmless: the Canvas
/// just positions them out of view).</para>
/// </summary>
public static class GhostLabelDeclutter
{
    // FontSize-11 label box estimate, matching the XAML template
    // (Border Padding 3,1 + TextBlock anchored at +9,-9 from the dot).
    private const double LabelAnchorDx = 9.0;
    private const double LabelAnchorDy = -9.0;
    private const double AvgCharPx = 6.5;
    private const double LabelPadPx = 8.0;
    private const double LabelHeightPx = 16.0;
    // A little breathing room so labels that merely graze still declutter.
    private const double SeparationPx = 2.0;

    public static IReadOnlyList<GhostMarker> Build(
        IEnumerable<CalibrationReference> references,
        WorldToOverlayCalibration calibration,
        double currentMapZoom = 0.0)
    {
        ArgumentNullException.ThrowIfNull(references);

        // #524: thread the in-game map zoom through the projection so the
        // validate-calibration ghosts re-scale live as the user drags PG's
        // zoom slider (and the bound Mithril field). Unsupplied (<= 0) falls
        // back to the calibration's own zoom — byte-identical to pre-#524 for
        // legacy callers / tests.
        var effectiveZoom = currentMapZoom > 1e-6 ? currentMapZoom : calibration.CalibrationZoom;

        var markers = new List<GhostMarker>();
        var placed = new List<Rect>();

        foreach (var r in references)
        {
            // #1076 Phase 6.5: frame-typed projection — OverlayPixel out, no re-tag.
            var p = calibration.ToOverlay(r.World, effectiveZoom);
            var name = r.Name ?? string.Empty;
            var box = new Rect(
                p.X + LabelAnchorDx,
                p.Y + LabelAnchorDy,
                name.Length * AvgCharPx + LabelPadPx,
                LabelHeightPx);

            var show = true;
            foreach (var q in placed)
            {
                if (box.IntersectsWith(q, SeparationPx)) { show = false; break; }
            }
            if (show) placed.Add(box);

            markers.Add(new GhostMarker(name, p, show));
        }

        return markers;
    }

    private readonly record struct Rect(double X, double Y, double W, double H)
    {
        public bool IntersectsWith(Rect o, double margin) =>
            X - margin < o.X + o.W &&
            X + W + margin > o.X &&
            Y - margin < o.Y + o.H &&
            Y + H + margin > o.Y;
    }
}
