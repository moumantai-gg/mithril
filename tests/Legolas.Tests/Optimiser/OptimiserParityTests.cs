using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;

namespace Legolas.Tests.Optimiser;

/// <summary>
/// Uses Held-Karp (exact) as an oracle for the NN+2-opt heuristic. Guarantees
/// the heuristic stays within a tight ratio of optimal across random layouts.
/// </summary>
public class OptimiserParityTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(15)]
    public void Heuristic_tracks_optimum_closely(int n)
    {
        var exact = new HeldKarpOptimizer();
        var heuristic = new NearestNeighbourTwoOptOptimizer();
        var rng = new Random(Seed: 20260414);

        const int trials = 50;
        var ratios = new List<double>(trials);

        for (var trial = 0; trial < trials; trial++)
        {
            var start = new OverlayPixel(rng.NextDouble() * 200, rng.NextDouble() * 200);
            var points = Enumerable.Range(0, n)
                .Select(_ => new OverlayPixel(rng.NextDouble() * 1000, rng.NextDouble() * 1000))
                .ToArray();

            var exactRoute = exact.Optimize(start, points);
            var heuristicRoute = heuristic.Optimize(start, points);

            var exactLength = PathLength(start, points, exactRoute);
            var heuristicLength = PathLength(start, points, heuristicRoute);

            ratios.Add(heuristicLength / exactLength);
        }

        // In practice the adaptive dispatcher sends every n <= 15 to Held-Karp, so
        // the heuristic only handles n > 15 where exact DP becomes expensive.
        // Average case is the meaningful quality gate; worst-case on adversarial
        // layouts can be substantially worse and that's accepted.
        var average = ratios.Average();
        var worst = ratios.Max();

        average.Should().BeLessThanOrEqualTo(1.05,
            because: "average heuristic ratio should stay tight on uniform random inputs");
        worst.Should().BeLessThanOrEqualTo(1.30,
            because: "worst-case NN+2-opt+Or-opt ratio guards against catastrophic regressions");
    }

    private static double PathLength(OverlayPixel start, IReadOnlyList<OverlayPixel> points, IReadOnlyList<int> route)
    {
        if (route.Count == 0) return 0;
        var length = start.DistanceTo(points[route[0]]);
        for (var i = 1; i < route.Count; i++)
        {
            length += points[route[i - 1]].DistanceTo(points[route[i]]);
        }
        return length;
    }
}
