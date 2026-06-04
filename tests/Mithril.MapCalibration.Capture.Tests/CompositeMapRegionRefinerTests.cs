using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class CompositeMapRegionRefinerTests
{
    private static GrayImage Img(int w = 4, int h = 4) =>
        new(w, h, new byte[w * h]);

    private sealed class FakeRefiner : IMapRegionRefiner, IAreaContextualRefiner
    {
        public MapRegionRefineResult Next = MapRegionRefineResult.None;
        public int RefineCalls;
        public string? LastAreaKey;
        public int SetAreaKeyCalls;

        public MapRegionRefineResult Refine(GrayImage capturedGray, GrayImage baseTexture)
        {
            RefineCalls++;
            return Next;
        }

        public void SetAreaKey(string? areaKey)
        {
            SetAreaKeyCalls++;
            LastAreaKey = areaKey;
        }
    }

    private static MapRect Rect() => new(0, 0, 4, 4, 4, 4);
    private static LocateMetrics OrbAcceptMetrics() => new(
        InlierCount: 100, CandidateCount: 120, InlierRatio: 0.83,
        Scale: 1.0, RotationDegrees: 0, Mirror: false,
        Tx: 0, Ty: 0, ResidualPixels: 0.5,
        Provenance: LocateProvenance.OrbRansac, Confidence: null);
    private static LocateMetrics NccMetrics(double ncc) => new(
        InlierCount: 0, CandidateCount: 0, InlierRatio: 0,
        Scale: 1.0, RotationDegrees: 0, Mirror: false,
        Tx: 0, Ty: 0, ResidualPixels: 0,
        Provenance: LocateProvenance.SobelPaddedPyramid, Confidence: ncc);

    [Fact]
    public void Returns_primary_result_when_primary_accepts()
    {
        var primary = new FakeRefiner { Next = new(Rect(), Rect(), OrbAcceptMetrics()) };
        var fallback = new FakeRefiner();
        var composite = new CompositeMapRegionRefiner(primary, fallback);

        var result = composite.Refine(Img(), Img());

        result.AcceptedRect.Should().NotBeNull();
        result.Metrics!.Provenance.Should().Be(LocateProvenance.OrbRansac);
        primary.RefineCalls.Should().Be(1);
        fallback.RefineCalls.Should().Be(0);
    }

    [Fact]
    public void Falls_through_to_fallback_when_primary_returns_none()
    {
        var primary = new FakeRefiner { Next = MapRegionRefineResult.None };
        var fallback = new FakeRefiner { Next = new(Rect(), Rect(), NccMetrics(0.5)) };
        var composite = new CompositeMapRegionRefiner(primary, fallback);

        var result = composite.Refine(Img(), Img());

        result.AcceptedRect.Should().NotBeNull();
        result.Metrics!.Provenance.Should().Be(LocateProvenance.SobelPaddedPyramid);
        primary.RefineCalls.Should().Be(1);
        fallback.RefineCalls.Should().Be(1);
    }

    [Fact]
    public void Falls_through_when_primary_rejects_with_metrics_but_no_accepted_rect()
    {
        // Primary populated RawFitRect + Metrics but gate rejected → still falls through.
        var rejectMetrics = OrbAcceptMetrics() with { InlierCount = 2, InlierRatio = 0.10 };
        var primary = new FakeRefiner { Next = new(null, Rect(), rejectMetrics) };
        var fallback = new FakeRefiner { Next = new(Rect(), Rect(), NccMetrics(0.6)) };
        var composite = new CompositeMapRegionRefiner(primary, fallback);

        var result = composite.Refine(Img(), Img());

        result.AcceptedRect.Should().NotBeNull();
        result.Metrics!.Provenance.Should().Be(LocateProvenance.SobelPaddedPyramid);
        fallback.RefineCalls.Should().Be(1);
    }

    [Fact]
    public void Surfaces_fallback_rejection_when_neither_branch_accepts()
    {
        var primary = new FakeRefiner { Next = MapRegionRefineResult.None };
        var rejectMetrics = NccMetrics(0.10);
        var fallback = new FakeRefiner { Next = new(null, Rect(), rejectMetrics) };
        var composite = new CompositeMapRegionRefiner(primary, fallback);

        var result = composite.Refine(Img(), Img());

        result.AcceptedRect.Should().BeNull();
        result.Metrics!.Provenance.Should().Be(LocateProvenance.SobelPaddedPyramid);
        result.Metrics.Confidence!.Value.Should().BeLessThan(0.20);
    }

    [Fact]
    public void Forwards_SetAreaKey_to_both_inner_refiners_that_support_it()
    {
        var primary = new FakeRefiner();
        var fallback = new FakeRefiner();
        var composite = new CompositeMapRegionRefiner(primary, fallback);

        composite.SetAreaKey("Map_GoblinDungeon");

        primary.LastAreaKey.Should().Be("Map_GoblinDungeon");
        fallback.LastAreaKey.Should().Be("Map_GoblinDungeon");
    }

    [Fact]
    public void Does_not_forward_SetAreaKey_to_refiners_that_do_not_implement_the_marker()
    {
        var primary = new MinimalRefiner();
        var fallback = new FakeRefiner();
        var composite = new CompositeMapRegionRefiner(primary, fallback);

        composite.SetAreaKey("Map_X");

        fallback.LastAreaKey.Should().Be("Map_X");
        // No throw — primary just gets skipped. (Asserted implicitly by the call returning.)
    }

    private sealed class MinimalRefiner : IMapRegionRefiner
    {
        public MapRegionRefineResult Refine(GrayImage capturedGray, GrayImage baseTexture)
            => MapRegionRefineResult.None;
    }
}
