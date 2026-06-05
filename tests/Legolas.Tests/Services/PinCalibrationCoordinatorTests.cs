using Arda.World.Player;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;
using Mithril.MapCalibration;
using Mithril.Shared.Reference;
using Xunit;

namespace Legolas.Tests.Services;

/// <summary>
/// #477 Part A: the guided two-phase correctable walkthrough. One model, two
/// phases; spread-suggested pairing with skip/override; non-persisting residual
/// preview; correction (select/drag/nudge); terminal Confirm. The #454
/// label-agnostic rule still holds — only (world↔pixel) reaches the solver.
/// </summary>
public class PinCalibrationCoordinatorTests
{
    private static (PinCalibrationCoordinator coord, FakeCalib calib, FakeMapPinState pins, Mithril.Shared.Game.GameConfig gameConfig) Build()
    {
        var calib = new FakeCalib();
        var pins = new FakeMapPinState();
        var bus = new TestDomainEventBus();
        var settings = new LegolasSettings();
        var gameConfig = new Mithril.Shared.Game.GameConfig();
        return (new PinCalibrationCoordinator(calib, pins, bus, settings, gameConfig: gameConfig), calib, pins, gameConfig);
    }

    private static (PinCalibrationCoordinator coord, FakeCalib calib, FakeMapPinState pins, SessionState session) BuildWithSession()
    {
        var calib = new FakeCalib();
        var pins = new FakeMapPinState();
        var bus = new TestDomainEventBus();
        var settings = new LegolasSettings();
        var session = new SessionState();
        return (new PinCalibrationCoordinator(calib, pins, bus, settings, session), calib, pins, session);
    }

    // A well-conditioned scale-only transform: pixel = (100 + 2X, 100 - 2Z),
    // i.e. AreaCalibration{scale=2, origin=(100,100), no rotation, north=+Z}.
    private static PixelPoint Project(double x, double z) => new(100 + 2 * x, 100 - 2 * z);

    // Pair every remaining pin at its own perfect projection, in whatever
    // spread order the coordinator suggests (the no-correction baseline).
    private static void PairAllPerfectly(PinCalibrationCoordinator coord)
    {
        while (coord.SuggestedPin is { } s)
            coord.PairClick(Project(s.X, s.Z));
    }

    // ---- Phase model ----

    [Fact]
    public void Arm_starts_in_Drop_when_under_three_pins_else_Pair()
    {
        var (coord, _, pins, _) = Build();
        coord.Arm();
        coord.Phase.Should().Be(CalibrationPhase.Drop, "no pins yet");

        pins.SeedExisting(
            FakeMapPinState.Pin(1, 2), FakeMapPinState.Pin(3, 4), FakeMapPinState.Pin(5, 6));
        coord.Arm();
        coord.Phase.Should().Be(CalibrationPhase.Pair, "≥3 usable pins → skip Drop");
    }

    [Fact]
    public void Phase_toggle_flips_capture_intent_and_is_idempotent()
    {
        var (coord, _, _, _) = Build();
        coord.Arm(); // Drop
        coord.IsDropping.Should().BeTrue();
        coord.IsPairing.Should().BeFalse();

        coord.TogglePhase();
        coord.Phase.Should().Be(CalibrationPhase.Pair);
        coord.IsPairing.Should().BeTrue();
        coord.IsDropping.Should().BeFalse();

        coord.TogglePhase();
        coord.Phase.Should().Be(CalibrationPhase.Drop, "toggle is a clean flip-back");
    }

    [Fact]
    public void Phase1_live_count_reflects_PinSetChanged_Added()
    {
        var (coord, _, pins, _) = Build();
        coord.Arm();
        coord.PinsAvailable.Should().Be(0);
        pins.Add(1, 2);
        pins.Add(3, 4);
        coord.PinsAvailable.Should().Be(2);
        coord.HasUsablePins.Should().BeFalse();
        pins.Add(5, 6);
        coord.HasUsablePins.Should().BeTrue();
    }

    // ---- Spread suggestion + skip/override ----

    [Fact]
    public void Suggestion_is_the_farthest_unpaired_pin_from_already_paired()
    {
        var (coord, _, pins, _) = Build();
        var near = FakeMapPinState.Pin(11, 10);
        var far = FakeMapPinState.Pin(900, 900);
        pins.SeedExisting(FakeMapPinState.Pin(10, 10), near, far);
        coord.Arm(); // Pair (3 pins)

        // First suggestion (no pairs yet) is deterministic: list head.
        coord.SuggestedPin.Should().NotBeNull();
        coord.SuggestedPin!.Value.X.Should().Be(10);
        coord.PairClick(Project(10, 10));

        // Now the farthest-from-(10,10) unpaired pin wins.
        coord.SuggestedPin.Should().Be(far);
    }

    [Fact]
    public void Skip_defers_the_pin_without_recording_a_pair()
    {
        var (coord, _, pins, _) = Build();
        var a = FakeMapPinState.Pin(10, 10);
        var b = FakeMapPinState.Pin(20, 20);
        var c = FakeMapPinState.Pin(30, 30);
        pins.SeedExisting(a, b, c);
        coord.Arm();

        var first = coord.SuggestedPin;
        coord.SkipSuggestion();
        coord.PairedCount.Should().Be(0, "skip records nothing");
        coord.SuggestedPin.Should().NotBe(first, "a different pin is offered");
    }

    [Fact]
    public void Override_pin_takes_precedence_over_the_spread_suggestion()
    {
        var (coord, _, pins, _) = Build();
        var a = FakeMapPinState.Pin(10, 10);
        var b = FakeMapPinState.Pin(20, 20);
        var c = FakeMapPinState.Pin(30, 30);
        pins.SeedExisting(a, b, c);
        coord.Arm();

        coord.OverridePin = c;
        coord.SuggestedPin.Should().Be(c);
        coord.PairClick(Project(30, 30));
        // Solver only ever sees (world↔pixel) — verified on Confirm below.
        coord.PairedCount.Should().Be(1);
        coord.OverridePin.Should().BeNull("cleared once paired");
    }

    // ---- Pairing (implicit advance) + finalize floor ----

    [Fact]
    public void Pairing_is_implicit_advance_and_under_three_cannot_finalize()
    {
        var (coord, calib, pins, _) = Build();
        pins.SeedExisting(
            FakeMapPinState.Pin(10, 10),
            FakeMapPinState.Pin(50, 60),
            FakeMapPinState.Pin(90, 20));
        coord.Arm();

        coord.PairClick(Project(10, 10));
        coord.PairClick(Project(50, 60));
        coord.PairedCount.Should().Be(2);
        coord.CanConfirm.Should().BeFalse();
        coord.Confirm().Should().BeNull("≥3 pairs is a hard floor");
        coord.ConfirmAnyway().Should().BeNull();
        calib.LastPairs.Should().BeNull("nothing persisted below the floor");

        coord.PairClick(Project(90, 20));
        coord.CanConfirm.Should().BeTrue();
    }

    [Fact]
    public void Duplicate_world_point_is_rejected_to_keep_the_solve_conditioned()
    {
        var (coord, _, pins, _) = Build();
        var p = FakeMapPinState.Pin(10, 20, "A");
        pins.SeedExisting(p);
        coord.Arm();
        coord.TogglePhase(); // 1 pin → entered in Drop; move to Pair
        coord.OverridePin = p;
        coord.PairClick(new PixelPoint(1, 1));
        coord.OverridePin = p; // same pin again
        coord.PairClick(new PixelPoint(9, 9));
        coord.PairedCount.Should().Be(1);
    }

    // ---- Correction (select / drag / nudge) ----

    [Fact]
    public void Hit_test_selects_nearest_marker_and_drag_moves_only_that_pixel()
    {
        var (coord, calib, pins, _) = Build();
        pins.SeedExisting(
            FakeMapPinState.Pin(10, 10),
            FakeMapPinState.Pin(50, 60),
            FakeMapPinState.Pin(90, 20));
        coord.Arm();
        PairAllPerfectly(coord); // marker[0] is the first-suggested pin = (10,10)

        var other = coord.PlacedMarkers[1];
        var otherPixel = other.Pixel;

        coord.TrySelectMarkerAt(new PixelPoint(121, 81), radius: 14).Should().BeTrue();
        coord.SelectedMarker!.PairIndex.Should().Be(0);
        coord.SelectedMarker.Pixel.Should().Be(Project(10, 10));

        coord.DragSelectedTo(new PixelPoint(500, 500));
        coord.PlacedMarkers[0].Pixel.Should().Be(new PixelPoint(500, 500));
        other.Pixel.Should().Be(otherPixel, "other markers are untouched");

        coord.NudgeSelected(3, -2);
        coord.PlacedMarkers[0].Pixel.Should().Be(new PixelPoint(503, 498));

        // The world half is never mutated — Confirm proves the pairs carry the
        // dragged pixel against the ORIGINAL world coords.
        coord.Confirm(); // residual high after the drag → gated; force anyway
        coord.ConfirmAnyway();
        calib.LastPairs!.Should().Contain(p =>
            p.Item1 == new WorldCoord(10, 0, 10) && p.Item2 == new PixelPoint(503, 498));
        calib.LastPairs!.Should().Contain(p =>
            p.Item1 == new WorldCoord(50, 0, 60) && p.Item2 == Project(50, 60));
    }

    [Fact]
    public void Miss_returns_false_so_the_click_pairs_instead()
    {
        var (coord, _, pins, _) = Build();
        pins.SeedExisting(
            FakeMapPinState.Pin(10, 10),
            FakeMapPinState.Pin(50, 60),
            FakeMapPinState.Pin(90, 20));
        coord.Arm();
        coord.PairClick(Project(10, 10));
        coord.TrySelectMarkerAt(new PixelPoint(9999, 9999), radius: 14).Should().BeFalse();
    }

    // ---- Non-persisting residual preview ----

    [Fact]
    public void Residual_preview_is_non_persisting_and_equals_a_direct_solve()
    {
        var (coord, calib, pins, _) = Build();
        pins.SeedExisting(
            FakeMapPinState.Pin(10, 10),
            FakeMapPinState.Pin(50, 60),
            FakeMapPinState.Pin(90, 20));
        coord.Arm();

        coord.PreviewResidual.Should().BeNull("under 3 pairs");
        PairAllPerfectly(coord);

        coord.PreviewResidual.Should().NotBeNull();
        calib.LastPairs.Should().BeNull("preview must not persist");
        calib.ChangedCount.Should().Be(0, "preview must not fire Changed");

        // Equals a direct solve of the same (world↔pixel) pairs. Residual is
        // order-independent, so the suggestion order doesn't matter.
        static LandmarkCalibrationSolver.Reference Ref(double x, double z)
        {
            var px = Project(x, z);
            return new LandmarkCalibrationSolver.Reference(x, z, px.X, px.Y);
        }
        var refs = new[]
        {
            Ref(10, 10),
            Ref(50, 60),
            Ref(90, 20),
        };
        var direct = LandmarkCalibrationSolver.Solve(refs)!.ResidualPixels;
        coord.PreviewResidual!.Value.Should().BeApproximately(direct, 1e-6);
    }

    [Fact]
    public void Confirm_gate_flips_at_the_configured_threshold_and_finish_anyway_persists()
    {
        var (coord, calib, pins, gameConfig) = Build();
        pins.SeedExisting(
            FakeMapPinState.Pin(10, 10),
            FakeMapPinState.Pin(50, 60),
            FakeMapPinState.Pin(90, 20));
        coord.Arm();

        coord.PairClick(Project(10, 10));
        coord.PairClick(Project(50, 60));
        // One badly misplaced click → a large residual.
        coord.PairClick(new PixelPoint(Project(90, 20).X + 400, Project(90, 20).Y));

        coord.IsResidualGood.Should().BeFalse();
        coord.Confirm().Should().BeNull("gated on a good residual");
        calib.LastPairs.Should().BeNull();

        // Raise the threshold above the residual → the gate opens. #919: the
        // threshold now lives on the shared GameConfig, not LegolasSettings.
        gameConfig.CalibrationGoodResidualPx = coord.PreviewResidual!.Value + 10;
        coord.IsResidualGood.Should().BeTrue();
        coord.Confirm().Should().NotBeNull();
        coord.IsArmed.Should().BeFalse("a successful confirm disarms");
    }

    [Fact]
    public void No_correction_run_solves_equivalently_and_only_Confirm_persists()
    {
        var (coord, calib, pins, _) = Build();
        var a = FakeMapPinState.Pin(10, 10);
        var b = FakeMapPinState.Pin(50, 60);
        var c = FakeMapPinState.Pin(90, 20);
        pins.SeedExisting(a, b, c);
        coord.Arm();

        // Click each named dot precisely (the regression baseline).
        while (coord.SuggestedPin is { } s)
            coord.PairClick(Project(s.X, s.Z));

        calib.LastPairs.Should().BeNull("only Confirm persists");
        coord.Confirm().Should().NotBeNull();
        // Pure (world↔pixel); no label/colour/shape ever reaches the solver.
        calib.LastPairs.Should().BeEquivalentTo(new[]
        {
            (new WorldCoord(10, 0, 10), Project(10, 10)),
            (new WorldCoord(50, 0, 60), Project(50, 60)),
            (new WorldCoord(90, 0, 20), Project(90, 20)),
        });
    }

    // ---- #524: zoom stamp ------------------------------------------------

    [Fact]
    public void Persist_StampsCurrentMapZoom_FromSessionState()
    {
        var (coord, calib, pins, session) = BuildWithSession();
        pins.SeedExisting(
            FakeMapPinState.Pin(10, 10),
            FakeMapPinState.Pin(50, 60),
            FakeMapPinState.Pin(90, 20));
        session.CurrentMapZoom = 1.5;
        coord.Arm();
        // Walk the spread-suggested order so each pin maps to its OWN perfect
        // pixel (residual ~0 ⇒ Confirm is ungated).
        PairAllPerfectly(coord);

        var result = coord.Confirm();

        result.Should().NotBeNull();
        calib.LastCalibrationZoom.Should().Be(1.5, "the live in-game zoom must be stamped, not the pre-#524 hardcoded 1.0");
        result!.CalibrationZoom.Should().Be(1.5);
    }

    [Fact]
    public void Persist_WithoutSessionState_FallsBackToOne()
    {
        // Legacy ctor (no SessionState) still works — preserves headless /
        // unit-test paths and the pre-#524 default stamp.
        var (coord, calib, pins, _) = Build();
        pins.SeedExisting(
            FakeMapPinState.Pin(10, 10),
            FakeMapPinState.Pin(50, 60),
            FakeMapPinState.Pin(90, 20));
        coord.Arm();
        PairAllPerfectly(coord);

        coord.Confirm().Should().NotBeNull();
        calib.LastCalibrationZoom.Should().Be(1.0);
    }

    [Fact]
    public void Arm_clears_stale_state()
    {
        var (coord, _, pins, _) = Build();
        pins.SeedExisting(
            FakeMapPinState.Pin(10, 10),
            FakeMapPinState.Pin(50, 60),
            FakeMapPinState.Pin(90, 20));
        coord.Arm();
        coord.PairClick(Project(10, 10));
        coord.PairedCount.Should().Be(1);

        coord.Arm();
        coord.PairedCount.Should().Be(0);
        coord.PlacedMarkers.Should().BeEmpty();
        coord.PreviewResidual.Should().BeNull();
        coord.SelectedMarker.Should().BeNull();
    }

    private sealed class FakeCalib : IAreaCalibrationService
    {
        public List<(WorldCoord, PixelPoint)>? LastPairs { get; private set; }
        public double LastCalibrationZoom { get; private set; }
        public int ChangedCount { get; private set; }
        /// <summary>If set, CalibrateCurrentArea throws this instead of solving. Round-4 review #2.</summary>
        public Exception? ThrowOnCalibrate { get; set; }

        public AreaCalibration? CalibrateCurrentArea(
            IReadOnlyList<(WorldCoord World, PixelPoint Pixel)> placements, double calibrationZoom = 1.0)
        {
            if (ThrowOnCalibrate is { } ex) throw ex;
            LastPairs = placements.Select(p => (p.World, p.Pixel)).ToList();
            LastCalibrationZoom = calibrationZoom;
            ChangedCount++;
            Changed?.Invoke(this, EventArgs.Empty);
            return new AreaCalibration(1, 0, 0, 0, placements.Count, 0) { CalibrationZoom = calibrationZoom };
        }

        public MapSceneRef? CurrentScene => new MapSceneRef("AreaTest", null, "Map_AreaTest");
        public string? CurrentAreaFriendlyName => "Test";
        // Default true so existing PinCalibrationCoordinatorTests (which
        // pre-date the #835 step 6 review-iter-1 B2 Arm gate) stay green:
        // their setups assume Arm always succeeds. The new gate-rejection
        // tests opt-in by flipping this to false.
        public bool IsCurrentAreaCalibrated { get; set; } = true;
        public AreaCalibration? CurrentCalibration => null;
        public IReadOnlyList<CalibrationReference> CurrentAreaReferences => Array.Empty<CalibrationReference>();
        public IReadOnlyList<AreaEntry> AllAreas => Array.Empty<AreaEntry>();
        public event EventHandler? Changed;
        public void SelectScene(MapSceneRef scene) { }
        public void ClearCurrentAreaCalibration() { }
        public void NoteSurvey(string name, MetreOffset offset) { }
        public event EventHandler<CalibrationSurveyObservation>? SurveyObserved { add { } remove { } }
    }

    [Fact]
    public void Confirm_on_IOException_sets_PersistError_returns_null_and_stays_armed()
    {
        // Round-4 review #2: pins the coordinator's IOException catch contract.
        // A persist failure must not crash the WPF command path; the
        // coordinator stays armed so the user's pairs aren't lost on retry.
        var (coord, calib, pins, _) = Build();
        pins.Add(0, 0, "A");
        pins.Add(50, 50, "B");
        pins.Add(100, 0, "C");
        coord.Arm();
        PairAllPerfectly(coord);
        coord.CanConfirm.Should().BeTrue();
        calib.ThrowOnCalibrate = new System.IO.IOException("simulated AV lock on refinements.json.tmp");

        var result = coord.Confirm();

        result.Should().BeNull("Confirm catches IOException and returns null instead of throwing");
        coord.PersistError.Should().NotBeNull().And.Contain("simulated AV lock");
        coord.IsArmed.Should().BeTrue("coordinator stays armed so placed pairs aren't lost on retry");
        coord.PairedCount.Should().Be(3, "pairs are preserved across a failed Confirm");
    }

    // #835 step 6 iter-1 touch-up: the bootstrap-gate Arm() tests that
    // lived here were retired alongside the gate itself. The dissolved-#868
    // framing in the #835 issue body is explicit ("no seed-calibration
    // requirement"); calibration placement pins now draw pixel-native via
    // LegolasOverlaySceneDrawer.DrawCalibrationPlacementPins, so the
    // walkthrough works in both calibrated and uncalibrated areas. The
    // tests that asserted the gate refused on uncalibrated areas + that a
    // stale BootstrapBlockedMessage cleared on success contradicted the
    // dissolution and were removed when the gate was reverted.

    private sealed class TestLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
    {
        public System.Collections.Concurrent.ConcurrentQueue<TestLogEntry> Entries { get; } = new();
        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) =>
            new TestLogger(categoryName, Entries);
        public void Dispose() { }

        private sealed class TestLogger : Microsoft.Extensions.Logging.ILogger
        {
            private readonly string _category;
            private readonly System.Collections.Concurrent.ConcurrentQueue<TestLogEntry> _sink;
            public TestLogger(string c, System.Collections.Concurrent.ConcurrentQueue<TestLogEntry> s) { _category = c; _sink = s; }
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
                Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => _sink.Enqueue(new TestLogEntry(_category, logLevel, formatter(state, exception), exception));

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }

    private sealed record TestLogEntry(string Category, Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Exception);

    [Fact]
    public void Disarm_clears_PersistError()
    {
        // Round-4 review #3: PersistError must not linger when the user backs
        // out of a failed Confirm without re-Arming.
        var (coord, calib, pins, _) = Build();
        pins.Add(0, 0, "A");
        pins.Add(50, 50, "B");
        pins.Add(100, 0, "C");
        coord.Arm();
        PairAllPerfectly(coord);
        calib.ThrowOnCalibrate = new System.IO.IOException("simulated");
        coord.Confirm();
        coord.PersistError.Should().NotBeNull();

        coord.Disarm();

        coord.PersistError.Should().BeNull("Disarm hygiene mirrors Arm");
    }
}
