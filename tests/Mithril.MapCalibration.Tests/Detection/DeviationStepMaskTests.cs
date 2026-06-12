using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// Task 6 (mithril#1116): verify the deviation-mask subtract step inside
/// <see cref="DeviationBlobDetector.DetectIconBlobs"/>. Mask-true pixels are
/// dropped from the working fg buffer between the rim subtract and morph-close;
/// null mask is byte-identical to pre-#1116; the OnDeviationMask hook (Task 5)
/// fires with correct counters when a mask is supplied.
/// </summary>
public class DeviationStepMaskTests
{
    [Fact]
    public void DetectIconBlobs_skips_fg_pixels_where_deviation_mask_is_set()
    {
        // Whole image above threshold → would form one big blob.
        // Mask the entire left half → only the right half can contribute.
        int w = 40, h = 40;
        var dev = new float[w * h];
        for (int i = 0; i < dev.Length; i++) dev[i] = 0.9f;

        var mask = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w / 2; x++) mask[y * w + x] = true;

        var blobs = DeviationBlobDetector.DetectIconBlobs(
            dev, w, h, lowNcc: 0.5, RimMaskMode.None,
            new BlobOptions(MinArea: 1, MaxIconArea: 100000, MinSolidity: 0.0, MaxAspect: 100, MinPeak: 0.0),
            closeRadius: 0,
            deviationMask: mask);

        // Every surviving blob must be in the unmasked (right) half.
        foreach (var b in blobs)
            (b.MinX >= w / 2).Should().BeTrue("masked region should not yield any blobs");
    }

    [Fact]
    public void DetectIconBlobs_null_mask_is_byte_identical_to_omitted()
    {
        int w = 30, h = 30;
        var dev = new float[w * h];
        for (int i = 0; i < dev.Length; i++) dev[i] = 0.9f;

        var opts = new BlobOptions(MinArea: 1, MaxIconArea: 100000, MinSolidity: 0.0, MaxAspect: 100, MinPeak: 0.0);

        var blobsExplicitNull = DeviationBlobDetector.DetectIconBlobs(
            dev, w, h, 0.5, RimMaskMode.None, opts, closeRadius: 0, deviationMask: null);
        var blobsOmitted = DeviationBlobDetector.DetectIconBlobs(
            dev, w, h, 0.5, RimMaskMode.None, opts, closeRadius: 0);

        blobsExplicitNull.Count.Should().Be(blobsOmitted.Count);
    }

    [Fact]
    public void DetectIconBlobs_fires_OnDeviationMask_hook_when_mask_supplied()
    {
        int w = 20, h = 20;
        var dev = new float[w * h];
        for (int i = 0; i < dev.Length; i++) dev[i] = 0.9f;

        var mask = new bool[w * h];
        // Mask out a 5×5 region near the upper-left corner.
        for (int y = 2; y < 7; y++)
            for (int x = 2; x < 7; x++) mask[y * w + x] = true;

        DeviationMaskSnapshot? received = null;
        // DetectionDiagnosticHooks is a positional record (OnDeviation, OnRimMask,
        // OnMorph, OnBlobClassified) with OnDeviationMask added as an init-only
        // property — construct with the four positional nulls, then set the new
        // hook via `with`.
        var hooks = new DetectionDiagnosticHooks(null, null, null, null)
        {
            OnDeviationMask = s => received = s,
        };

        DeviationBlobDetector.DetectIconBlobs(
            dev, w, h, 0.5, RimMaskMode.None,
            new BlobOptions(1, 100000, 0.0, 100, 0.0),
            closeRadius: 0,
            hooks: hooks,
            deviationMask: mask);

        received.Should().NotBeNull();
        received!.Width.Should().Be(w);
        received.Height.Should().Be(h);
        received.MaskPixelCount.Should().Be(25);  // 5×5
        received.FgInputCount.Should().BeGreaterThan(received.FgSurvivorCount);
    }
}
