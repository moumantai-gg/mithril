using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;

namespace Legolas.Tests.Optimiser;

public class AdaptiveRouteOptimizerTests
{
    private readonly AdaptiveRouteOptimizer _optimiser;

    public AdaptiveRouteOptimizerTests()
    {
        _optimiser = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer())
        {
            ExactThreshold = 10
        };
    }

    [Fact]
    public void Uses_exact_solver_at_or_below_threshold()
    {
        var points = Enumerable.Range(0, 10).Select(i => new OverlayPixel(i, 0)).ToArray();
        var route = _optimiser.Optimize(OverlayPixel.Zero, points);
        route.Should().HaveCount(10);
    }

    [Fact]
    public void Uses_heuristic_above_threshold()
    {
        // 20 points — above our test-threshold of 10 and also HK's MaxPoints=18.
        var rng = new Random(Seed: 1);
        var points = Enumerable.Range(0, 20)
            .Select(_ => new OverlayPixel(rng.NextDouble() * 1000, rng.NextDouble() * 1000))
            .ToArray();

        var route = _optimiser.Optimize(OverlayPixel.Zero, points);

        route.Should().HaveCount(20);
        route.Should().OnlyHaveUniqueItems();
    }
}
