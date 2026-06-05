// #1076 Phase 5a/5b: this perf harness constructs OverlayPixel positions
// directly (synthetic load — no mouse-event input), so no CanvasOverlayMapping
// boundary is needed here.
using System.Windows;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.ViewModels;
using Mithril.MapCalibration;

namespace Legolas.Diagnostics;

/// <summary>
/// Drives a synthetic 30-survey load through the live MapOverlay UI for perf
/// profiling. Bypasses the chat-log path entirely — surveys are constructed
/// directly and inserted into <see cref="SessionState.Surveys"/>, with the
/// projector pre-seeded so each pin lands at a defined pixel inside the
/// current map window. Captures two phases:
///   1. Listening (with a SelectedSurvey set, so the active-pin halo runs)
///   2. Gathering (post-OptimizeRoute, marching-ants animation running)
///
/// Numbers come from <see cref="FrameTimeLogger"/>; reports land in its
/// configured output folder.
///
/// Not a unit test — this exercises real WPF rendering. Has to run on the
/// dispatcher thread for any session-state mutation.
/// </summary>
public sealed class SurveyPerfHarness
{
    private readonly SessionState _session;
    private readonly SurveyFlowController _surveyFlow;
    private readonly MapOverlayViewModel _mapVm;
    private readonly LegolasSettings _settings;
    private readonly FrameTimeLogger _logger;
    // #957: the overlay frame is the shell capture rect now (MapOverlay retired).
    private readonly IMapCaptureRectStore _captureRectStore;

    public SurveyPerfHarness(
        SessionState session,
        SurveyFlowController surveyFlow,
        MapOverlayViewModel mapVm,
        LegolasSettings settings,
        FrameTimeLogger logger,
        IMapCaptureRectStore captureRectStore)
    {
        _session = session;
        _surveyFlow = surveyFlow;
        _mapVm = mapVm;
        _settings = settings;
        _logger = logger;
        _captureRectStore = captureRectStore;
    }

    // Current map-overlay frame size as a rough origin/metadata for the synthetic
    // load. Sourced from the one-rect capture store (physical px — exact magnitude
    // is irrelevant here, just a valid centre), falling back to 800×600 when unset.
    private (double Width, double Height) CurrentMapSize()
    {
        var rect = _captureRectStore.Get() ?? default;
        return (rect.Width > 0 ? rect.Width : 800, rect.Height > 0 ? rect.Height : 600);
    }

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Run the two-phase capture. Default 30 pins, 15s in each phase. Caller
    /// is responsible for being on a thread that *isn't* the dispatcher (we
    /// marshal back as needed; awaiting Task.Delay on the dispatcher would
    /// freeze the UI).
    /// </summary>
    public async Task RunAsync(int pinCount = 30, int phaseSeconds = 15, string labelPrefix = "", CancellationToken ct = default)
    {
        if (IsRunning) return;
        IsRunning = true;

        // OverlayController hides the map whenever PG isn't foreground (and
        // AutoHideOverlaysOnGameUnfocused is on, which is the default). The
        // harness has to render to capture meaningful frames, so suppress the
        // gate for the duration of the run and restore the user's preference
        // in the finally below.
        var prevAutoHide = _settings.AutoHideOverlaysOnGameUnfocused;

        try
        {
            var dispatcher = Application.Current?.Dispatcher
                ?? throw new InvalidOperationException("No WPF dispatcher available — harness needs a running UI.");

            await dispatcher.InvokeAsync(() => _settings.AutoHideOverlaysOnGameUnfocused = false);

            // Phase 0: arrange. Reset the session, anchor at map centre, inject
            // synthetic surveys. Done as one dispatcher hop so the FSM doesn't
            // observe a half-built collection.
            await dispatcher.InvokeAsync(() => Arrange(pinCount));

            // Phase 1: Listening, with the most-recently-added survey selected.
            // This is the everyday working state where the active-pin halo /
            // glow / Effect runs. Show the overlay so paint actually happens.
            await dispatcher.InvokeAsync(() =>
            {
                _session.IsMapVisible = true;
                _session.SelectedSurvey = _session.Surveys.LastOrDefault();
            });

            // Warmup: long enough for first paint after a freshly-shown overlay
            // window. 250ms wasn't — early harness runs captured zero frames
            // until the user manually toggled the logger first (which had the
            // side effect of forcing the window to render). 1500ms is safe even
            // on a cold start.
            await Task.Delay(TimeSpan.FromMilliseconds(1500), ct).ConfigureAwait(false);
            _logger.Start(labelPrefix + "listening", await dispatcher.InvokeAsync(() => SnapshotConfig(pinCount, "Listening")));
            await Task.Delay(TimeSpan.FromSeconds(phaseSeconds), ct).ConfigureAwait(false);
            _logger.Stop();

            // Phase 2: Gathering. OptimizeRouteCommand transitions the FSM and
            // assigns RouteOrders, which lights up the marching-ants animation
            // on the active segment.
            await dispatcher.InvokeAsync(() =>
            {
                _mapVm.OptimizeRouteCommand.Execute(null);
            });

            // Warmup: long enough for first paint after a freshly-shown overlay
            // window. 250ms wasn't — early harness runs captured zero frames
            // until the user manually toggled the logger first (which had the
            // side effect of forcing the window to render). 1500ms is safe even
            // on a cold start.
            await Task.Delay(TimeSpan.FromMilliseconds(1500), ct).ConfigureAwait(false);
            _logger.Start(labelPrefix + "gathering", await dispatcher.InvokeAsync(() => SnapshotConfig(pinCount, "Gathering")));
            await Task.Delay(TimeSpan.FromSeconds(phaseSeconds), ct).ConfigureAwait(false);
            _logger.Stop();

            // Tidy up so the next harness run starts from a known-clean state.
            await dispatcher.InvokeAsync(() => _surveyFlow.Reset());
        }
        finally
        {
            // Belt-and-braces: ensure the logger isn't left running if anything
            // above threw or we were cancelled mid-phase.
            if (_logger.IsRunning) _logger.Stop();
            // Restore the user's auto-hide preference. Best-effort dispatcher hop;
            // if the app is shutting down there's nothing to restore on.
            try
            {
                await (Application.Current?.Dispatcher.InvokeAsync(() =>
                    _settings.AutoHideOverlaysOnGameUnfocused = prevAutoHide).Task ?? Task.CompletedTask);
            }
            catch { }
            IsRunning = false;
        }
    }

    /// <summary>
    /// Run the harness once per active-pin treatment back-to-back so a
    /// single keypress produces matched A/B reports without the user
    /// flipping settings between runs. Halo and Glow are the perf-relevant
    /// pair (the other treatments are pure paint variants); each produces
    /// listening + gathering reports prefixed with the treatment name.
    /// Restores the user's previous treatment when done. Approx
    /// <c>2 * (1 + 2 * (1.5 + phaseSeconds))</c> seconds wall-clock.
    /// </summary>
    public async Task RunTreatmentSweepAsync(int pinCount, int phaseSeconds = 15, CancellationToken ct = default)
    {
        // Outer guard. RunAsync sets/clears IsRunning per-iteration, so this
        // sweep doesn't pre-set it — it's just refusing to interleave with
        // an already-running single run.
        if (IsRunning) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        var prevTreatment = _settings.ActivePinStyle.Treatment;
        var treatments = new[] { ActivePinTreatment.Halo, ActivePinTreatment.Glow };

        try
        {
            for (var i = 0; i < treatments.Length; i++)
            {
                var t = treatments[i];
                await dispatcher.InvokeAsync(() => _settings.ActivePinStyle.Treatment = t);
                // Brief settle so the Effect MultiBindings re-evaluate before
                // we start measuring — otherwise the first ~1 frame of the
                // listening phase mixes treatments.
                await Task.Delay(500, ct).ConfigureAwait(false);
                await RunAsync(
                    pinCount: pinCount,
                    phaseSeconds: phaseSeconds,
                    labelPrefix: t.ToString().ToLowerInvariant() + "-",
                    ct: ct).ConfigureAwait(false);
            }
        }
        finally
        {
            try { await dispatcher.InvokeAsync(() => _settings.ActivePinStyle.Treatment = prevTreatment); }
            catch { }
        }
    }

    private void Arrange(int pinCount)
    {
        _surveyFlow.Reset();

        // Centre the anchor in whatever the current map window happens to be
        // (the user may have resized it). Width/Height fall back if the
        // settings layout hasn't hydrated yet — irrelevant for the synthetic
        // load, just need a valid origin.
        var (w, h) = CurrentMapSize();
        var centre = new OverlayPixel(w / 2, h / 2);

        // #454: pins are absolute now — inject them directly at pixel
        // positions (no anchor, no projector). Deterministic seed so two runs
        // are visually identical and any frame-time delta is attributable to
        // config changes, not layout. ~60–240 px ring around the window
        // centre (visually equivalent to the old 20–80 m × ~3 px/m).
        var rng = new Random(42);
        for (var i = 0; i < pinCount; i++)
        {
            var theta = rng.NextDouble() * 2 * Math.PI;
            var rPx = 60 + rng.NextDouble() * 180;
            var pixel = new OverlayPixel(
                centre.X + rPx * Math.Cos(theta),
                centre.Y + rPx * Math.Sin(theta));
            var model = Survey.CreateAbsolute(
                $"PerfPin{i + 1:D2}", new WorldCoord(rPx, 0, theta), pixel, gridIndex: i);
            _session.Surveys.Add(new SurveyItemViewModel(model));
        }
    }

    private FrameRunConfig SnapshotConfig(int pinCount, string fsmState)
    {
        var (mapWidth, mapHeight) = CurrentMapSize();
        return new(
            PinCount: pinCount,
            ActiveTreatment: _settings.ActivePinStyle.Treatment.ToString(),
            AllowsTransparency: true, // MapOverlayView XAML hard-sets this; recorded for the report
            ClickThroughMap: _settings.ClickThroughMap,
            ShowBearingWedges: _session.ShowBearingWedges,
            ShowRouteLines: _session.ShowRouteLines,
            MapWidth: mapWidth,
            MapHeight: mapHeight,
            FsmState: fsmState);
    }
}
