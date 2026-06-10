namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// σ(scale) curve for the mithril#1070 blur-aware Sobel template path in the
/// fallback's full-resolution stage. Linear in 1/scale with floor + ceiling
/// clamps; coefficients persist on <see cref="MapCalibrationLocateOptions"/>
/// so the curve is tunable without recompile.
///
/// <para>The production coefficients come from the spec's Plan Task 0
/// measurement experiment — a one-time σ-vs-scale fit on the 5-bundle
/// Hogan's corpus. See the σ-curve property docs on
/// <see cref="MapCalibrationLocateOptions"/> for the fitted values and the
/// rationale behind the floor clamp at 0.</para>
///
/// <para>Cost: pure scalar arithmetic, no allocation. Called once per
/// fine-ladder rung evaluated at the full stage (~5 calls per refine
/// attempt). Producer-cost: zero when <see cref="MapCalibrationLocateOptions.RendererBlurEnabled"/>
/// is <c>false</c>.</para>
/// </summary>
internal static class RendererBlurModel
{
    /// <summary>
    /// σ to apply to a template that's been resized to <paramref name="scale"/>×
    /// native. Returns 0 when blur is disabled (early-out short-circuits the
    /// per-rung <c>Cv2.GaussianBlur</c> call). Guards against the <c>1 / scale</c>
    /// blow-up by returning <see cref="MapCalibrationLocateOptions.RendererBlurMinSigma"/>
    /// at <c>scale &lt;= 0</c>. Clamps to
    /// [<see cref="MapCalibrationLocateOptions.RendererBlurMinSigma"/>,
    /// <see cref="MapCalibrationLocateOptions.RendererBlurMaxSigma"/>].
    /// </summary>
    public static double SigmaFor(double scale, MapCalibrationLocateOptions options)
    {
        if (!options.RendererBlurEnabled) return 0.0;
        if (scale <= 0.0) return options.RendererBlurMinSigma;
        double raw = options.RendererBlurIntercept + options.RendererBlurSlope * (1.0 / scale);
        if (raw < options.RendererBlurMinSigma) return options.RendererBlurMinSigma;
        if (raw > options.RendererBlurMaxSigma) return options.RendererBlurMaxSigma;
        return raw;
    }
}
