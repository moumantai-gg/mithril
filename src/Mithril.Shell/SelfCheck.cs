using System.IO;
using System.Linq;
using System.Threading;
using Mithril.Shared.Game;
using Mithril.Shared.Modules;
using Mithril.Shared.Settings;
using Mithril.Shell.DependencyInjection;
using Mithril.Shell.ViewModels;
using Mithril.Shell.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Mithril.Shell;

/// <summary>
/// <c>--selfcheck</c>: a process-isolated headless boot that composes the <em>real</em>
/// shell service graph (same <see cref="ShellComposition.AddMithrilShell"/> as the live
/// launch), builds the provider, and resolves every startup root — but does <strong>not</strong>
/// start hosted services (no CDN refresh) and never shows or runs the window. Its only job is
/// to prove the DI graph resolves without a re-entrant cycle (#365).
/// </summary>
/// <remarks>
/// A re-entrant factory-lambda cycle does not throw — Microsoft.Extensions.DI's
/// <c>StackGuard</c> offloads the unbounded recursion onto worker stacks and blocks forever,
/// so <c>ValidateOnBuild</c> can't see it (it only walks constructor call sites, not
/// factory delegates) and a plain resolve-and-assert would itself hang. The guard is
/// therefore a hard wall-clock watchdog on a separate thread: if resolution hasn't finished
/// by the deadline the process is killed with a distinctive exit code. Exit codes:
/// <c>0</c> all roots resolved; <c>1</c> a root threw; <c>2</c> watchdog timeout (the
/// silent-deadlock signature).
/// </remarks>
internal static class SelfCheck
{
    public const string Switch = "--selfcheck";

    public static int Run(string[] args)
    {
        var timeout = TimeSpan.FromSeconds(
            ArgValue(args, "--selfcheck-timeout-seconds", out var s) && int.TryParse(s, out var t)
                ? t
                : 120);

        // Hard watchdog: runs regardless of what the main thread is doing. A real
        // cycle hangs the main thread inside GetRequiredService forever, so the only
        // reliable escape is to terminate the process from elsewhere.
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(timeout);
            Console.Error.WriteLine(
                $"selfcheck: FAIL - DI root resolution did not complete within {timeout.TotalSeconds:0}s. " +
                "This is the silent re-entrant-cycle signature (#365): a factory-lambda graph " +
                "resolving back into a still-constructing singleton. Capture a managed stack " +
                "(dotnet-stack) of the hung thread to find the offending edge.");
            Environment.Exit(2);
        })
        { IsBackground = true, Name = "selfcheck-watchdog" };
        watchdog.Start();

        try
        {
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var shellDir = Path.Combine(localApp, "Mithril", "Shell");
            Directory.CreateDirectory(shellDir);

            var shellStore = new JsonSettingsStore<ShellSettings>(
                Path.Combine(shellDir, "shell.json"), ShellSettingsJsonContext.Default.ShellSettings);
#pragma warning disable VSTHRD002 // no dispatcher yet; mirrors Program's pre-host load
            var shellSettings = shellStore.LoadAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            // Mirror the real launch path's GameConfig seeding (Program.cs): seed
            // the relocated game-config fields from ShellSettings, not just
            // GameRoot, so this DI-graph self-check resolves against the same
            // config shape the app runs with. GameProcessName +
            // CalibrationGoodResidualPx were relocated here in #919 (#927).
            // PollIntervalSeconds is not persisted in ShellSettings, so it keeps
            // GameConfig's default — same as Program.cs. The carry-over migration
            // is deliberately NOT run in this headless, process-isolated check —
            // it only validates that the graph resolves, not runtime behaviour.
            var gameConfig = new GameConfig
            {
                GameRoot = shellSettings.GameRoot,
                GameProcessName = shellSettings.GameProcessName,
                CalibrationGoodResidualPx = shellSettings.CalibrationGoodResidualPx,
            };

            var options = new ShellCompositionOptions(
                PreferencesPath: Path.Combine(localApp, "Mithril", "preferences.json"),
                ShellStore: shellStore,
                ShellSettings: shellSettings,
                GameConfig: gameConfig,
                LogDir: Path.Combine(shellDir, "logs"),
                PerfDir: Path.Combine(shellDir, "perf"),
                CharactersRootDir: Path.Combine(localApp, "Mithril", "characters"),
                ReferenceCacheDir: Path.Combine(localApp, "Mithril", "Reference"),
                CommunityCalibrationCacheDir: Path.Combine(localApp, "Mithril", "Reference", "CommunityCalibration"),
                IconCacheDir: Path.Combine(localApp, "Mithril", "Icons"),
                ShellSettingsDir: shellDir,
                MapCalibrationDir: Path.Combine(localApp, "Mithril", "MapCalibration"),
                AssetCacheDir: Path.Combine(localApp, "Mithril", "assets"),
                ModuleLog: m => Console.Out.WriteLine($"selfcheck: {m}"));

            var builder = Host.CreateApplicationBuilder(
                args.Where(a => a != Switch).ToArray());
            // Same provider options as the live launch — validate what ships.
            builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            }));
            // AddMithrilApp = AddMithrilShell + AddWorldMergerStart (trailing
            // per #696 Call 2). Self-check validates the same composition the
            // real launch does, so any future trailing additions are caught
            // by the DI-cycle guard.
            builder.Services.AddMithrilApp(options);

            // Self-test hook (CI guard-of-the-guard, never set in prod): inject a
            // re-entrant factory-lambda cycle of the exact shape #365 was — two
            // singletons whose factories resolve each other. MS.DI's StackGuard
            // offloads the unbounded recursion and blocks, so the watchdog must be
            // what trips (exit 2). Proves the guard detects, not just that a clean
            // graph passes.
            if (Environment.GetEnvironmentVariable("MITHRIL_SELFCHECK_SELFTEST_CYCLE") == "1")
            {
                builder.Services.AddSingleton<CycleA>(sp => new CycleA(sp.GetRequiredService<CycleB>()));
                builder.Services.AddSingleton<CycleB>(sp => new CycleB(sp.GetRequiredService<CycleA>()));
            }

            using var host = builder.Build(); // does NOT StartAsync — no hosted services, no CDN

            if (Environment.GetEnvironmentVariable("MITHRIL_SELFCHECK_SELFTEST_CYCLE") == "1")
            {
                _ = host.Services.GetRequiredService<CycleA>(); // hangs → watchdog exit 2
            }

            // App provides Application.Current + App.xaml resource dictionaries that
            // view ctors (StaticResource) need. Resources only; never Run().
            var app = new App();
            app.InitializeComponent();

            int resolved = 0;
            var modules = host.Services.GetServices<IMithrilModule>().ToList();
            foreach (var m in modules)
            {
                host.Services.GetRequiredService(m.ViewType); resolved++;
                if (m.SettingsViewType is not null)
                {
                    host.Services.GetRequiredService(m.SettingsViewType); resolved++;
                }
            }

            // The roots Program resolves between "creating App" and "shell shown" —
            // the exact window #359's cycle bit (IDeepLinkRouter pulls every handler).
            _ = host.Services.GetRequiredService<IDeepLinkRouter>(); resolved++;
            resolved += host.Services.GetServices<IDeepLinkHandler>().Count();
            _ = host.Services.GetRequiredService<IModuleActivator>(); resolved++;
            _ = host.Services.GetRequiredService<ShellWindow>(); resolved++;
            var shellViewModel = host.Services.GetRequiredService<ShellViewModel>(); resolved++;
            // Exercise the activation path too: post-Layer-3 this is where module
            // ViewTypes resolve, so a cycle through ActivateModule is caught here.
            shellViewModel.Initialize();

            Console.Out.WriteLine(
                $"selfcheck: OK - {modules.Count} modules, {resolved} roots resolved, no DI cycle.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"selfcheck: FAIL - a startup root threw:\n{ex}");
            return 1;
        }
    }

    // Self-test cycle types (see MITHRIL_SELFCHECK_SELFTEST_CYCLE above). Mutually
    // factory-resolving singletons — the re-entrant pathology #365 guards against.
    private sealed class CycleA(CycleB b) { public CycleB B { get; } = b; }
    private sealed class CycleB(CycleA a) { public CycleA A { get; } = a; }

    private static bool ArgValue(string[] args, string name, out string? value)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length) { value = args[i + 1]; return true; }
            if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
            {
                value = args[i][(name.Length + 1)..]; return true;
            }
        }
        value = null;
        return false;
    }
}
