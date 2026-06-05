using Legolas.Domain;

namespace Legolas.Services;

/// <summary>
/// Dispatches to <see cref="HeldKarpOptimizer"/> when the input fits within
/// <see cref="ExactThreshold"/>, falling back to <see cref="NearestNeighbourTwoOptOptimizer"/>
/// for larger inputs.
/// </summary>
public sealed class AdaptiveRouteOptimizer : IRouteOptimizer
{
    private readonly HeldKarpOptimizer _exact;
    private readonly NearestNeighbourTwoOptOptimizer _heuristic;

    public AdaptiveRouteOptimizer(
        HeldKarpOptimizer exact,
        NearestNeighbourTwoOptOptimizer heuristic)
    {
        _exact = exact ?? throw new ArgumentNullException(nameof(exact));
        _heuristic = heuristic ?? throw new ArgumentNullException(nameof(heuristic));
    }

    public int ExactThreshold { get; init; } = 15;

    public IReadOnlyList<int> Optimize(
        OverlayPixel start,
        IReadOnlyList<OverlayPixel> points,
        CancellationToken cancellationToken = default)
    {
        return points.Count <= ExactThreshold
            ? _exact.Optimize(start, points, cancellationToken)
            : _heuristic.Optimize(start, points, cancellationToken);
    }
}
