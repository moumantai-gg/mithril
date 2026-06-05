using System.Diagnostics.Metrics;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Overlay.Internal;
using Mithril.Shared.Diagnostics.Telemetry;
using Xunit;

namespace Mithril.Overlay.Tests;

/// <summary>
/// M3 + m1 telemetry guards. Verifies that the
/// <c>MithrilMeters.Overlay.DispatchMisses</c> counter fires when a marker's
/// style has no registered drawer, AND that registering the drawer
/// suppresses subsequent misses (the delta-check guards against a future
/// refactor moving the counter outside the miss branch — the original
/// assertion would silently still pass).
///
/// <para>The counter is a process-static, and other tests in the suite
/// (notably the contention test) also produce miss events. The listener
/// therefore filters by <c>style_type</c> tag matching this test's
/// uniquely-named TestStyle so cross-test pollution doesn't perturb the
/// assertion.</para>
/// </summary>
public sealed class MissCountersTests
{
    // Unique name keeps this test's misses identifiable in the global stream.
    private sealed record MissCountersTestStyle(string Name) : IMarkerStyle;

    [Fact]
    public void Dispatch_misses_counter_fires_per_unregistered_style_marker_and_stops_after_registration()
    {
        var renderer = new MarkerSceneRenderer();

        var targetTypeName = typeof(MissCountersTestStyle).FullName!;
        long observedDispatchMisses = 0;

        using var listener = new MeterListener
        {
            InstrumentPublished = (instr, l) =>
            {
                if (instr.Meter.Name == "Mithril.Overlay"
                    && instr.Name == "mithril.overlay.dispatch.misses")
                {
                    l.EnableMeasurementEvents(instr);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            // Filter on style_type to isolate this test's misses from any
            // parallel test's dispatch counter increments.
            foreach (var kv in tags)
            {
                if (kv.Key == "style_type" && (kv.Value as string) == targetTypeName)
                {
                    Interlocked.Add(ref observedDispatchMisses, measurement);
                    return;
                }
            }
        });
        listener.Start();

        var markers = new List<(OverlayPixel, IMarkerStyle)>
        {
            (new OverlayPixel(0, 0), new MissCountersTestStyle("a")),
            (new OverlayPixel(1, 1), new MissCountersTestStyle("b")),
            (new OverlayPixel(2, 2), new MissCountersTestStyle("a")),
        };

        // Phase 1: no drawer registered — every marker misses.
        renderer.Render(markers, null!, null!, null!);

        observedDispatchMisses.Should().Be(3,
            "every marker missed (no drawer registered) and the counter should tick once per miss");

        // Phase 2: register a drawer, render again. Misses should NOT
        // increase. Without this delta-check the assertion above could pass
        // for the wrong reason if a future refactor moved the counter
        // outside the miss branch (it would fire on every marker rather
        // than only on misses).
        var drawerCalls = 0;
        renderer.RegisterDrawer<MissCountersTestStyle>((_, _, _, _, _) => Interlocked.Increment(ref drawerCalls));

        var preRegistrationMisses = Interlocked.Read(ref observedDispatchMisses);
        renderer.Render(markers, null!, null!, null!);

        Interlocked.Read(ref observedDispatchMisses).Should().Be(preRegistrationMisses,
            "with a registered drawer no marker should miss; the miss counter is per-miss, not per-render");
        drawerCalls.Should().Be(3, "the drawer must fire once per matching marker after registration");
    }
}
