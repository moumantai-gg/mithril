using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Overlay.Internal;
using Xunit;

namespace Mithril.Overlay.Tests;

/// <summary>
/// Drawer-dispatch behaviour for <see cref="MarkerSceneRenderer"/>. Verifies
/// that a registered drawer is invoked exactly once per matching marker per
/// tick, an unregistered style is silently skipped (v1 contract), and that
/// type-keying is exact (subclasses are NOT polymorphically routed to a
/// base-class drawer — that's a v2 decision).
/// </summary>
public sealed class MarkerSceneRendererTests
{
    private sealed record StyleA : IMarkerStyle;
    private sealed record StyleB : IMarkerStyle;

    [Fact]
    public void Registered_drawer_is_invoked_once_per_matching_marker()
    {
        var renderer = new MarkerSceneRenderer();
        var calls = new List<OverlayPixel>();
        renderer.RegisterDrawer<StyleA>((style, pixel, rt, factory, brushes) => calls.Add(pixel));

        var markers = new List<(OverlayPixel, IMarkerStyle)>
        {
            (new OverlayPixel(10, 20), new StyleA()),
            (new OverlayPixel(30, 40), new StyleA()),
        };

        // Passing nulls for D2D plumbing is fine — the registered drawer
        // never touches them.
        renderer.Render(markers, rt: null!, factory: null!, brushes: null!);

        calls.Should().Equal(new OverlayPixel(10, 20), new OverlayPixel(30, 40));
    }

    [Fact]
    public void Unregistered_style_is_silently_skipped()
    {
        var renderer = new MarkerSceneRenderer();
        var calls = 0;
        renderer.RegisterDrawer<StyleA>((s, p, rt, f, b) => calls++);

        var markers = new List<(OverlayPixel, IMarkerStyle)>
        {
            (new OverlayPixel(0, 0), new StyleA()),
            (new OverlayPixel(1, 1), new StyleB()), // unregistered — must not throw
        };

        renderer.Render(markers, null!, null!, null!);
        calls.Should().Be(1);
    }

    [Fact]
    public void HasAnyDrawer_reflects_registration_state()
    {
        var renderer = new MarkerSceneRenderer();
        renderer.HasAnyDrawer.Should().BeFalse();
        renderer.RegisterDrawer<StyleA>((_, _, _, _, _) => { });
        renderer.HasAnyDrawer.Should().BeTrue();
    }

    [Fact]
    public void Re_registering_a_style_type_replaces_the_drawer()
    {
        var renderer = new MarkerSceneRenderer();
        var call1 = 0;
        var call2 = 0;
        renderer.RegisterDrawer<StyleA>((_, _, _, _, _) => call1++);
        renderer.RegisterDrawer<StyleA>((_, _, _, _, _) => call2++);

        renderer.Render(new[] { (new OverlayPixel(0, 0), (IMarkerStyle)new StyleA()) },
            null!, null!, null!);
        call1.Should().Be(0);
        call2.Should().Be(1);
    }
    /// <summary>B2 + F4: concurrent RegisterDrawer + Render must never throw,
    /// must never lose a registration, AND must make actual render progress
    /// (the drawer body must be invoked). The progress assertion guards
    /// against a refactor where Render silently no-ops under contention
    /// (e.g. always missing the dictionary lookup) — without it the test
    /// would pass purely by not throwing.</summary>
    [Fact]
    public async Task Concurrent_RegisterDrawer_and_Render_do_not_throw_and_dispatch_progresses()
    {
        var renderer = new MarkerSceneRenderer();
        var marker = new[] { (new OverlayPixel(0, 0), (IMarkerStyle)new StyleA()) };
        var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var drawerInvocations = 0;

        var register = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                // Re-register the StyleA drawer in a tight loop with a body
                // that Interlocked-increments a counter. Subsequent
                // registrations replace the previous drawer (per the renderer
                // contract); whichever generation is live when Render dispatches
                // still hits the same counter.
                renderer.RegisterDrawer<StyleA>((_, _, _, _, _) => Interlocked.Increment(ref drawerInvocations));
            }
        });
        var render = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                renderer.Render(marker, null!, null!, null!);
            }
        });

        await Task.WhenAll(register, render);
        renderer.HasAnyDrawer.Should().BeTrue();
        drawerInvocations.Should().BeGreaterThan(0,
            "Render must have dispatched to the registered drawer at least once during the contention window — "
            + "if zero, Render silently no-op'd (concurrent-dictionary lookup miss under contention, or similar)");
    }
}
