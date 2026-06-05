using System.Windows;
using System.Windows.Controls;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Hotkeys;
using Legolas.Services;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;
using Mithril.Overlay;
using Xunit;

namespace Legolas.Tests.Hotkeys;

/// <summary>
/// #835 step 6 review iteration-1 B4: smoke test for the survey-drag input
/// pipeline ported from <c>MapOverlayView</c> onto the shared overlay's
/// <c>ViewportRoot</c>. A real WPF <c>MouseButtonEventArgs</c> can't be
/// constructed without a full visual-tree dispatcher, so this test drives
/// the controller's state machine through the <c>TestSimulate*</c> seams
/// which run the exact production handler bodies minus the WPF args
/// plumbing.
/// </summary>
public sealed class OverlayControllerInputSmokeTests
{
    [Fact]
    public void Down_then_up_invokes_CorrectSurveyCommand_with_final_pixel() => RunOnSta(() =>
    {
        var (controller, vm, viewport, _) = BuildHarness();

        // Seed a survey + select it so the down-handler's
        // "selected != null && !Collected && !Skipped" branch fires.
        var survey = SeedSurvey(vm.Session, "p0", world: (10, 20));
        vm.Session.SelectedSurvey = survey;

        controller.TestInjectMapVmAndViewport(vm, viewport);

        var downPos = new Point(40, 50);
        var upPos = new Point(140, 250);

        controller.TestSimulateMouseDown(downPos)
            .Should().BeTrue("a selected, uncollected survey was clicked — handler must claim the drag.");
        controller.TestSimulateMouseUp(upPos);

        // CorrectSurveyCommand body: vm.UpdateModel(...) with the new
        // pixel as ManualOverride. EffectivePixel resolves to the
        // ManualOverride first, so the survey's pixel must reflect the
        // mouse-up coordinate exactly — this is what the legacy view's
        // viewport-up handler shipped, and what users will see in PG.
        survey.Model.ManualOverride.Should().NotBeNull();
        survey.Model.ManualOverride!.Value.X.Should().Be(upPos.X);
        survey.Model.ManualOverride!.Value.Y.Should().Be(upPos.Y);
    });

    [Fact]
    public void Up_without_active_drag_is_a_noop() => RunOnSta(() =>
    {
        var (controller, vm, viewport, _) = BuildHarness();
        SeedSurvey(vm.Session, "p0", world: (10, 20));
        controller.TestInjectMapVmAndViewport(vm, viewport);

        // No prior Down — Up must not invoke CorrectSurveyCommand. The
        // legacy view's Viewport.IsMouseCaptured guard mapped to "we
        // never entered the drag" — same behaviour here.
        controller.TestSimulateMouseUp(new Point(99, 99));

        var leftover = vm.Session.Surveys.Single();
        leftover.Model.ManualOverride.Should().BeNull(
            "an orphaned mouse-up must not commit a phantom correction.");
    });

    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (captured is not null) throw captured;
    }

    private static (OverlayController controller, MapOverlayViewModel vm, FrameworkElement viewport, ServiceCollectionStub _) BuildHarness()
    {
        var session = new SessionState();
        session.CurrentMapZoom = 1.0;
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var areaState = new FakeAreaState { CurrentArea = "AreaTest" };
        var vm = new MapOverlayViewModel(
            session, projector, optimizer, surveyFlow, brushes, settings,
            pinCalibration: null, positionState: null, bus: null,
            areaCalibration: null, motherlode: null, characterPin: null,
            markers: null, areaState: areaState);

        // OverlayController is normally constructed via DI. For this smoke
        // test we don't run StartAsync — the input pipeline is exercised
        // through the test seams which bypass DI / dispatcher entirely.
        // Build a minimal SP stub that supplies just MapOverlayViewModel.
        // The TestSimulate seams only touch _wiredMapVm / _viewportRoot —
        // the other ctor params are never read on this path. Pass null!
        // for the heavyweight ones (ForegroundFocusGate / SettingsAutoSaver
        // pull in DispatcherTimer + Win32 hooks) and a real ModuleGates
        // since it's cheap and the type isn't nullable.
        var stub = new ServiceCollectionStub(vm);
        var moduleGates = new Mithril.Shared.Modules.ModuleGates();
        var overlayWindow = new FakeOverlayWindow();
        var controller = new OverlayController(
            stub, moduleGates, session, settings, focusGate: null!, overlayWindow,
            settingsSaver: null!, captureRectStore: null!);

        // The "viewport" stands in for the shared window's ViewportRoot.
        // The TestSimulate* seams don't read its rendered surface, only
        // its presence (null guard). A bare ContentControl satisfies the
        // FrameworkElement contract. The test runs on an STA thread via
        // RunOnSta because WPF DispatcherObject construction needs it.
        var viewport = new ContentControl();
        return (controller, vm, viewport, stub);
    }

    private static SurveyItemViewModel SeedSurvey(SessionState session, string name, (double X, double Z) world)
    {
        var w = new WorldCoord(world.X, 0, world.Z);
        var pixel = new OverlayPixel(world.X, world.Z);
        var model = Survey.CreateAbsolute(name, w, pixel, gridIndex: 0);
        var vm = new SurveyItemViewModel(model);
        session.Surveys.Add(vm);
        return vm;
    }

    /// <summary>Minimal IServiceProvider that only resolves MapOverlayViewModel
    /// (the only DI lookup the input wiring does). Other resolutions return
    /// null — the test seams don't exercise the StartAsync flow.</summary>
    private sealed class ServiceCollectionStub : IServiceProvider
    {
        private readonly MapOverlayViewModel _vm;
        public ServiceCollectionStub(MapOverlayViewModel vm) { _vm = vm; }
        public object? GetService(Type serviceType)
            => serviceType == typeof(MapOverlayViewModel) ? _vm : null;
    }

    /// <summary>The TestSimulate* seams never touch <c>Window</c>, so a
    /// throwing accessor is safe — and avoids the <c>new Window()</c>
    /// STA-thread requirement the WPF runtime would otherwise impose on
    /// this test.</summary>
    private sealed class FakeOverlayWindow : IOverlayWindow
    {
        public Window Window => throw new InvalidOperationException(
            "FakeOverlayWindow.Window must not be accessed in the input smoke test — " +
            "the TestSimulate* seams bypass the Window-bound wiring path.");
        public bool IsReady => true;
        public string? StatusMessage => null;
        public void SetStatusMessage(string? message) { }
        public IDisposable RegisterScene(Action<IOverlaySceneContext> draw) => new NoopDisposable();
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
