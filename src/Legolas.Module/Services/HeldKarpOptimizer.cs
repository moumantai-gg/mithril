using Legolas.Domain;

namespace Legolas.Services;

/// <summary>
/// Exact open-path TSP via Held–Karp dynamic programming. O(n^2 · 2^n).
/// Node 0 in the DP is the fixed start; nodes 1..n correspond to <c>points</c>.
/// </summary>
public sealed class HeldKarpOptimizer : IRouteOptimizer
{
    public const int MaxPoints = 18;

    public IReadOnlyList<int> Optimize(
        OverlayPixel start,
        IReadOnlyList<OverlayPixel> points,
        CancellationToken cancellationToken = default)
    {
        var n = points.Count;
        if (n == 0) return Array.Empty<int>();
        if (n == 1) return new[] { 0 };
        if (n > MaxPoints)
        {
            throw new ArgumentException(
                $"{nameof(HeldKarpOptimizer)} supports at most {MaxPoints} points; got {n}.",
                nameof(points));
        }

        var dist = RouteDistance.BuildMatrix(start, points);
        var totalNodes = n + 1;
        var stateCount = 1 << totalNodes;

        // dp[mask, last]: min cost visiting subset <mask> ending at <last>.
        // mask always contains bit 0 (the start).
        var dp = new double[stateCount, totalNodes];
        var parent = new int[stateCount, totalNodes];
        for (var i = 0; i < stateCount; i++)
        {
            for (var j = 0; j < totalNodes; j++)
            {
                dp[i, j] = double.PositiveInfinity;
                parent[i, j] = -1;
            }
        }

        dp[1, 0] = 0;

        for (var mask = 1; mask < stateCount; mask++)
        {
            if ((mask & 1) == 0) continue; // start must always be in the visited set
            if ((mask & 0x3FF) == 0) cancellationToken.ThrowIfCancellationRequested();

            for (var last = 0; last < totalNodes; last++)
            {
                if ((mask & (1 << last)) == 0) continue;
                var currentCost = dp[mask, last];
                if (double.IsPositiveInfinity(currentCost)) continue;

                for (var next = 1; next < totalNodes; next++)
                {
                    if ((mask & (1 << next)) != 0) continue;
                    var newMask = mask | (1 << next);
                    var candidate = currentCost + dist[last, next];
                    if (candidate < dp[newMask, next])
                    {
                        dp[newMask, next] = candidate;
                        parent[newMask, next] = last;
                    }
                }
            }
        }

        var fullMask = stateCount - 1;
        var bestEnd = -1;
        var bestCost = double.PositiveInfinity;
        for (var k = 1; k < totalNodes; k++)
        {
            if (dp[fullMask, k] < bestCost)
            {
                bestCost = dp[fullMask, k];
                bestEnd = k;
            }
        }

        var path = new List<int>(n);
        var curMask = fullMask;
        var cur = bestEnd;
        while (cur > 0)
        {
            path.Add(cur - 1); // convert DP node index back to points index
            var prev = parent[curMask, cur];
            curMask ^= 1 << cur;
            cur = prev;
        }
        path.Reverse();
        return path;
    }
}
