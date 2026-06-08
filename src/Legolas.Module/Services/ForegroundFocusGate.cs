using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Legolas.Interop;
using Microsoft.Extensions.Hosting;
using Mithril.Shared.Game;
using Mithril.Shared.Hotkeys;
using Mithril.Shared.Modules;

namespace Legolas.Services;

/// <summary>
/// Tracks whether the foreground window belongs to Mithril or the configured
/// game process. Exposes a single observable <see cref="IsInApp"/> bool that
/// <see cref="Hotkeys.OverlayController"/> ANDs with the user's IsMapVisible /
/// IsInventoryVisible flags so overlays vanish while the user is in a browser,
/// Discord, etc., and reappear (with prior intent preserved) when they come
/// back.
///
/// The "in-app" predicate matches by self-PID OR a case-insensitive substring
/// match against <see cref="GameConfig.GameProcessName"/> (#919: relocated from
/// LegolasSettings to the shared GameConfig). Substring match is forgiving
/// across Steam/Itch/standalone naming variations (e.g. "ProjectGorgon",
/// "Project Gorgon", "ProjectGorgon64") and lets the user override via the
/// settings UI without a code change.
/// </summary>
public sealed class ForegroundFocusGate : IHostedService, INotifyPropertyChanged, IHotkeyGate
{
    private readonly ModuleGates _gates;
    private readonly GameConfig _gameConfig;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly uint _ownPid;

    // Held to prevent GC of the delegate while the native hook is live.
    private User32Focus.WinEventProc? _hookProc;
    private IntPtr _hookHandle = IntPtr.Zero;
    private Task? _activationTask;
    private bool _isInApp = true;

    // mithril#1114 — Probe for "WPF Application is tearing down" so OnWinEvent
    // can bail before cascading into lazy OverlayWindow construction. Default
    // reads Dispatcher.HasShutdownStarted; tests inject a constant since the
    // static Dispatcher state can't be faked.
    private Func<bool> _shutdownProbe = DefaultShutdownProbe;
    private static bool DefaultShutdownProbe() =>
        Application.Current?.Dispatcher.HasShutdownStarted ?? false;

    // mithril#1114 — Cached so UninstallHook can detach symmetrically.
    private ExitEventHandler? _applicationExitHandler;

    public ForegroundFocusGate(ModuleGates gates, GameConfig gameConfig)
    {
        _gates = gates;
        _gameConfig = gameConfig;
        _ownPid = (uint)Environment.ProcessId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsInApp
    {
        get => _isInApp;
        private set
        {
            if (_isInApp == value) return;
            _isInApp = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInApp)));
            // Mirror onto IHotkeyGate.CanFire so HotkeyService re-registers
            // bindings when focus crosses the in-app boundary.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanFire)));
        }
    }

    public bool CanFire => IsInApp;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Wait on the legolas module gate before installing the hook — there's
        // no overlay to gate before the user opens the Legolas tab, and we
        // don't want to claim a system-wide hook for an unused module.
        _activationTask = Task.Run(async () =>
        {
            try
            {
                await _gates.For("legolas").WaitAsync(_stopCts.Token).ConfigureAwait(false);
                if (Application.Current?.Dispatcher is { } dispatcher)
                {
                    await dispatcher.InvokeAsync(InstallHook);
                }
            }
            catch (OperationCanceledException) { }
        }, _stopCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopCts.Cancel();
        if (_activationTask is not null) { try { await _activationTask.ConfigureAwait(false); } catch { } }
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            await dispatcher.InvokeAsync(UninstallHook);
        }
    }

    private void InstallHook()
    {
        if (_hookHandle != IntPtr.Zero) return;
        _hookProc = OnWinEvent;
        _hookHandle = User32Focus.SetWinEventHook(
            User32Focus.EVENT_SYSTEM_FOREGROUND,
            User32Focus.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _hookProc,
            idProcess: 0,
            idThread: 0,
            User32Focus.WINEVENT_OUTOFCONTEXT);

        // Re-evaluate the current foreground when the user changes the
        // configured process name — otherwise they'd have to alt-tab to make
        // a corrected name take effect.
        _gameConfig.PropertyChanged += OnSettingsPropertyChanged;

        // mithril#1114 — Tear the hook down inside the WPF teardown sequence
        // rather than waiting for IHostedService.StopAsync, which Program.cs
        // only runs in its finally block AFTER app.Run() returns. By then WPF
        // has already finished shutting down and any foreground event
        // delivered during teardown has already cascaded through OverlayController
        // into Application.LoadComponent (which throws once IsShuttingDown).
        if (Application.Current is { } app)
        {
            _applicationExitHandler = (_, _) => UninstallHook();
            app.Exit += _applicationExitHandler;
        }

        // Hook delivery is event-driven, so seed the gate from the current
        // foreground window — otherwise IsInApp stays at its default until the
        // next focus change.
        EvaluateForeground(User32Focus.GetForegroundWindow());
    }

    private void UninstallHook()
    {
        if (_hookHandle == IntPtr.Zero) return;
        if (_applicationExitHandler is not null && Application.Current is { } app)
        {
            app.Exit -= _applicationExitHandler;
        }
        _applicationExitHandler = null;
        _gameConfig.PropertyChanged -= OnSettingsPropertyChanged;
        User32Focus.UnhookWinEvent(_hookHandle);
        _hookHandle = IntPtr.Zero;
        _hookProc = null;
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GameConfig.GameProcessName)) return;
        EvaluateForeground(User32Focus.GetForegroundWindow());
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime)
    {
        if (eventType != User32Focus.EVENT_SYSTEM_FOREGROUND) return;
        // mithril#1114 — Race backstop for any foreground event already in the
        // dispatcher queue when Application.Exit fires (Exit runs AFTER
        // IsShuttingDown is set). Without this, OverlayController.SyncMap
        // would lazily construct an OverlayWindow whose InitializeComponent
        // calls Application.LoadComponent and throws.
        if (_shutdownProbe()) return;
        EvaluateForeground(hwnd);
    }

    private void EvaluateForeground(IntPtr hwnd)
    {
        EvaluateForegroundCallsForTest++;
        if (hwnd == IntPtr.Zero) return;
        IsInApp = IsForegroundInApp(hwnd);
    }

    // ---- mithril#1114 test seams (not part of the production surface) ----

    /// <summary>Test seam — count of EvaluateForeground entries. Lets the
    /// shutdown-guard test assert that OnWinEvent's early-return BEFORE
    /// EvaluateForeground is what stops the cascade (vs. the existing
    /// hwnd==IntPtr.Zero early-return inside EvaluateForeground).</summary>
    internal int EvaluateForegroundCallsForTest { get; private set; }

    /// <summary>Test seam — override the "Application is shutting down" probe.
    /// Production reads <see cref="System.Windows.Threading.Dispatcher.HasShutdownStarted"/>,
    /// which is sealed on the static dispatcher and can't be faked.</summary>
    internal void SetShutdownProbeForTest(Func<bool> probe) =>
        _shutdownProbe = probe ?? throw new ArgumentNullException(nameof(probe));

    /// <summary>Test seam — drive <see cref="OnWinEvent"/> directly with the
    /// foreground-changed event type, bypassing the Win32 hook dispatch.</summary>
    internal void TestSimulateForegroundChanged(IntPtr hwnd) =>
        OnWinEvent(IntPtr.Zero, User32Focus.EVENT_SYSTEM_FOREGROUND, hwnd, 0, 0, 0, 0);

    private bool IsForegroundInApp(IntPtr hwnd)
    {
        User32Focus.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return _isInApp;
        if (pid == _ownPid) return true;

        var configured = _gameConfig.GameProcessName;
        if (string.IsNullOrWhiteSpace(configured)) return false;

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            // Substring match (case-insensitive) so "ProjectGorgon",
            // "ProjectGorgon64", or "Project Gorgon" all count as in-app
            // without forcing the user to know the exact image name.
            return proc.ProcessName.Contains(configured, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Process exited between the hook firing and us looking it up,
            // or access denied (elevated process). Treat as out-of-app so
            // overlays hide rather than stick around incorrectly.
            return false;
        }
    }
}
