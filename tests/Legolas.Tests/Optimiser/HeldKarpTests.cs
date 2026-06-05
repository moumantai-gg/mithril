using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;

namespace Legolas.Tests.Optimiser;

public class HeldKarpTests
{
    private readonly HeldKarpOptimizer _optimiser = new();

    [Fact]
    public void Empty_input_returns_empty_route()
    {
        var route = _optimiser.Optimize(OverlayPixel.Zero, Array.Empty<OverlayPixel>());
        route.Should().BeEmpty();
    }

    [Fact]
    public void Single_point_returns_zero()
    {
        var route = _optimiser.Optimize(OverlayPixel.Zero, new[] { new OverlayPixel(3, 4) });
        route.Should().Equal(0);
    }

    [Fact]
    public void Finds_optimal_path_for_collinear_points()
    {
        // Start at origin; points along the x-axis should be visited in order of distance.
        var points = new[]
        {
            new OverlayPixel(3, 0),
            new OverlayPixel(1, 0),
            new OverlayPixel(5, 0),
            new OverlayPixel(2, 0),
        };

        var route = _optimiser.Optimize(OverlayPixel.Zero, points);

        // Expected order (by distance from origin along x): points[1]=1, points[3]=2, points[0]=3, points[2]=5
        route.Should().Equal(1, 3, 0, 2);
    }

    [Fact]
    public void Throws_when_exceeding_max_points()
    {
        var points = Enumerable.Range(0, HeldKarpOptimizer.MaxPoints + 1)
            .Select(i => new OverlayPixel(i, 0))
            .ToArray();

        var act = () => _optimiser.Optimize(OverlayPixel.Zero, points);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Matches_brute_force_optimum_at_small_n()
    {
        // n = 5 → 120 permutations; easy to brute force as a check
        var rng = new Random(Seed: 42);
        var points = Enumerable.Range(0, 5)
            .Select(_ => new OverlayPixel(rng.NextDouble() * 100, rng.NextDouble() * 100))
            .ToArray();
        var start = new OverlayPixel(50, 50);

        var hkRoute = _optimiser.Optimize(start, points);
        var hkLength = PathLength(start, points, hkRoute);
        var bruteLength = BruteForce(start, points);

        hkLength.Should().BeApproximately(bruteLength, 1e-6);
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

    private static double BruteForce(OverlayPixel start, IReadOnlyList<OverlayPixel> points)
    {
        var indices = Enumerable.Range(0, points.Count).ToArray();
        var best = double.PositiveInfinity;
        foreach (var perm in Permutations(indices))
        {
            var length = PathLength(start, points, perm);
            if (length < best) best = length;
        }
        return best;
    }

    private static IEnumerable<int[]> Permutations(int[] source)
    {
        if (source.Length == 0)
        {
            yield return Array.Empty<int>();
            yield break;
        }
        if (source.Length == 1)
        {
            yield return source;
            yield break;
        }
        for (var i = 0; i < source.Length; i++)
        {
            var rest = source.Take(i).Concat(source.Skip(i + 1)).ToArray();
            foreach (var sub in Permutations(rest))
            {
                var result = new int[source.Length];
                result[0] = source[i];
                Array.Copy(sub, 0, result, 1, sub.Length);
                yield return result;
            }
        }
    }
}
