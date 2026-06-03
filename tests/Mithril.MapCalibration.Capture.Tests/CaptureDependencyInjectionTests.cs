// Picker stopped consuming CalibrationGoodResidualPx in mithril#1046; this test
// uses it to configure the confidence gate wiring via GameConfig.
#pragma warning disable CS0618
using System.IO;
using System.Reflection;
using Arda.Contracts;
using Arda.World.Player;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Mithril.MapCalibration.Capture.DependencyInjection;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Internal;
using Mithril.Overlay;
using Mithril.Shared.Game;
using Mithril.Shared.Hotkeys;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// Task 28 (#914): the auto-capture pipeline resolves from a built provider with
/// fakes for the cross-cutting collaborators it doesn't own (IOverlayWindow,
/// IDomainEventSubscriber, IAreaState, IReferenceDataService). Exercises the full
/// AddMithrilMapCalibrationCapture registration graph — including the
/// GameConfig-wired gate override and the asset-engine registration — so a
/// missing/mis-shaped registration fails here, and ValidateOnBuild catches a
/// singleton cycle (memory: di_cycle_invisible_to_unit_tests).
/// </summary>
public sealed class CaptureDependencyInjectionTests
{
    [Fact]
    public void Auto_capture_pipeline_resolves_from_a_built_provider()
    {
        using var provider = BuildProvider(out _);

        provider.GetRequiredService<AutoCalibrationEngine>().Should().NotBeNull();

        var hotkeys = provider.GetServices<IHotkeyCommand>().ToList();
        hotkeys.Should().Contain(h => h.Id == "mapcalibration.capture");
        hotkeys.Should().Contain(h => h.Id == "mapcalibration.draw_bbox");

        // The GameConfig-wired gate must win over the engine's default gate
        // (last-registration-wins): a residual of 9.5 exceeds the configured 9.0.
        var gate = provider.GetRequiredService<ICalibrationConfidenceGate>();
        gate.Accept(new AreaCalibration(1, 0, 0, 0, 8, 9.5), 8, out _).Should().BeFalse();

        provider.GetRequiredService<AutoCalibrationTrigger>().Should().NotBeNull();
        provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .Should().Contain(s => s is AutoCalibrationTrigger);

        // #945 Gap 3: the one-time icon-template bootstrap is also a hosted service.
        // Asserts the AddHostedService line survives (guards a future regression that
        // drops it, leaving IconTemplateSet permanently Empty).
        provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .Should().Contain(s => s is IconTemplateBootstrap);
    }

    /// <summary>
    /// #945 Gap 1: the asset-extractor sidecar adapter is registered, so a
    /// base-texture cache-miss in <see cref="AutoCalibrationEngine"/> can actually
    /// invoke the sidecar (previously <c>GetService&lt;IAssetExtractor&gt;()</c>
    /// returned null and the cache-miss path short-circuited, so the cache never
    /// populated). It registers unconditionally (no File.Exists gate): the
    /// fail-soft when the exe is absent lives in <see cref="ProcessAssetExtractor"/>.
    /// </summary>
    [Fact]
    public void IAssetExtractor_is_registered_as_the_process_sidecar_adapter()
    {
        using var provider = BuildProvider(out _);

        var extractor = provider.GetRequiredService<IAssetExtractor>();
        extractor.Should().BeOfType<ProcessAssetExtractor>();
    }

    /// <summary>
    /// #945 Gap 1: the engine actually receives the registered extractor (not the
    /// optional <c>null</c> default). The engine resolves it the identical way the
    /// DI lambda does (<c>sp.GetService&lt;IAssetExtractor&gt;()</c>), so asserting
    /// the private field is non-null proves the cache-miss → sidecar path is live.
    /// Reflection is used here deliberately: there is no public accessor for the
    /// optional collaborator and a wiring test wants to prove the field, not behaviour.
    /// </summary>
    [Fact]
    public void Engine_receives_the_registered_asset_extractor()
    {
        using var provider = BuildProvider(out _);

        var engine = provider.GetRequiredService<AutoCalibrationEngine>();
        var field = typeof(AutoCalibrationEngine)
            .GetField("_assetExtractor", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull("the engine holds the optional sidecar extractor in a private field");
        field!.GetValue(engine).Should().BeOfType<ProcessAssetExtractor>();
    }

    /// <summary>
    /// #949: the per-attempt icon-template provider is registered (not the old eager
    /// <c>IconTemplateSet</c> singleton) and the engine consumes it. Resolving an
    /// empty cache yields an empty set (the intended fail-soft).
    /// </summary>
    [Fact]
    public void Icon_template_provider_is_registered_and_resolves_empty_over_an_empty_cache()
    {
        using var provider = BuildProvider(out _);

        var iconProvider = provider.GetRequiredService<IIconTemplateProvider>();
        iconProvider.Should().NotBeNull();
        iconProvider.GetTemplates().Templates.Should().BeEmpty("the temp asset cache is empty in this test");

        var engine = provider.GetRequiredService<AutoCalibrationEngine>();
        var field = typeof(AutoCalibrationEngine)
            .GetField("_iconTemplates", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull("the engine holds the icon-template provider in a private field");
        field!.GetValue(engine).Should().BeSameAs(iconProvider, "the engine consumes the registered provider");
    }

    /// <summary>
    /// Builds the full capture graph with faked cross-cutting collaborators, the
    /// same shape the shell composes (<see cref="ShellComposition"/> calls
    /// <c>AddMithrilMapCalibrationCapture(assetCacheDir)</c> with the real
    /// single-arg signature). <c>ValidateOnBuild</c> catches a singleton cycle
    /// (memory: di_cycle_invisible_to_unit_tests).
    /// </summary>
    private static ServiceProvider BuildProvider(out string assetCacheDir)
    {
        var settingsDir = Path.Combine(Path.GetTempPath(), "mithril-capture-di-" + Guid.NewGuid());
        assetCacheDir = Path.Combine(settingsDir, "assets");

        var services = new ServiceCollection();

        // The trigger is a hosted service → requires a non-optional ILogger
        // (CLAUDE.md), resolved via ILoggerFactory in the DI lambda. The shell
        // always registers logging; mirror that here so the graph resolves.
        services.AddLogging();

        // Cross-cutting collaborators the shell normally provides — faked here.
        services.AddSingleton(new GameConfig { CalibrationGoodResidualPx = 9.0, GameRoot = "" });
        services.AddSingleton<IOverlayWindow>(new FakeOverlayWindow());
        services.AddSingleton<IDomainEventSubscriber>(new FakeDomainEventSubscriber());
        services.AddSingleton<IAreaState>(new FakeAreaState("AreaSerbule"));
        // #1021: the engine now resolves IMapState (per-scene asset + sub-zone)
        // instead of IAreaState. AutoCalibrationTrigger still consumes IAreaState
        // (zone-change subscription), so both registrations remain.
        services.AddSingleton<IMapState>(new FakeMapState
        {
            CurrentArea = "AreaSerbule",
            CurrentMapScene = new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule"),
        });
        // #1041: the engine + trigger now consume an ISceneAssetCache. The real
        // composition wires it through AddMithrilMapCalibration; this test stops
        // short of that, so we register the in-memory fake directly.
        services.AddSingleton<ISceneAssetCache>(new FakeSceneAssetCache());
        services.AddSingleton<IReferenceDataService>(new FakeAreaReferenceData());
        services.AddSingleton<IMapCalibrationService>(new FakeCalibrationService());

        // The live single-arg signature is (assetCacheDir, pgVersion = null). Passing
        // just the cache dir mirrors ShellComposition.AddMithrilMapCalibrationCapture.
        services.AddMithrilMapCalibrationCapture(assetCacheDir);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
