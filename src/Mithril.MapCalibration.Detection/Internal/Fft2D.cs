using System;
using System.Numerics;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Iterative Cooley–Tukey 2-D FFT over power-of-two complex grids. BCL-only;
/// keeps the calibration library's dependency surface flat. Used by
/// <see cref="CrossCorrelationMapViewProbe"/> for FFT-accelerated
/// cross-correlation (phase-correlation via element-wise spectrum product).
/// </summary>
internal static class Fft2D
{
    /// <summary>Round <paramref name="n"/> up to the next power of two (≥ 1).</summary>
    public static int NextPow2(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    /// <summary>In-place forward FFT over a <paramref name="rows"/>×<paramref name="cols"/>
    /// complex grid (both dimensions must be powers of two).</summary>
    public static void Forward(Complex[] grid, int rows, int cols)
        => Transform(grid, rows, cols, inverse: false);

    /// <summary>In-place inverse FFT. Normalises by 1/(rows*cols).</summary>
    public static void Inverse(Complex[] grid, int rows, int cols)
    {
        Transform(grid, rows, cols, inverse: true);
        double inv = 1.0 / (rows * cols);
        for (int i = 0; i < grid.Length; i++) grid[i] *= inv;
    }

    private static void Transform(Complex[] grid, int rows, int cols, bool inverse)
    {
        // Row-wise then column-wise 1-D FFTs.
        var rowBuf = new Complex[cols];
        for (int r = 0; r < rows; r++)
        {
            Array.Copy(grid, r * cols, rowBuf, 0, cols);
            Fft1D(rowBuf, inverse);
            Array.Copy(rowBuf, 0, grid, r * cols, cols);
        }
        var colBuf = new Complex[rows];
        for (int c = 0; c < cols; c++)
        {
            for (int r = 0; r < rows; r++) colBuf[r] = grid[r * cols + c];
            Fft1D(colBuf, inverse);
            for (int r = 0; r < rows; r++) grid[r * cols + c] = colBuf[r];
        }
    }

    private static void Fft1D(Complex[] x, bool inverse)
    {
        int n = x.Length;
        // Bit-reverse permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (x[i], x[j]) = (x[j], x[i]);
        }
        // Cooley–Tukey butterflies.
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = (inverse ? 2 : -2) * Math.PI / len;
            var wLen = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = x[i + k];
                    var v = x[i + k + len / 2] * w;
                    x[i + k] = u + v;
                    x[i + k + len / 2] = u - v;
                    w *= wLen;
                }
            }
        }
    }
}
