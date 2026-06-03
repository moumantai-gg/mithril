using System.Collections.Generic;
using System.IO;
using System.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Mithril.MapCalibration.Capture.Diagnostics;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// PR-4 Task 22 (#1009 cutover proof): the load-bearing end-to-end test that the
/// production-wired <see cref="AutoCalibrationEngine"/> — with
/// <see cref="FeatureMatchingRefiner"/> as <see cref="IMapRegionRefiner"/> — now
/// calibrates the same Kur Mountains live capture that the legacy NCC ladder
/// rejected with the "rejected-map-not-located" outcome (NCC peak 0.473, below
/// the 0.65 floor).
///
/// <para><b>Scope.</b> This is the engine-level analogue of
/// <see cref="FeatureMatchingRefinerReplayTests.Recovers_kur_mountains_live_ground_truth_rect_within_two_pixels"/>.
/// The replay test proves the refiner-in-isolation green; this test proves the
/// engine path uses it correctly: it calls Refine on the captured frame + base
/// texture, surfaces a non-null <see cref="CalibrationAttemptContext.LocatorRawFit"/>
/// and high-inlier <see cref="CalibrationAttemptContext.LocatorMetrics"/>, and the
/// attempt outcome is NOT <see cref="OutcomeVocabulary.RejectedMapNotLocated"/>
/// (the #1009 bug).</para>
///
/// <para><b>Solve stub.</b> The detect→solve stage downstream of locate is stubbed
/// with a <see cref="SpySolver"/> returning a successful
/// <see cref="CalibrationSolveResult"/> — exercising the engine all the way to
/// persistence proves the refiner cutover doesn't break any downstream wiring (the
/// engine's runtime cast to <see cref="FeatureMatchingRefiner"/> for SetAreaKey,
/// the metrics threading into the attempt context, and the accept path). Detection
/// inputs (icon templates, real references) are not exercised here — the
/// refiner-in-isolation replay tests cover the actual ORB+RANSAC math; this is
/// the wiring proof.</para>
/// </summary>
public sealed class AutoCalibrationEngineFeatureMatchingTests
{
    private const string Area = "AreaKurMountains";

    private static readonly string FixturesRoot = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "CalibrationBundles");

    private readonly ITestOutputHelper _output;

    public AutoCalibrationEngineFeatureMatchingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Kur_live_bundle_now_calibrates_under_feature_matching()
    {
        var bundleDir = Path.Combine(FixturesRoot, "KurMountains-Live-20260602");
        var capture = PngFixtureLoader.LoadGray(Path.Combine(bundleDir, "capture.png"));

        // Resolve the Kur base texture from the fixture-rooted #931 provider — same
        // pattern the refiner-in-isolation replay tests use. Real on-disk decode,
        // no fakes.
        var baseTextureProvider = new ServiceCollection()
            .AddMithrilMapCalibrationDetection(bundleDir)
            .BuildServiceProvider()
            .GetRequiredService<IBaseTextureProvider>();
        var baseTexture = baseTextureProvider.TryGetBaseTexture(Area)
                          ?? throw new InvalidOperationException(
                              $"Kur fixture: no base texture for area {Area}");

        // Real production refiner (the cutover under test).
        var refiner = new FeatureMatchingRefiner(new MapCalibrationLocateOptions());

        // Capture sink: pulls the per-attempt context out so we can assert the
        // headline locate metrics + outcome.
        var captured = new List<CalibrationAttemptContext>();
        var sinkSelector = new CalibrationAttemptBundleSinkSelector(
            new CaptureDiagnosticsOptions { DumpCalibrationBundles = true },
            new CapturingSink(captured),
            new CapturingSink(captured));

        // Solve stub: returns an accepted CalibrationSolveResult so the engine
        // reaches the accept path. The actual solve math is not exercised here —
        // proving Refine wires correctly is the goal; the SolveEngine has its own
        // tests.
        var solver = new SpySolver(new CalibrationSolveResult(
            new AreaCalibration(1.0, 0, 0, 0, 6, 0.5), InlierCount: 6, RejectReason: null));

        // bbox size matches the captured frame so the SpyCapture's CapturedFrame
        // dims agree with the engine's capture-validation expectations. The bbox
        // origin is arbitrary (capture is faked).
        var bbox = new CaptureRect(0, 0, capture.Width, capture.Height);

        // #1021: the engine now reads IMapState. The Kur fixture's texture is
        // keyed on the area name (legacy fixture naming), so we set
        // CurrentMapAsset = Area to match — both pre- and post-#1021 the lookup
        // key for this fixture is "AreaKurMountains".
        var engine = new AutoCalibrationEngine(
            new FakeMapState { CurrentArea = Area, CurrentMapScene = new MapSceneRef(Area, null, Area) },
            new FakeSceneAssetCache(),
            new FakeWindowLocator(new GameWindow(1, new CaptureRect(0, 0, 1920, 1080))),
            new FakeRegionProvider(bbox),
            new SpyCapture(capture),
            refiner,
            new FakeBaseTextureProvider(baseTexture),
            new FakeAreaRefs(new[] { new LandmarkReference("landmark_npc", "x", new WorldCoord(1, 0, 1)) }),
            solver,
            new FakeIconTemplateProvider(IconTemplateSet.Empty),
            new FakeCalibrationService(),
            logger: null,
            sinkSelector: sinkSelector);

        var outcome = await engine.TryCalibrateCurrentAreaAsync(CancellationToken.None);

        captured.Should().HaveCount(1, "the sink writes exactly one bundle per attempt");
        var ctx = captured[0];

        // === The #1009 proof: locate stage no longer rejects on Kur live ===
        ctx.Outcome.Should().NotBe(OutcomeVocabulary.RejectedMapNotLocated,
            "the FeatureMatchingRefiner cutover (#1009) must locate the Kur live capture "
            + "that the legacy NCC ladder rejected at peak NCC 0.473");

        // Locator metrics were threaded through to the attempt context — same
        // shape as the refiner-in-isolation replay test asserts on the refiner
        // directly. High inlier count proves a clean ORB+RANSAC fit, not a
        // marginal/noisy one.
        ctx.LocatorRawFit.Should().NotBeNull(
            "Refine populates RawFitRect on both accept + reject paths under FM");
        ctx.LocatorMetrics.Should().NotBeNull(
            "Refine populates Metrics on both accept + reject paths under FM");
        ctx.LocatorMetrics!.InlierCount.Should().BeGreaterThan(500,
            "the Kur fixture clears the gate floor comfortably (replay test observes ~700+ inliers)");
        ctx.LocatorMetrics.InlierRatio.Should().BeGreaterThan(0.90,
            "the Kur live capture is synthetic-clean — the FM ratio test should be very high");

        // Engine reached the solve stage and persisted (since the solve was
        // stubbed Accepted). This proves the full pipeline post-refiner wiring
        // still works under the cutover.
        outcome.Persisted.Should().BeTrue(
            "with locate green + solve stubbed Accepted, the engine should persist");
        outcome.AreaKey.Should().Be(Area);
        outcome.RejectReason.Should().BeNull();
        ctx.Outcome.Should().Be(OutcomeVocabulary.Accepted);

        // Diagnostic dump for the test log — makes a future regression's symptoms
        // immediately legible without re-running the replay test.
        var m = ctx.LocatorMetrics!;
        _output.WriteLine(
            $"Kur live engine attempt: outcome={ctx.Outcome}, persisted={outcome.Persisted}, "
            + $"inliers={m.InlierCount}/{m.CandidateCount} (ratio={m.InlierRatio:0.000}), "
            + $"scale={m.Scale:0.0000}, rotation={m.RotationDegrees:0.000}°, "
            + $"residual={m.ResidualPixels:0.00} px.");
    }

    /// <summary>Test-double sink: records every Write call.</summary>
    private sealed class CapturingSink : ICalibrationAttemptBundleSink
    {
        private readonly List<CalibrationAttemptContext> _captured;
        public CapturingSink(List<CalibrationAttemptContext> captured) => _captured = captured;
        public void Write(CalibrationAttemptContext context) => _captured.Add(context);
    }
}
