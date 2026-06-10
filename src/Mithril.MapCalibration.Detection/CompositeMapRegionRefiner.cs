using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Diagnostics;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Two-stage <see cref="IMapRegionRefiner"/>: try the primary; if it returns
/// no <see cref="MapRegionRefineResult.AcceptedRect"/> (whether "no fit at all"
/// or "fit produced but gate rejected"), run the fallback and return its
/// result. The mithril#1061 dispatcher: primary = ORB+Lowe
/// (<see cref="FeatureMatchingRefiner"/>), fallback = Sobel-padded-pyramid
/// (<see cref="SobelPaddedPyramidRefiner"/>).
///
/// <para><b>Area-context forwarding.</b> Implements
/// <see cref="IAreaContextualRefiner"/> so the engine can call
/// <see cref="SetAreaKey"/> without knowing about the composition. The call
/// forwards to whichever inner refiners implement
/// <see cref="IAreaContextualRefiner"/> (currently only the FM primary uses
/// per-area state, but the contract symmetrically supports either branch).</para>
/// </summary>
public sealed class CompositeMapRegionRefiner : IMapRegionRefiner, IAreaContextualRefiner
{
    private readonly IMapRegionRefiner _primary;
    private readonly IMapRegionRefiner _fallback;
    private readonly ILogger? _logger;

    public CompositeMapRegionRefiner(
        IMapRegionRefiner primary,
        IMapRegionRefiner fallback,
        ILogger<CompositeMapRegionRefiner>? logger = null)
    {
        _primary = primary;
        _fallback = fallback;
        _logger = logger;
    }

    public MapRegionRefineResult Refine(GrayImage capturedGray, GrayImage baseTexture)
    {
        // mithril#1061: emit per-branch spans so Seq/OTLP can split "what fraction
        // of attempts hit fallback" without parsing logs. Spans are zero-cost when
        // no listener is attached (StartActivity returns null), so the producer
        // emits unconditionally per CLAUDE.md instrumentation convention.
        MapRegionRefineResult primary;
        using (var primaryAct = MapCalibrationDiagnostics.ActivitySource
            .StartActivity("calibration.refine.primary"))
        {
            primary = _primary.Refine(capturedGray, baseTexture);
            primaryAct?.SetTag("outcome",
                primary.AcceptedRect is not null ? "accepted"
                : primary.RawFitRect is not null ? "rejected"
                : "no_fit");
        }
        if (primary.AcceptedRect is not null) return primary;

        _logger?.LogInformation(
            "Composite locate: primary did not accept (raw fit {HasFit}); trying fallback.",
            primary.RawFitRect is not null);

        MapRegionRefineResult fallback;
        using (var fallbackAct = MapCalibrationDiagnostics.ActivitySource
            .StartActivity("calibration.refine.fallback"))
        {
            fallback = _fallback.Refine(capturedGray, baseTexture);
            if (fallback.Metrics is { } m)
            {
                if (m.Confidence is double ncc) fallbackAct?.SetTag("ncc", ncc);
                fallbackAct?.SetTag("scale", m.Scale);
                // mithril#1070: surface the σ applied at the final
                // matchTemplate so a Seq/OTLP triager can correlate fallback
                // NCC with the blur-aware template's σ.
                if (m.BlurAppliedSigma is double sigma) fallbackAct?.SetTag("blur.sigma", sigma);
            }
            // Outcome classifier derives from contract, not from the option default:
            // SobelPaddedPyramidRefiner only populates Confidence when it produced a
            // fit, and the ONLY confidence-populating reject path is the
            // FallbackNccFloor gate. So `AcceptedRect == null && Confidence != null`
            // is precisely the floor-rejected case — no literal threshold needed,
            // no drift when a user customises FallbackNccFloor.
            fallbackAct?.SetTag("outcome",
                fallback.AcceptedRect is not null ? "accepted"
                : fallback.Metrics?.Confidence is not null ? "rejected_low_confidence"
                : fallback.RawFitRect is not null ? "rejected"
                : "no_fit");
        }
        return fallback;
    }

    public void SetAreaKey(string? areaKey)
    {
        if (_primary is IAreaContextualRefiner p) p.SetAreaKey(areaKey);
        if (_fallback is IAreaContextualRefiner f) f.SetAreaKey(areaKey);
    }
}
