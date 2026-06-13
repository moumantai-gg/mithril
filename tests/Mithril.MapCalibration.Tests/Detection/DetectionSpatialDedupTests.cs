using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// mithril#1154: spatial-dedup contract for typed detections. Adjacent blobs
/// whose template-match crops overlap can pivot-correct to byte-identical
/// (or sub-pixel-equivalent) anchors; the solver's "≥4 inliers" gate then
/// silently double-counts the duplicate. Dedup must collapse anchors within
/// epsilon px to one survivor — the highest-score detection wins — and
/// preserve original insertion order of the survivors so the downstream
/// solver's iteration is deterministic.
/// </summary>
public sealed class DetectionSpatialDedupTests
{
    private static TypedDetection Det(string type, string icon, double x, double y, double score) =>
        new(type, icon, new CroppedFramePixel(x, y), score);

    [Fact]
    public void Empty_input_passes_through()
    {
        var result = DetectionSpatialDedup.Dedupe([], epsilon: 16);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Single_item_passes_through()
    {
        var only = Det("Portal", "landmark_portal", 10, 10, 0.8);
        var result = DetectionSpatialDedup.Dedupe([only], epsilon: 16);
        result.Should().ContainSingle().Which.Should().Be(only);
    }

    [Fact]
    public void Byte_identical_anchors_keeps_highest_score()
    {
        var low = Det("Portal", "landmark_portal", 10, 10, 0.7);
        var high = Det("Portal", "landmark_portal", 10, 10, 0.9);

        var result = DetectionSpatialDedup.Dedupe([low, high], epsilon: 16);

        result.Should().ContainSingle().Which.Should().Be(high);
    }

    [Fact]
    public void Within_epsilon_keeps_highest_score()
    {
        var a = Det("Portal", "landmark_portal", 10, 10, 0.6);
        var b = Det("Portal", "landmark_portal", 12, 11, 0.95);  // ~2.24 px away

        var result = DetectionSpatialDedup.Dedupe([a, b], epsilon: 5);

        result.Should().ContainSingle().Which.Should().Be(b);
    }

    [Fact]
    public void Beyond_epsilon_keeps_both()
    {
        var a = Det("Portal", "landmark_portal", 10, 10, 0.7);
        var b = Det("Portal", "landmark_portal", 30, 10, 0.9);  // 20 px away

        var result = DetectionSpatialDedup.Dedupe([a, b], epsilon: 5);

        result.Should().HaveCount(2);
        result.Should().Contain(a);
        result.Should().Contain(b);
    }

    [Fact]
    public void Preserves_original_insertion_order_for_survivors()
    {
        // A (low, near B) collapses into B. B and C both survive — B was inserted
        // BEFORE C in the input, so the result lists B before C even though C may
        // have a different score.
        var a = Det("Portal", "landmark_portal", 10, 10, 0.5);
        var b = Det("Portal", "landmark_portal", 11, 11, 0.9);    // within ε of A
        var c = Det("Portal", "landmark_portal", 100, 100, 0.6);  // far from both

        var result = DetectionSpatialDedup.Dedupe([a, b, c], epsilon: 5);

        result.Should().HaveCount(2);
        result[0].Should().Be(b);  // B kept its original position (index 1 in input)
        result[1].Should().Be(c);  // C followed B in the input, follows B in output
    }

    [Fact]
    public void Tie_break_on_score_uses_first_insertion()
    {
        // Two byte-identical anchors with the SAME score: the first-inserted wins.
        var first = Det("Portal", "landmark_portal_a", 10, 10, 0.8);
        var second = Det("Portal", "landmark_portal_b", 10, 10, 0.8);

        var result = DetectionSpatialDedup.Dedupe([first, second], epsilon: 1);

        result.Should().ContainSingle().Which.Should().Be(first);
    }

    [Fact]
    public void Epsilon_zero_returns_input_as_is()
    {
        // ε <= 0 → no clustering. Defensive contract: callers can pass 0 to opt
        // out of dedup without the helper raising or silently keeping nothing.
        var a = Det("Portal", "landmark_portal", 10, 10, 0.7);
        var b = Det("Portal", "landmark_portal", 10, 10, 0.9);  // byte-identical

        var result = DetectionSpatialDedup.Dedupe([a, b], epsilon: 0);

        result.Should().HaveCount(2);
        result[0].Should().Be(a);
        result[1].Should().Be(b);
    }

    [Fact]
    public void Distance_uses_euclidean_not_chebyshev()
    {
        // Diagonal distance: (10,10) -> (13,14) is sqrt(9+16) = 5.0. With ε=5
        // these are at the boundary (within ε → collapse). With ε=4.5 they
        // stay separate (Chebyshev/AABB would also collapse them at ε=4 because
        // max(|dx|, |dy|) = 4 — the Euclidean predicate keeps them apart).
        var a = Det("Portal", "landmark_portal", 10, 10, 0.6);
        var b = Det("Portal", "landmark_portal", 13, 14, 0.9);

        var collapsed = DetectionSpatialDedup.Dedupe([a, b], epsilon: 5.0001);
        collapsed.Should().ContainSingle().Which.Should().Be(b);

        var kept = DetectionSpatialDedup.Dedupe([a, b], epsilon: 4.5);
        kept.Should().HaveCount(2);
    }
}
