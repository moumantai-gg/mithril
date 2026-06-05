using Legolas.Domain;

namespace Legolas.Services;

internal static class RouteDistance
{
    public static double[,] BuildMatrix(OverlayPixel start, IReadOnlyList<OverlayPixel> points)
    {
        var n = points.Count + 1;
        var m = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            var pi = i == 0 ? start : points[i - 1];
            for (var j = 0; j < n; j++)
            {
                if (i == j)
                {
                    m[i, j] = 0;
                    continue;
                }
                var pj = j == 0 ? start : points[j - 1];
                m[i, j] = pi.DistanceTo(pj);
            }
        }
        return m;
    }

    public static double PathLength(double[,] dist, IReadOnlyList<int> route)
    {
        if (route.Count == 0) return 0;
        var total = dist[0, route[0] + 1];
        for (var i = 1; i < route.Count; i++)
        {
            total += dist[route[i - 1] + 1, route[i] + 1];
        }
        return total;
    }
}
