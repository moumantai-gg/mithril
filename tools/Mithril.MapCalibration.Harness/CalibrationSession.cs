using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibration.Common;

namespace Mithril.Tools.MapCalibration.Harness;

/// <summary>
/// The headless calibration engine: owns the live ref set, the solved
/// <see cref="Calibration"/>, and the projection overlay. <see cref="ReSolve"/>,
/// the per-ref residual math, and the projection refresh are lifted from the
/// merged <c>AreaWorkspaceViewModel</c> and made headless so they're unit-tested
/// for the first time. The solve goes through
/// <see cref="LandmarkCalibrationSolver.Solve"/> unchanged (it self-corrects
/// handedness by trying both reflections — don't bypass it).
///
/// <para>The session is reactive at the ref granularity: mutating a ref's
/// solve-input properties (<see cref="CalibrationRef.Enabled"/>,
/// <see cref="CalibrationRef.World"/>, <see cref="CalibrationRef.TexturePixel"/>)
/// auto-re-solves, so the spec's core composition UX ("disable a suspect ref,
/// watch the residual") works by direct mutation — no explicit
/// <see cref="ReSolve"/> call needed. This restores the lifted source's
/// <c>Refs.CollectionChanged → ReSolve</c> wiring at finer granularity.</para>
///
/// <para>This type also doubles as the <see cref="ICandidateSink"/> a method
/// emits into: <see cref="ICandidateSink.Emit"/> routes through
/// <see cref="Accept"/> and <see cref="ICandidateSink.EmitBatch"/> through the
/// single-solve batch path.</para>
/// </summary>
public sealed partial class CalibrationSession : ObservableObject, ICandidateSink
{
    private readonly CalibrationContext _ctx;

    // Set while ReSolve writes back per-ref ResidualPx / Calibration. Those
    // writes raise PropertyChanged, which would otherwise re-enter ReSolve; the
    // guard breaks that feedback loop. (ResidualPx is also not a solve input, so
    // the property filter in OnRefPropertyChanged already ignores it — the guard
    // is belt-and-braces against any future write-back that touches an input.)
    private bool _suppressReSolve;

    public CalibrationSession(CalibrationContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        Area = ctx.Area;
    }

    public string Area { get; }

    public ObservableCollection<CalibrationRef> Refs { get; } = new();

    [ObservableProperty]
    private AreaCalibration? _calibration;

    public ObservableCollection<ProjectionMarker> Projections { get; } = new();

    /// <summary>
    /// Materialises a single <see cref="CalibrationRef"/> from a
    /// <see cref="CandidateRef"/> and re-solves once. (For an N-candidate batch
    /// use <see cref="ICandidateSink.EmitBatch"/>, which adds all refs then
    /// solves once.) A candidate with no <see cref="CandidateRef.World"/> is
    /// added as a disabled ref (it carries no world coord, so it can't yet
    /// participate in the fit — the user assigns its world position later, then
    /// enables it).
    /// </summary>
    public void Accept(CandidateRef candidate)
    {
        AddRef(candidate);
        ReSolve();
    }

    /// <summary>
    /// Materialises a ref from a candidate and registers it WITHOUT re-solving.
    /// Shared by <see cref="Accept"/> (which adds one then re-solves) and the
    /// batch path (which adds many then re-solves once).
    /// </summary>
    private void AddRef(CandidateRef candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var hasWorld = candidate.World is not null;
        var calRef = new CalibrationRef
        {
            Name = candidate.SuggestedName ?? candidate.LandmarkId ?? "(unnamed)",
            Kind = candidate.Kind,
            Source = candidate.Source,
            Confidence = candidate.Confidence,
            // (0,0,0) sentinel is load-bearing only while the ref is disabled: a
            // World-null candidate is added disabled, so the sentinel never feeds
            // the solve. The disabled-then-named flow (issue B) is "assign World,
            // set Enabled=true" — both are solve inputs, so that auto-re-solves.
            World = candidate.World ?? default,
            TexturePixel = candidate.TexturePixel,
            Enabled = hasWorld,
        };
        calRef.PropertyChanged += OnRefPropertyChanged;
        Refs.Add(calRef);
    }

    /// <summary>Removes a ref from the live set and re-solves.</summary>
    public void Remove(CalibrationRef r)
    {
        if (r is null) return;
        if (Refs.Remove(r))
        {
            r.PropertyChanged -= OnRefPropertyChanged;
            ReSolve();
        }
    }

    /// <summary>
    /// Correction primitive: nudges a ref's <see cref="CalibrationRef.TexturePixel"/>
    /// by (<paramref name="dx"/>, <paramref name="dy"/>). The mutation itself
    /// triggers an automatic re-solve via the ref's <c>PropertyChanged</c>.
    /// </summary>
    public void NudgeSelected(CalibrationRef r, double dx, double dy)
    {
        if (r is null || !Refs.Contains(r)) return;
        r.TexturePixel = new TexturePixel(r.TexturePixel.X + dx, r.TexturePixel.Y + dy);
    }

    // Re-solve only when a solve-input property changes. ResidualPx is an OUTPUT
    // (written by ReSolve) so it's deliberately excluded — re-solving on it would
    // be a feedback loop. Name/Kind/Source/Confidence are immutable init-only.
    private void OnRefPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressReSolve) return;
        if (e.PropertyName is nameof(CalibrationRef.Enabled)
            or nameof(CalibrationRef.World)
            or nameof(CalibrationRef.TexturePixel))
        {
            ReSolve();
        }
    }

    /// <summary>
    /// Solves the calibration from the <b>enabled</b> refs only, fills each
    /// enabled ref's <see cref="CalibrationRef.ResidualPx"/>, and refreshes the
    /// projection overlay for every landmark in the context. With fewer than two
    /// enabled refs the calibration is null, the projection overlay is cleared,
    /// and all residuals are nulled. Lifted from <c>AreaWorkspaceViewModel.ReSolve</c>.
    /// </summary>
    public void ReSolve()
    {
        var enabled = Refs.Where(r => r.Enabled).ToList();
        if (enabled.Count < 2)
        {
            Calibration = null;
            SetResidualsNull();
            Projections.Clear();
            return;
        }

        var solverRefs = enabled
            .Select(r => new LandmarkCalibrationSolver.Reference(
                r.World.X, r.World.Z, r.TexturePixel.X, r.TexturePixel.Y))
            .ToList();
        var cal = LandmarkCalibrationSolver.Solve(solverRefs);
        Calibration = cal;

        if (cal is null)
        {
            SetResidualsNull();
            Projections.Clear();
            return;
        }

        // Suppress the re-solve feedback loop while writing per-ref residuals
        // (each write raises PropertyChanged).
        var prior = _suppressReSolve;
        _suppressReSolve = true;
        try
        {
            // #1076 Phase 7.5: project through a texture-frame view of the solved
            // calibration so the pixel comparison stays frame-explicit (the
            // harness solves in texture-pixel space — refs and projections are
            // both texture-frame). AsTexture is a field-identity re-tag of the
            // same similarity transform.
            var calTex = AsTexture(cal);
            // Disabled refs carry no residual; enabled refs get the projected error.
            foreach (var r in Refs)
            {
                if (!r.Enabled)
                {
                    r.ResidualPx = null;
                    continue;
                }
                var projected = calTex.ToTexture(r.World);
                var dx = projected.X - r.TexturePixel.X;
                var dy = projected.Y - r.TexturePixel.Y;
                r.ResidualPx = Math.Sqrt(dx * dx + dy * dy);
            }
        }
        finally
        {
            _suppressReSolve = prior;
        }

        RefreshProjections(cal);
    }

    private void SetResidualsNull()
    {
        var prior = _suppressReSolve;
        _suppressReSolve = true;
        try
        {
            foreach (var r in Refs) r.ResidualPx = null;
        }
        finally
        {
            _suppressReSolve = prior;
        }
    }

    private void RefreshProjections(AreaCalibration cal)
    {
        Projections.Clear();
        var calTex = AsTexture(cal);
        foreach (var landmark in _ctx.Landmarks)
        {
            var px = calTex.ToTexture(landmark.World);
            var refMatch = RefForLandmark(landmark);
            Projections.Add(new ProjectionMarker(
                landmark.Name, landmark.Type, px, refMatch?.ResidualPx));
        }
    }

    // #1076 Phase 7.5: field-identity re-tag of an AreaCalibration to its
    // texture-frame view. The harness solves calibrations in texture-pixel
    // space (refs and ProjectionMarker positions are both texture-frame), so
    // projecting through a frame-typed struct keeps the pixel comparison
    // explicit instead of going through the deleted PixelPoint surface.
    private static WorldToTextureCalibration AsTexture(AreaCalibration cal) =>
        new(cal.OriginX, cal.OriginY, cal.Scale, cal.RotationRadians, cal.MirrorNorth);

    // Prefer the enabled ref at this coord; disabled refs carry no residual, so
    // a disabled ref must not supply a projection marker's ResidualPx. (The
    // lifted source had no Enabled concept; the r.Enabled filter is the correct
    // adaptation, not an accidental deviation.)
    private CalibrationRef? RefForLandmark(LandmarkRef landmark) =>
        Refs.FirstOrDefault(r =>
            r.Enabled
            && Math.Abs(r.World.X - landmark.World.X) < 1e-6
            && Math.Abs(r.World.Z - landmark.World.Z) < 1e-6);

    void ICandidateSink.Emit(CandidateRef candidate) => Accept(candidate);

    /// <summary>
    /// Adds every candidate as a ref and re-solves <b>once</b> (not once per
    /// candidate) — an N-candidate scan triggers a single solve + projection
    /// rebuild, unlike calling <see cref="Accept"/> N times. The final
    /// calibration is identical to the equivalent sequence of <see cref="Accept"/>
    /// calls.
    /// </summary>
    void ICandidateSink.EmitBatch(IEnumerable<CandidateRef> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var any = false;
        foreach (var c in candidates)
        {
            AddRef(c);
            any = true;
        }
        if (any) ReSolve();
    }
}
