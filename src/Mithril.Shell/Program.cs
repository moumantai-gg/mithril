using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Mithril.Shared.Audio;
using Mithril.Shared.Character;
using Mithril.Shared.DependencyInjection;
using Mithril.Shared.Diagnostics.Performance;
using Mithril.Shared.Game;
using Mithril.Shared.Hotkeys;
using Mithril.Shared.Icons;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.Shared.Settings;
using Mithril.Shared.Wpf;
using Mithril.Shared.Wpf.DependencyInjection;
using Mithril.Shell.DependencyInjection;
using Mithril.Shell.Updates;
using Mithril.Shell.ViewModels;
using Mithril.Shell.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Velopack;

namespace Mithril.Shell;

public static class Program
{
    private const string MutexName = @"Global\Mithril.Shell.SingleInstance";
    private const string ActivateEventName = @"Global\Mithril.Shell.Activate";

    private static readonly string BootLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mithril", "Shell", "logs", "mithril-boot.log");

    /// <summary>
    /// Drop-off file the second instance uses to hand an activation URI (e.g. <c>mithril://item/X</c>)
    /// to the first instance. Read and deleted by <see cref="App"/> on each activate-event signal.
    /// </summary>
    public static readonly string ActivationUriPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mithril", "Shell", "activation.uri");

    /// <summary>System (primary-monitor) DPI scale at process start — 1.0 at 96 DPI
    /// (100%), 1.5 at 144 (150%). Used once at bootstrap to convert the retired
    /// overlay DIU rect to physical px (#957, <see cref="MapCaptureRectCarryOver"/>)
    /// before any window exists. The process is PerMonitorV2-aware via the app
    /// manifest, so this reflects the real system setting (not a flat 96).</summary>
    private static double SystemDpiScale()
    {
        uint dpi = GetDpiForSystem();
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack hooks must run before ANY side-effecting code (mutex, file I/O, WPF init):
        // --veloapp-install / --veloapp-uninstall / --veloapp-updated / --veloapp-firstrun
        // are stripped from argv here, and install/uninstall variants call Environment.Exit
        // after handling. Anything that ran first would leave settings folders, boot logs,
        // or single-instance handles behind during a quiet install.
        VelopackApp.Build().Run();

        // Channel marker is embedded by MSBuild via -p:MithrilUpdateChannel=…; 'dev' for F5.
        // Probe once we're past the Velopack hooks but before we build the host, so a
        // missing .NET 10 runtime on framework-dependent installs surfaces as a friendly
        // dialog instead of a CLR-load crash.
        var channel = UpdateChannelInfo.FromEmbedded();
        if (channel.IsFrameworkDependent && !DesktopRuntimeIsAvailable())
        {
            ShowMissingRuntimeDialog();
            return;
        }

        // Headless DI-graph self-check (#365 guard). Process-isolated, before the
        // single-instance mutex and any UI/host work: composes the real graph,
        // resolves every startup root under a hard watchdog, exits 0/1/2.
        if (Array.IndexOf(args, SelfCheck.Switch) >= 0)
        {
            Environment.Exit(SelfCheck.Run(args));
        }

        Mutex? mutex = null;
        EventWaitHandle? activateEvent = null;
        CancellationTokenSource? activateCts = null;
        IHost? host = null;

        try
        {
            Boot("=== startup ===");
            Boot($"shell version: {typeof(Program).Assembly.InformationalVersion()}");

            // Extract an activation URI from argv if present. The OS passes it as the first
            // argument when launching via the registered custom scheme (mithril://…).
            var activationUri = ExtractActivationUri(args);

            // Single-instance guard
            mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (!createdNew)
            {
                try
                {
                    // Hand the URI to the first instance before signalling, so its WatchActivateEvent
                    // loop has a file to read. Overwrite any previous drop-off.
                    if (activationUri is not null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(ActivationUriPath)!);
                        File.WriteAllText(ActivationUriPath, activationUri);
                    }
                    using var ev = EventWaitHandle.OpenExisting(ActivateEventName);
                    ev.Set();
                }
                catch { }
                return;
            }

            activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            activateCts = new CancellationTokenSource();

            // Shell settings — no SynchronizationContext yet, so blocking is safe
#pragma warning disable VSTHRD002 // runs before WPF dispatcher is installed
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var shellDir = Path.Combine(localApp, "Mithril", "Shell");
            Directory.CreateDirectory(shellDir);
            var shellSettingsPath = Path.Combine(shellDir, "shell.json");

            var shellStore = new JsonSettingsStore<ShellSettings>(
                shellSettingsPath, ShellSettingsJsonContext.Default.ShellSettings);
            var shellSettings = shellStore.LoadAsync().GetAwaiter().GetResult();

            // #957: ShellSettings became schema-versioned (#208). Mirror
            // AddMithrilVersionedSettings — dispatch through Migrate + persist when the
            // loaded version lags CurrentVersion (identity for v1; future bumps add
            // upgrade logic). Done inline because the shell store is loaded by hand
            // here, before the host is built.
            if (shellSettings.SchemaVersion != ShellSettings.CurrentVersion)
            {
                shellSettings = ShellSettings.Migrate(shellSettings);
                shellSettings.SchemaVersion = ShellSettings.CurrentVersion;
                shellStore.SaveAsync(shellSettings).GetAwaiter().GetResult();
            }

            if (string.IsNullOrEmpty(shellSettings.GameRoot))
                shellSettings.GameRoot = GameLocator.AutoDetectGameRoot() ?? "";

            // #959: the asset-extractor sidecar needs the Steam INSTALL dir
            // (…\steamapps\common\Project Gorgon — contains WindowsPlayer_Data),
            // which is distinct from GameRoot (the LocalLow data dir). Auto-detect
            // Steam-only + fail-soft when unset; a manual override always wins. Persist
            // only when detection actually filled it in (skip a redundant save when it
            // returns null and the value stays "").
            if (string.IsNullOrEmpty(shellSettings.InstallRoot))
            {
                var detectedInstall = GameLocator.AutoDetectInstallRoot();
                if (!string.IsNullOrEmpty(detectedInstall))
                {
                    shellSettings.InstallRoot = detectedInstall;
                    shellStore.SaveAsync(shellSettings).GetAwaiter().GetResult();
                }
            }

            // #919 one-time cross-file carry-over: GameProcessName +
            // CalibrationGoodResidualPx moved from LegolasSettings (legolas.json)
            // to the shared shell store. Per-file Migrate can't cross files, so
            // read the legacy values straight from legolas.json and copy them in
            // when the shell value is still its factory default. Idempotent —
            // once carried/edited, the shell value is non-default and the gate
            // closes. Runs before the module loads, so the shared store wins.
            var legolasSettingsPath = Path.Combine(localApp, "Mithril", "Legolas", "settings.json");
            if (GameConfigCarryOver.Apply(legolasSettingsPath, shellSettings))
                shellStore.SaveAsync(shellSettings).GetAwaiter().GetResult();

            // #957 one-time cross-file carry-over: the retired LegolasSettings.MapOverlay
            // (overlay position, DIUs) → ShellSettings.MapCaptureBbox (the one-rect
            // capture frame, physical px), so an upgrading user's overlay frame becomes
            // the capture frame instead of resetting to "no bbox". Idempotent — only
            // fires when the shell rect is still unset. DIU→physical needs a device
            // scale and no window exists yet, so use the system/primary DPI (exact for
            // uniform-DPI; mixed-DPI is #938 best-effort — re-snip to correct).
            if (MapCaptureRectCarryOver.Apply(legolasSettingsPath, shellSettings, SystemDpiScale()))
                shellStore.SaveAsync(shellSettings).GetAwaiter().GetResult();

            // Game config (live, observable)
            var gameConfig = new GameConfig
            {
                GameRoot = shellSettings.GameRoot,
                InstallRoot = shellSettings.InstallRoot,
                GameProcessName = shellSettings.GameProcessName,
                CalibrationGoodResidualPx = shellSettings.CalibrationGoodResidualPx,
            };
            gameConfig.PropertyChanged += (_, ev) =>
            {
                switch (ev.PropertyName)
                {
                    case nameof(GameConfig.GameRoot):
                        shellSettings.GameRoot = gameConfig.GameRoot;
                        break;
                    case nameof(GameConfig.InstallRoot):
                        shellSettings.InstallRoot = gameConfig.InstallRoot;
                        break;
                    case nameof(GameConfig.GameProcessName):
                        shellSettings.GameProcessName = gameConfig.GameProcessName;
                        break;
                    case nameof(GameConfig.CalibrationGoodResidualPx):
                        shellSettings.CalibrationGoodResidualPx = gameConfig.CalibrationGoodResidualPx;
                        break;
                }
            };

            // Build host
            var logDir = Path.Combine(shellDir, "logs");
            var perfDir = Path.Combine(shellDir, "perf");
            var referenceCacheDir = Path.Combine(localApp, "Mithril", "Reference");
            var communityCalibrationCacheDir = Path.Combine(localApp, "Mithril", "Reference", "CommunityCalibration");
            var iconCacheDir = Path.Combine(localApp, "Mithril", "Icons");
            var charactersRootDir = Path.Combine(localApp, "Mithril", "characters");
            var mapCalibrationDir = Path.Combine(localApp, "Mithril", "MapCalibration");
            // #914 PR-2: the out-of-process asset-extractor sidecar cache the
            // #931 base-texture + icon-template loaders read (BCL-only). Lives
            // alongside the other Mithril caches.
            var assetCacheDir = Path.Combine(localApp, "Mithril", "assets");

            var builder = Host.CreateApplicationBuilder(args);

            // Validate the DI graph at host-build time. Catches singleton-vs-singleton
            // cycles and missing dependencies as a clear exception at builder.Build()
            // instead of letting them manifest later as a hung App.OnStartup. Cost is
            // one eager singleton-resolution pass; upside is fail-fast with a precise
            // error trace instead of "stuck at 'creating App'".
            //
            // HostApplicationBuilder routes this through ConfigureContainer rather
            // than the older HostBuilder.UseDefaultServiceProvider extension.
            builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            }));

            // Cross-cutting user preferences (12/24h clock, future display
            // toggles). Lives in Mithril.Shared so modules can read it
            // without depending on Mithril.Shell. Persisted to
            // %LocalAppData%/Mithril/preferences.json — top-level so it's
            // shared across modules, not nested under Shell/.
            var preferencesPath = Path.Combine(localApp, "Mithril", "preferences.json");

            // AddMithrilApp = AddMithrilShell + AddWorldMergerStart (trailing).
            // The trailing-registration invariant (#696 Call 2) is what makes
            // each world's merger drain start strictly after every other hosted
            // service has completed its registration work.
            builder.Services.AddMithrilApp(new ShellCompositionOptions(
                preferencesPath, shellStore, shellSettings, gameConfig,
                logDir, perfDir, charactersRootDir, referenceCacheDir,
                communityCalibrationCacheDir, iconCacheDir, shellDir,
                mapCalibrationDir, assetCacheDir, Boot));

            Boot($"modules discovered: {builder.Services.Count(d => d.ServiceType == typeof(IMithrilModule))}");
            host = builder.Build();
            Boot("host built");

            // Construct the WPF Application BEFORE host.StartAsync().
            //
            // Under #695 Call 1 (eager-always state subscription) every
            // ingestion service's StartAsync resolves Application.Current?.Dispatcher
            // to bind the L1 driver's DeliveryContext.Marshaled — which means
            // host.StartAsync() now reads Application.Current during the
            // sequential hosted-service chain. If `new App()` runs AFTER
            // host.StartAsync, every Marshaled-vs-Inline fallback silently
            // selects Inline and L1 envelopes dispatch on the pump thread
            // instead of the WPF dispatcher, violating the cross-thread
            // ObservableCollection guarantee that #550 capability E was
            // introduced to provide. The fix is structural: construct App
            // here so the Dispatcher exists when the chain attaches its
            // subscriptions. Queued dispatcher operations sit in the
            // Application's queue until app.Run() starts the message loop
            // below; the bounded log backlog drains promptly then.
            Boot("creating App");
            var app = new App();
            app.Init(activateEvent, activateCts);
            app.InitializeComponent();

            host.StartAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            Boot("host started");

            // Persist active-character selection on every switch (log or UI).
            var activeCharSvc = host.Services.GetRequiredService<IActiveCharacterService>();
            activeCharSvc.ActiveCharacterChanged += (_, _) =>
            {
                try { shellStore.Save(shellSettings); } catch { /* best-effort */ }
            };

            host.Services.GetRequiredService<IReferenceDataService>().BeginBackgroundRefresh();

            // Community-calibration refresh is a single HTTP GET per module and fails gracefully
            // offline; always kick it. Per-module settings control merge behavior (PreferLocal /
            // Blend / PreferCommunity), not whether we fetch — users who want zero network can
            // firewall the host.
            host.Services.GetRequiredService<ICommunityCalibrationService>().BeginBackgroundRefresh();

            IconImage.SetCacheService(host.Services.GetRequiredService<IIconCacheService>());

            // Open gates for Eager modules
            var gates = host.Services.GetRequiredService<ModuleGates>();
            var discovered = host.Services.GetRequiredService<DiscoveredModules>();
            foreach (var module in discovered.Modules)
            {
                var eager = shellSettings.ModuleEagerOverrides.TryGetValue(module.Id, out var v)
                    ? v
                    : module.DefaultActivation == ActivationMode.Eager;
                if (eager) gates.For(module.Id).Open();
            }

            // App was constructed BEFORE host.StartAsync() above so the L1
            // dispatcher resolves correctly during the chain. Wire up the
            // host-dependent App fields now that host services are available.
            app.DeepLinkRouter = host.Services.GetRequiredService<IDeepLinkRouter>();

            _ = new UiFontApplier(app, shellSettings);

            // Auto-start a perf-trace session before the shell view-model resolves
            // (which is what fires the first ActivateModule). Anything earlier than
            // this — Velopack hooks, mutex, settings load, host.Build, host.StartAsync,
            // eager-module gate opens — is still only captured by boot.log; the
            // WPF-side hooks need Application.Current to attach, which exists
            // from the `new App()` above (now constructed before host.StartAsync
            // so the L1 ingestion services see a non-null Dispatcher during
            // their StartAsync per #695 Call 1).
            if (shellSettings.EnablePerfTrace && shellSettings.AutoStartPerfTrace)
            {
                try { host.Services.GetRequiredService<PerfRecorderHostedService>().Toggle(); }
                catch (Exception ex) { Boot($"perf auto-start failed: {ex.Message}"); }
            }

            Boot("resolving ShellWindow");
            var shell = host.Services.GetRequiredService<ShellWindow>();
            var shellViewModel = host.Services.GetRequiredService<ShellViewModel>();
            shell.DataContext = shellViewModel;
            shell.Left = shellSettings.WindowLeft;
            shell.Top = shellSettings.WindowTop;
            shell.Width = shellSettings.WindowWidth;
            shell.Height = shellSettings.WindowHeight;
            shell.SidebarWidth = shellSettings.SidebarWidth;
            app.MainWindow = shell;

            Boot("calling shell.Show()");
            shell.Show();
            // Activate the initial module now — after the window is shown and the
            // ShellViewModel singleton is fully constructed/cached. Doing this in the
            // VM ctor risks a re-entrant resolution deadlock (#365).
            shellViewModel.Initialize();
            shell.Closing += (_, _) =>
            {
                if (shell.WindowState == WindowState.Normal)
                {
                    shellSettings.WindowLeft = shell.Left;
                    shellSettings.WindowTop = shell.Top;
                    shellSettings.WindowWidth = shell.Width;
                    shellSettings.WindowHeight = shell.Height;
                }
                shellSettings.SidebarWidth = shell.SidebarWidth;
            };
            Boot("shell shown");

            var hwnd = new WindowInteropHelper(shell).Handle;
            var hk = host.Services.GetRequiredService<IHotkeyService>();
            hk.Attach(hwnd);
            hk.ReloadFromBindings(shellSettings.HotkeyBindings.Values);

            Boot("=== startup done ===");

            // Cold-start deep-link: the user launched us via a mithril:// URI and we are the
            // first instance. Dispatch once the shell is live. The warm-activation path is
            // handled inside App.WatchActivateEvent via the activation.uri drop-off.
            if (activationUri is not null)
            {
                app.Dispatcher.BeginInvoke(() => app.DeepLinkRouter?.Handle(activationUri));
            }

            app.Run(); // blocks until WPF shuts down

            // Cleanup — save settings (position already captured in Closing handler)
            try
            {
                shellStore.Save(shellSettings);
            }
            catch { }
        }
        catch (Exception ex)
        {
            ShowFatal(ex);
        }
        finally
        {
            try { activateCts?.Cancel(); activateCts?.Dispose(); } catch { }
            try { activateEvent?.Dispose(); } catch { }
            if (host is not null)
            {
#pragma warning disable VSTHRD002 // shutdown path, no dispatcher running
                try { host.StopAsync(TimeSpan.FromSeconds(2)).Wait(TimeSpan.FromSeconds(3)); } catch { }
#pragma warning restore VSTHRD002
                try { host.Dispose(); } catch { }
            }
            try { mutex?.ReleaseMutex(); } catch { }
            mutex?.Dispose();
        }
    }

    internal static void ShowFatal(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Mithril", "Shell", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "mithril-crash.log"),
                $"\n=== {DateTime.Now:s} ===\n{ex}\n");
        }
        catch { }
        System.Windows.MessageBox.Show(ex.ToString(), "Mithril crashed",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void Boot(string step)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BootLogPath)!);
            File.AppendAllText(BootLogPath, $"{DateTime.Now:HH:mm:ss.fff} {step}\n");
        }
        catch { }
    }

    private static string? ExtractActivationUri(string[] args)
    {
        foreach (var a in args)
        {
            if (!string.IsNullOrEmpty(a) &&
                a.StartsWith(MithrilUriSchemeRegistrar.Scheme + ":", StringComparison.OrdinalIgnoreCase))
                return a;
        }
        return null;
    }

    // The framework-dependent SKU expects Microsoft.WindowsDesktop.App 10.* on the host.
    // Without it the CoreCLR error message is incomprehensible; do a cheap probe of
    // C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App and surface a download link.
    private const string DotnetDownloadUrl = "https://dotnet.microsoft.com/download/dotnet/10.0";
    private const int RequiredMajorVersion = 10;

    private static bool DesktopRuntimeIsAvailable()
    {
        try
        {
            var roots = new[]
            {
                Environment.GetEnvironmentVariable("DOTNET_ROOT"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"),
            }.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p));

            foreach (var root in roots)
            {
                var dir = Path.Combine(root!, "shared", "Microsoft.WindowsDesktop.App");
                if (!Directory.Exists(dir)) continue;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    var dot = name.IndexOf('.');
                    if (dot <= 0) continue;
                    if (int.TryParse(name[..dot], out var major) && major >= RequiredMajorVersion)
                        return true;
                }
            }
            // Fallback: if the framework description says we are already loaded on
            // a >= net10 runtime, trust it. (This path won't normally hit because the
            // probe runs before host startup, but it covers self-contained-style hosts.)
            var fx = RuntimeInformation.FrameworkDescription;
            return fx.Contains(".NET 10", StringComparison.OrdinalIgnoreCase) ||
                   fx.Contains(".NET 11", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // If we can't probe, don't block launch — the user will see the CLR error and
            // we've at least tried to be helpful.
            return true;
        }
    }

    private static void ShowMissingRuntimeDialog()
    {
        var result = System.Windows.MessageBox.Show(
            "Mithril needs the .NET 10 Desktop Runtime, which doesn't appear to be installed on this machine.\n\n" +
            "Click OK to open the download page (look for \".NET Desktop Runtime 10.x\", x64).",
            "Missing .NET 10 Desktop Runtime",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (result == MessageBoxResult.OK)
        {
            try { Process.Start(new ProcessStartInfo(DotnetDownloadUrl) { UseShellExecute = true }); }
            catch { /* best-effort */ }
        }
    }
}
