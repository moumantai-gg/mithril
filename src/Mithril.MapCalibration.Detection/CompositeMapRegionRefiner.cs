using Microsoft.Extensions.Logging;

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
        var primary = _primary.Refine(capturedGray, baseTexture);
        if (primary.AcceptedRect is not null) return primary;

        _logger?.LogInformation(
            "Composite locate: primary did not accept (raw fit {HasFit}); trying fallback.",
            primary.RawFitRect is not null);
        return _fallback.Refine(capturedGray, baseTexture);
    }

    public void SetAreaKey(string? areaKey)
    {
        if (_primary is IAreaContextualRefiner p) p.SetAreaKey(areaKey);
        if (_fallback is IAreaContextualRefiner f) f.SetAreaKey(areaKey);
    }
}
