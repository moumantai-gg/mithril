using System.Windows;
using System.Windows.Threading;
using Arda.Contracts;
using Arda.World.Player;
using Arda.World.Player.Events;
using Gandalf.Domain;
using Mithril.Shared.Audio;
using Mithril.Shared.Wpf;

namespace Gandalf.Services;

/// <summary>
/// Plays the alarm sound + flashes the window when a user timer transitions to
/// ready. Subscribes to <see cref="UserTimerSource"/> directly — quest and loot
/// cooldowns ship through the cross-source <c>DashboardAggregator</c> instead;
/// dragging derived rows into the user-tab alarm path would surface an audible
/// alarm for every chest re-loot the player observes, which isn't the user's
/// expressed preference. Cross-source notification belongs in a future shell
/// inbox subsystem (see docs/roadmaps/gandalf.md non-goals).
/// </summary>
public sealed class TimerAlarmService : IDisposable
{
    /// <summary>
    /// How long after a fire to suppress a same-key re-fire. Long enough to
    /// debounce accidental duplicates (a tick that fires and stamps then a
    /// rapid follow-up tick before the row is dismissed) but short enough
    /// not to interfere with recurring game-clock alarms whose natural
    /// cadence is ~7200 real seconds.
    /// </summary>
    private static readonly TimeSpan RefireSuppressionWindow = TimeSpan.FromSeconds(30);

    private readonly UserTimerSource _source;
    private readonly GandalfSettings _settings;
    private readonly IAudioPlaybackSink _audio;
    private readonly TimeProvider _time;
    private readonly ICalendarState? _calendarState;
    private readonly IDisposable? _replayTracker;
    private readonly Dictionary<string, DateTimeOffset> _firedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _snoozedUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IPlaybackHandle> _playback = new(StringComparer.Ordinal);
    private volatile bool _isReplaying;

    public TimerAlarmService(
        UserTimerSource source,
        GandalfSettings settings,
        IAudioPlaybackSink audio,
        TimeProvider? time = null,
        ICalendarState? calendarState = null,
        IDomainEventSubscriber? bus = null)
    {
        _source = source;
        _settings = settings;
        _audio = audio;
        _time = time ?? TimeProvider.System;
        _calendarState = calendarState;
        _replayTracker = bus?.Subscribe<CalendarTimeAdvanced>(evt =>
            _isReplaying = evt.Metadata.IsReplay);
        _source.TimerReady += OnTimerReady;
    }

    // State-decision clock: refire-suppression + snooze gates read from
    // the Arda calendar state; writes pair with reads so the comparison
    // is internally consistent.
    private DateTimeOffset Now => _calendarState?.LastTimestamp ?? _time.GetUtcNow();

    public void SnoozeAll()
    {
        var until = Now + TimeSpan.FromMinutes(_settings.SnoozeMinutes);
        foreach (var key in _firedAt.Keys.ToArray()) _snoozedUntil[key] = until;
        _firedAt.Clear();
        StopAllPlayback();
    }

    public void DismissAll()
    {
        _firedAt.Clear();
        StopAllPlayback();
    }

    public void Dismiss(string key)
    {
        _firedAt.Remove(key);
        if (_playback.Remove(key, out var handle))
            handle.Stop();
    }

    // internal for tests (Gandalf.Tests has InternalsVisibleTo); production
    // dispatch goes through UserTimerSource.TimerReady subscribed in the ctor.
    internal void OnTimerReady(object? sender, TimerReadyEventArgs e)
    {
        if (!_settings.AlarmEnabled) return;
        var key = e.Key;
        var now = Now;
        // Time-based dedup, not the old "fired once, never again" set —
        // recurring game-clock alarms reach this path on every cycle.
        if (_firedAt.TryGetValue(key, out var last) && now - last < RefireSuppressionWindow) return;
        if (_snoozedUntil.TryGetValue(key, out var until) && until > now) return;

        _firedAt[key] = now;

        // Mode-gate: suppress audio during replay. The _firedAt dedup
        // write above stays mode-agnostic so the next live-mode tick
        // reuses the same suppression contract.
        if (_isReplaying) return;

        var soundPath = ResolveSoundPath(e, _settings);
        Dispatch(() =>
        {
            var handle = _audio.Play(soundPath, (float)_settings.AlarmVolume, "gandalf");
            _playback[key] = handle;
            if (_settings.FlashWindow)
            {
                var win = Application.Current?.MainWindow;
                if (win is not null) WindowFlasher.Flash(win);
            }
        });
    }

    /// <summary>
    /// Per-timer <see cref="GandalfTimerDef.SoundFilePath"/> wins; null falls
    /// back to the global <see cref="GandalfSettings.SoundFilePath"/>. Extracted
    /// as a static for testability — the rest of the alarm pipeline goes
    /// through <c>AudioPlayer.Play</c> which is hard to mock.
    /// </summary>
    internal static string? ResolveSoundPath(TimerReadyEventArgs e, GandalfSettings settings) =>
        (e.SourceMetadata as GandalfTimerDef)?.SoundFilePath ?? settings.SoundFilePath;

    private static void Dispatch(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.InvokeAsync(a, DispatcherPriority.Normal);
    }

    public void Dispose()
    {
        _source.TimerReady -= OnTimerReady;
        _replayTracker?.Dispose();
        StopAllPlayback();
    }

    private void StopAllPlayback()
    {
        foreach (var h in _playback.Values) h.Stop();
        _playback.Clear();
    }
}
