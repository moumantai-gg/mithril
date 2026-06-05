using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;
using Mithril.Shared.Diagnostics.Telemetry;
using Vortice.Direct2D1;

namespace Mithril.Overlay.Internal;

/// <summary>
/// Direct2D draw dispatcher for projected markers. Each consumer registers a
/// drawer keyed on its concrete <see cref="IMarkerStyle"/> type via
/// <see cref="RegisterDrawer{TStyle}"/>; per tick the renderer iterates the
/// projected list and dispatches to the matching drawer.
///
/// <para>v1 dispatch is a <c>ConcurrentDictionary&lt;Type, MarkerDrawer&gt;</c>
/// keyed on <c>style.GetType()</c>. The issue body calls for a switch in v1
/// but with no concrete styles shipped in this scaffold PR, a type-keyed
/// drawer registry is the smallest forward-compat shape that lets tests
/// verify dispatch &#8212; the type-switch suggestion was a hint at the
/// dispatch granularity, not a literal switch statement. When the migration
/// PRs land Legolas's drawers (<c>LegolasSurveyMarkerDrawer</c> etc.) they
/// call <see cref="RegisterDrawer{TStyle}"/> at module activation.</para>
///
/// <para><b>Thread safety.</b> Backing collections are concurrent because
/// <see cref="RegisterDrawer{TStyle}"/> may be invoked from module-activation
/// callbacks (which can run on threadpool threads for lazy modules /
/// hosted-service-initiated registration) while
/// <see cref="Render"/> reads from the WPF dispatcher every tick. A plain
/// <see cref="Dictionary{TKey,TValue}"/> would invite UB (stale reads or
/// in-flight enumeration throws inside the render loop). The fast paths
/// (<see cref="ConcurrentDictionary{TKey,TValue}.TryGetValue"/> /
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/>) are lock-free so
/// the per-tick cost stays a single hashed lookup per marker.</para>
///
/// <para><b>Unregistered styles.</b> A marker whose <see cref="IMarkerStyle"/>
/// has no registered drawer is silently skipped this PR &#8212; per the
/// brief, the v2 cut (when there's a real registry surface) will log/throw.
/// A first-encounter trace log is emitted per unknown type so the case is
/// at least visible during the migration window; the
/// <see cref="MithrilMeters.Overlay.DispatchMisses"/> counter increments per
/// skipped marker so production drops are observable in the trace.</para>
/// </summary>
internal sealed class MarkerSceneRenderer
{
    /// <summary>Pure drawer: paint one marker at a pixel into the target.</summary>
    public delegate void MarkerDrawer(IMarkerStyle style, OverlayPixel pixel, ID2D1RenderTarget rt, ID2D1Factory factory, D2DBrushCache brushes);

    private readonly ConcurrentDictionary<Type, MarkerDrawer> _drawers = new();
    private readonly ConcurrentDictionary<Type, byte> _missingDrawerLogged = new();
    private readonly ILogger? _logger;

    public MarkerSceneRenderer(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>Register a drawer for a concrete <see cref="IMarkerStyle"/>
    /// type. Subsequent registrations for the same type replace the
    /// previous drawer. Safe to call from any thread.</summary>
    public void RegisterDrawer<TStyle>(Action<TStyle, OverlayPixel, ID2D1RenderTarget, ID2D1Factory, D2DBrushCache> drawer)
        where TStyle : IMarkerStyle
    {
        ArgumentNullException.ThrowIfNull(drawer);
        _drawers[typeof(TStyle)] = (style, pixel, rt, factory, brushes)
            => drawer((TStyle)style, pixel, rt, factory, brushes);
    }

    /// <summary>True when at least one drawer is registered. Lets the
    /// projection driver short-circuit when the scaffold is wired but no
    /// consumer has plugged drawers in yet (e.g. immediately after
    /// <see cref="OverlayWindowService"/> starts, before Legolas activates).</summary>
    public bool HasAnyDrawer => !_drawers.IsEmpty;

    /// <summary>Number of registered drawer types &#8212; internal helper
    /// for diagnostics.</summary>
    internal int DrawerCount => _drawers.Count;

    /// <summary>
    /// True iff a drawer is registered for the exact concrete style type
    /// <typeparamref name="TStyle"/>. Internal helper used by Legolas's
    /// drawer-registration tests so they can verify each style type is wired
    /// individually (a raw <see cref="DrawerCount"/> check would pass even
    /// if one type were double-registered and another silently dropped).
    ///
    /// <para>TEMPORARY (#835 migration window): exposed for the Legolas
    /// drawer-set composition tests. When step 6 retires Legolas's
    /// own-renderer fallback path, the test surface owner becomes
    /// <c>Mithril.Overlay.Tests</c> which already has internal access — this
    /// accessor can stay or shrink to <c>private</c>, whichever the
    /// then-canonical tests prefer.</para>
    /// </summary>
    internal bool IsRegistered<TStyle>() where TStyle : IMarkerStyle
        => _drawers.ContainsKey(typeof(TStyle));

    /// <summary>Render one frame's projected markers. Each marker's style
    /// type is looked up in the drawer registry; misses are silently
    /// skipped (with a trace log on first encounter per type, plus a
    /// <c>DispatchMisses</c> meter increment per skipped marker).</summary>
    public void Render(
        IReadOnlyList<(OverlayPixel Pixel, IMarkerStyle Style)> markers,
        ID2D1RenderTarget rt,
        ID2D1Factory factory,
        D2DBrushCache brushes)
    {
        if (markers.Count == 0) return;

        for (var i = 0; i < markers.Count; i++)
        {
            var (pixel, style) = markers[i];
            var type = style.GetType();
            if (!_drawers.TryGetValue(type, out var drawer))
            {
                if (_missingDrawerLogged.TryAdd(type, 0))
                {
                    _logger?.LogTrace(
                        "MarkerSceneRenderer: no drawer registered for style type {StyleType}; markers of this type will be silently skipped",
                        type.FullName);
                }
                MithrilMeters.Overlay.DispatchMisses.Add(1,
                    new KeyValuePair<string, object?>("style_type", type.FullName ?? type.Name));
                continue;
            }
            drawer(style, pixel, rt, factory, brushes);
        }
    }
}
