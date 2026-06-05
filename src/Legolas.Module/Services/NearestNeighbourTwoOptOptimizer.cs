using Legolas.Domain;

namespace Legolas.Services;

/// <summary>
/// Heuristic open-path TSP: multi-start nearest-neighbour construction followed by
/// 2-opt local search and a single-node Or-opt pass. Cheap and good at small n.
/// </summary>
public sealed class NearestNeighbourTwoOptOptimizer : IRouteOptimizer
{
    public IReadOnlyList<int> Optimize(
        OverlayPixel start,
        IReadOnlyList<OverlayPixel> points,
        CancellationToken cancellationToken = default)
    {
        var n = points.Count;
        if (n == 0) return Array.Empty<int>();
        if (n == 1) return new[] { 0 };

        var dist = RouteDistance.BuildMatrix(start, points);

        // Always start NN from node 0 (the fixed origin). Multi-start seeding
        // from other nodes doesn't apply to a fixed-start open path.
        var route = NearestNeighbour(dist, n);
        route = TwoOpt(dist, route, cancellationToken);
        route = OrOpt(dist, route, cancellationToken);

        return route;
    }

    private static int[] NearestNeighbour(double[,] dist, int n)
    {
        var route = new int[n];
        var visited = new bool[n + 1];
        visited[0] = true;
        var current = 0;
        for (var step = 0; step < n; step++)
        {
            var best = -1;
            var bestDist = double.PositiveInfinity;
            for (var j = 1; j <= n; j++)
            {
                if (visited[j]) continue;
                // squared distance comparison would skip a sqrt per call, but since
                // BuildMatrix already holds real distances, reuse them directly.
                var d = dist[current, j];
                if (d < bestDist)
                {
                    bestDist = d;
                    best = j;
                }
            }
            route[step] = best;
            visited[best] = true;
            current = best;
        }
        // Convert from DP node indices (1..n) to point indices (0..n-1)
        for (var i = 0; i < n; i++) route[i] -= 1;
        return route;
    }

    private static int[] TwoOpt(double[,] dist, int[] route, CancellationToken ct)
    {
        var n = route.Length;
        if (n < 3) return route;

        // sequence[k] holds DP-node index; sequence[0] = start (0).
        var sequence = new int[n + 1];
        sequence[0] = 0;
        for (var i = 0; i < n; i++) sequence[i + 1] = route[i] + 1;

        var improved = true;
        while (improved)
        {
            improved = false;
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i < sequence.Length - 2; i++)
            {
                for (var j = i + 2; j < sequence.Length - 1; j++)
                {
                    var a = sequence[i];
                    var b = sequence[i + 1];
                    var c = sequence[j];
                    var d = sequence[j + 1];
                    var oldCost = dist[a, b] + dist[c, d];
                    var newCost = dist[a, c] + dist[b, d];
                    if (newCost < oldCost - 1e-9)
                    {
                        Array.Reverse(sequence, i + 1, j - i);
                        improved = true;
                    }
                }
            }
        }

        var result = new int[n];
        for (var i = 0; i < n; i++) result[i] = sequence[i + 1] - 1;
        return result;
    }

    private static int[] OrOpt(double[,] dist, int[] route, CancellationToken ct)
    {
        var n = route.Length;
        if (n < 3) return route;

        var sequence = new List<int>(n + 1) { 0 };
        for (var i = 0; i < n; i++) sequence.Add(route[i] + 1);

        var improved = true;
        while (improved)
        {
            improved = false;
            ct.ThrowIfCancellationRequested();
            for (var i = 1; i < sequence.Count && !improved; i++)
            {
                var prev = sequence[i - 1];
                var node = sequence[i];
                var hasNext = i + 1 < sequence.Count;
                var next = hasNext ? sequence[i + 1] : -1;
                var removalGain = dist[prev, node]
                    + (hasNext ? dist[node, next] - dist[prev, next] : 0);

                for (var j = 0; j < sequence.Count - 1; j++)
                {
                    if (j == i - 1 || j == i) continue;
                    var a = sequence[j];
                    var b = sequence[j + 1];
                    var insertCost = dist[a, node] + dist[node, b] - dist[a, b];
                    if (insertCost < removalGain - 1e-9)
                    {
                        sequence.RemoveAt(i);
                        var insertAt = j < i ? j + 1 : j;
                        sequence.Insert(insertAt, node);
                        improved = true;
                        break;
                    }
                }
            }
        }

        var result = new int[n];
        for (var i = 0; i < n; i++) result[i] = sequence[i + 1] - 1;
        return result;
    }
}
