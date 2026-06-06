using Arda.World.Player.Events;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;
using Mithril.MapCalibration;
using Mithril.Shared.Reference;

namespace Legolas.Tests.ViewModels;

public class CalibrationSessionViewModelTests
{
    private static CalibrationReference Ref(string name, double x, double z) =>
        new(name, "NPC", new WorldCoord(x, 0, z));

    [Fact]
    public void Refresh_pulls_references_and_status_from_service()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            Refs = { Ref("Marn", 10, 20), Ref("Yetta", -5, 8) },
        };
        var vm = new CalibrationSessionViewModel(svc);

        vm.References.Should().HaveCount(2);
        vm.StatusText.Should().Contain("not calibrated");
    }

    [Fact]
    public void Placing_references_accumulates_and_gates_solve_at_two()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            Refs = { Ref("A", 0, 0), Ref("B", 100, 0) },
        };
        var vm = new CalibrationSessionViewModel(svc);

        vm.CanSolve.Should().BeFalse();

        vm.SelectedReference = vm.References[0];
        vm.PlaceSelectedAtCommand.Execute(new OverlayPixel(10, 10));
        vm.CanSolve.Should().BeFalse();
        // The dropped reference stays the active target — visible & nudgeable.
        vm.SelectedReference.Should().Be(vm.References[0]);
        vm.SelectedPlacement.Should().NotBeNull();
        vm.CanNudge.Should().BeTrue();

        // Picking another reference swaps target; the first pin stays put.
        vm.SelectedReference = vm.References[1];
        vm.PlaceSelectedAtCommand.Execute(new OverlayPixel(200, 10));
        vm.Placements.Should().HaveCount(2);
        vm.Placements[0].X.Should().Be(10);   // untouched by the swap
        vm.CanSolve.Should().BeTrue();
        vm.SolveCommand.CanExecute(null).Should().BeTrue();

        // Done deselects: a stray click then warns instead of moving anything.
        vm.DeselectCommand.Execute(null);
        vm.SelectedReference.Should().BeNull();
        vm.SelectedPlacement.Should().BeNull();
        vm.CanNudge.Should().BeFalse();
        vm.PlaceSelectedAtCommand.Execute(new OverlayPixel(5, 5));
        vm.Placements.Should().HaveCount(2);  // nothing placed/moved
        vm.ClickWarning.Should().NotBeNull();
    }

    [Fact]
    public void Re_clicking_a_reference_replaces_its_placement_not_adds_a_second()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            Refs = { Ref("A", 0, 0) },
        };
        var vm = new CalibrationSessionViewModel(svc);

        vm.SelectedReference = vm.References[0];
        vm.PlaceSelectedAtCommand.Execute(new OverlayPixel(10, 10));
        // Selection persists, so clicking again repositions the same pin
        // (it's the clearly-indicated active target).
        vm.PlaceSelectedAtCommand.Execute(new OverlayPixel(50, 60));

        vm.Placements.Should().HaveCount(1);   // replaced, not appended
        vm.Placements[0].X.Should().Be(50);
        vm.Placements[0].Y.Should().Be(60);
    }

    [Fact]
    public void Solve_passes_placements_to_service_and_reports_residual()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            Refs = { Ref("A", 0, 0), Ref("B", 100, 0) },
            SolveResult = new AreaCalibration(1.0, 0, 0, 0, 2, 0.4),
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.SelectedReference = vm.References[0];
        vm.PlaceSelectedAtCommand.Execute(new OverlayPixel(0, 0));
        vm.SelectedReference = vm.References[1];
        vm.PlaceSelectedAtCommand.Execute(new OverlayPixel(100, 0));

        vm.SolveCommand.Execute(null);

        svc.LastSolvePairs.Should().HaveCount(2);
        vm.ResultText.Should().Contain("0.4 px");
    }

    [Fact]
    public void Solve_reports_high_residual_warning()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            Refs = { Ref("A", 0, 0), Ref("B", 100, 0) },
            SolveResult = new AreaCalibration(1.0, 0, 0, 0, 2, 47.0),
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.SelectedReference = vm.References[0];
        vm.PlaceSelectedAtCommand.Execute(new OverlayPixel(0, 0));
        vm.SelectedReference = vm.References[1];
        vm.PlaceSelectedAtCommand.Execute(new OverlayPixel(100, 0));

        vm.SolveCommand.Execute(null);

        vm.ResultText.Should().Contain("residual is high");
    }

    [Fact]
    public void Recalibrate_clears_persisted_calibration_and_placements()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            Refs = { Ref("A", 0, 0), Ref("B", 100, 0) },
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.SelectedReference = vm.References[0];
        vm.PlaceSelectedAtCommand.Execute(new OverlayPixel(1, 1));

        vm.RecalibrateCommand.Execute(null);

        svc.ClearCalled.Should().BeTrue();
        vm.Placements.Should().BeEmpty();
        vm.CanSolve.Should().BeFalse();
    }

    [Fact]
    public void Choosing_an_area_in_the_picker_drives_the_service()
    {
        var svc = new FakeService
        {
            Areas = { new AreaEntry("AreaEltibule", "Eltibule", ""), new AreaEntry("AreaSerbule", "Serbule", "") },
        };
        var vm = new CalibrationSessionViewModel(svc);

        vm.SelectedArea = vm.AvailableAreas.First(a => a.Key == "AreaSerbule");

        svc.SelectedAreaKey.Should().Be("AreaSerbule");
    }

    [Fact]
    public void Dead_click_sets_an_explanatory_warning_instead_of_silently_failing()
    {
        var noArea = new CalibrationSessionViewModel(new FakeService());
        noArea.PlaceSelectedAtCommand.Execute(new OverlayPixel(5, 5));
        noArea.ClickWarning.Should().Contain("No area");

        var withRefs = new CalibrationSessionViewModel(new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            Refs = { Ref("A", 0, 0) },
        });
        withRefs.PlaceSelectedAtCommand.Execute(new OverlayPixel(5, 5)); // nothing selected
        withRefs.ClickWarning.Should().Contain("Pick a landmark");
    }

    [Fact]
    public void Successful_placement_clears_warning_and_arms_nudge_target()
    {
        var vm = new CalibrationSessionViewModel(new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            Refs = { Ref("A", 0, 0) },
        });
        vm.PlaceSelectedAtCommand.Execute(new OverlayPixel(5, 5)); // sets a warning
        vm.ClickWarning.Should().NotBeNull();

        vm.SelectedReference = vm.References[0];
        vm.PlaceSelectedAtCommand.Execute(new OverlayPixel(40, 60));

        vm.ClickWarning.Should().BeNull();
        vm.SelectedPlacement.Should().NotBeNull();
        vm.SelectedPlacement!.X.Should().Be(40);
    }

    [Fact]
    public void NudgeSelected_moves_the_selected_placement_and_notifies()
    {
        var vm = new CalibrationSessionViewModel(new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            Refs = { Ref("A", 0, 0) },
        });
        vm.SelectedReference = vm.References[0];
        vm.PlaceSelectedAtCommand.Execute(new OverlayPixel(100, 100));
        var placed = vm.SelectedPlacement!;

        var xRaised = false;
        placed.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PlacedReference.X)) xRaised = true; };

        vm.NudgeSelected(-3, 5);

        placed.Pixel.X.Should().Be(97);
        placed.Pixel.Y.Should().Be(105);
        placed.X.Should().Be(97);
        xRaised.Should().BeTrue();
    }

    [Fact]
    public void ProjectLandmarks_ghosts_only_unplaced_refs_via_full_world_transform()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            Refs = { Ref("A", 10, 20), Ref("B", 40, -15) },
            // Full solved transform: scale 1, rot 0, world-origin pixel (100,100).
            CurrentCalibration = new AreaCalibration(1.0, 0.0, 100, 100, 2, 0.3) { MirrorNorth = false },
        };
        var vm = new CalibrationSessionViewModel(svc);

        // A is "used" (placed); only B should ghost.
        vm.SelectedReference = vm.References.First(r => r.Name == "A");
        vm.PlaceSelectedAtCommand.Execute(new OverlayPixel(0, 0));

        vm.ProjectLandmarksCommand.Execute(null);

        vm.GhostPins.Should().ContainSingle();
        var g = vm.GhostPins[0];
        g.Name.Should().Be("B");
        // East=40,North=-15 → px = 100 + 1*40 = 140 ; py = 100 - 1*(-15) = 115
        g.X.Should().BeApproximately(140, 1e-6);
        g.Y.Should().BeApproximately(115, 1e-6);

        vm.ClearGhostsCommand.Execute(null);
        vm.GhostPins.Should().BeEmpty();
    }

    [Fact]
    public void ProjectLandmarks_without_a_calibration_warns()
    {
        var vm = new CalibrationSessionViewModel(new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            Refs = { Ref("A", 1, 1) },
        });

        vm.ProjectLandmarksCommand.Execute(null);

        vm.GhostPins.Should().BeEmpty();
        vm.ClickWarning.Should().Contain("Solve a calibration");
    }

    [Fact]
    public void Offscreen_survey_pin_clamps_to_the_viewport_edge_and_re_evals_on_resize()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            CurrentCalibration = new AreaCalibration(1.0, 0.0, 0, 0, 3, 0.2),
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.SetViewport(200, 200);
        vm.SetPlayerPositionCommand.Execute(null);            // arm
        vm.ViewportClickedCommand.Execute(new OverlayPixel(50, 50)); // drop "you"

        svc.NoteSurvey("Far", new MetreOffset(East: 1000, North: 0));
        var far = vm.SurveyPins[0];
        far.OverlayPixel.X.Should().BeApproximately(1050, 1e-6); // true projection kept
        far.IsOffscreen.Should().BeTrue();
        far.DisplayX.Should().BeApproximately(186, 1e-6);        // clamped to 200-14
        far.DisplayY.Should().BeApproximately(50, 1e-6);

        svc.NoteSurvey("Near", new MetreOffset(East: 20, North: 0));
        var near = vm.SurveyPins[1];
        near.IsOffscreen.Should().BeFalse();
        near.DisplayX.Should().BeApproximately(70, 1e-6);

        vm.SetViewport(2000, 2000);
        far.IsOffscreen.Should().BeFalse();
        far.DisplayX.Should().BeApproximately(1050, 1e-6);
    }

    [Fact]
    public void Survey_projection_reflects_scale()
    {
        // Post-#1095: CalibrationZoom removed from AreaCalibration; the zoom-ratio
        // path is being reworked in P2.3. This test verifies that projection still
        // works at the calibrated scale (no zoom factor — scale is absolute).
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            CurrentCalibration = new AreaCalibration(1.0, 0.0, 0, 0, 3, 0.1),
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.MapZoom = 1.0;
        vm.SetPlayerPositionCommand.Execute(null);
        vm.ViewportClickedCommand.Execute(new OverlayPixel(0, 0));

        svc.NoteSurvey("Vein", new MetreOffset(East: 100, North: 0));
        var s = vm.SurveyPins[0];
        s.ProjX.Should().BeApproximately(100, 1e-6); // 100 × scale1, no zoom factor
    }

    [Fact]
    public void Solve_stamps_the_current_map_zoom_onto_the_calibration()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            Refs = { Ref("A", 0, 0), Ref("B", 100, 0) },
            SolveResult = new AreaCalibration(1.0, 0.0, 0, 0, 2, 0.2),
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.MapZoom = 0.39;
        vm.SelectedReference = vm.References[0];
        vm.PlaceSelectedAtCommand.Execute(new OverlayPixel(10, 10));
        vm.SelectedReference = vm.References[1];
        vm.PlaceSelectedAtCommand.Execute(new OverlayPixel(200, 10));

        vm.SolveCommand.Execute(null);

        svc.LastCalibrationZoom.Should().BeApproximately(0.39, 1e-9);
        svc.CurrentCalibration.Should().NotBeNull();
    }

    [Fact]
    public void Duplicate_survey_vector_is_deduped()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            CurrentCalibration = new AreaCalibration(1.0, 0.0, 0, 0, 3, 0.1),
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.SetPlayerPositionCommand.Execute(null);
        vm.ViewportClickedCommand.Execute(new OverlayPixel(100, 100));

        svc.NoteSurvey("Vein", new MetreOffset(50, 20));
        svc.NoteSurvey("Vein", new MetreOffset(51, 19));    // within 2m → dup
        vm.SurveyPins.Should().ContainSingle();
        vm.LastSurveyText.Should().Contain("duplicate");

        svc.NoteSurvey("Vein", new MetreOffset(80, 20));     // far enough → new
        vm.SurveyPins.Should().HaveCount(2);
        svc.NoteSurvey("Other", new MetreOffset(50, 20));    // diff name → new
        vm.SurveyPins.Should().HaveCount(3);
    }

    [Fact]
    public void Click_hit_test_selects_a_pin_for_drag_else_falls_through()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            Refs = { Ref("A", 0, 0) },
            CurrentCalibration = new AreaCalibration(1.0, 0.0, 0, 0, 3, 0.1),
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.SetPlayerPositionCommand.Execute(null);
        vm.ViewportClickedCommand.Execute(new OverlayPixel(300, 300)); // player

        // Within radius of the player pin → selects it.
        vm.TrySelectPinAt(new OverlayPixel(307, 304), 14).Should().BeTrue();
        vm.IsPlayerSelected.Should().BeTrue();

        // Empty space → no hit.
        vm.TrySelectPinAt(new OverlayPixel(10, 10), 14).Should().BeFalse();

        // A placement gets hit-selected too.
        vm.SelectedReference = vm.References[0];
        vm.PlaceSelectedAtCommand.Execute(new OverlayPixel(50, 60));
        vm.TrySelectPinAt(new OverlayPixel(52, 58), 14).Should().BeTrue();
        vm.SelectedPlacement.Should().NotBeNull();
        vm.IsPlayerSelected.Should().BeFalse(); // mutually exclusive
    }

    [Fact]
    public void DragSelectedTo_moves_the_pin_to_an_absolute_point()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            CurrentCalibration = new AreaCalibration(1.0, 0.0, 0, 0, 3, 0.1),
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.SetPlayerPositionCommand.Execute(null);
        vm.ViewportClickedCommand.Execute(new OverlayPixel(100, 100));
        svc.NoteSurvey("Vein", new MetreOffset(0, 50)); // proj (100,50)
        var s = vm.SurveyPins[0];

        vm.SelectedSurveyPin = s;
        vm.DragSelectedTo(new OverlayPixel(420, 360));
        s.OverlayX.Should().Be(420);
        s.OverlayY.Should().Be(360);
        s.Corrected.Should().BeTrue();

        // Drag the player pin (absolute) → survey re-projects from it.
        vm.SetPlayerPositionCommand.Execute(null); // selects existing player
        vm.DragSelectedTo(new OverlayPixel(200, 100));
        vm.PlayerPinX.Should().Be(200);
        s.ProjX.Should().BeApproximately(200, 1e-6); // re-projected
        s.OverlayX.Should().Be(420);                 // corrected → unaffected
    }

    [Fact]
    public void NudgeSelected_is_a_noop_with_nothing_selected()
    {
        var vm = new CalibrationSessionViewModel(new FakeService());
        vm.Invoking(v => v.NudgeSelected(1, 1)).Should().NotThrow();
    }

    [Fact]
    public void Nudging_player_pin_reprojects_uncorrected_survey_pins()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            CurrentCalibration = new AreaCalibration(1.0, 0.0, 0, 0, 3, 0.1),
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.SetPlayerPositionCommand.Execute(null);
        vm.ViewportClickedCommand.Execute(new OverlayPixel(200, 200));
        svc.NoteSurvey("Vein", new MetreOffset(East: 0, North: 50)); // proj (200,150)

        var s = vm.SurveyPins.Should().ContainSingle().Subject;
        s.ProjY.Should().BeApproximately(150, 1e-6);
        vm.IsPlayerSelected.Should().BeTrue(); // dropped → selected
        vm.CanNudge.Should().BeTrue();
        vm.NudgeTargetText.Should().Contain("your position");

        vm.NudgeSelected(10, 5); // move "you"

        vm.PlayerPinX.Should().Be(210);
        vm.PlayerPinY.Should().Be(205);
        s.ProjX.Should().BeApproximately(210, 1e-6);   // re-projected
        s.ProjY.Should().BeApproximately(155, 1e-6);
        s.OverlayX.Should().BeApproximately(210, 1e-6); // uncorrected → follows
        s.OverlayY.Should().BeApproximately(155, 1e-6);
    }

    [Fact]
    public void Correcting_a_survey_pin_makes_its_overlay_stick_through_player_moves()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            CurrentCalibration = new AreaCalibration(1.0, 0.0, 0, 0, 3, 0.1),
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.SetPlayerPositionCommand.Execute(null);
        vm.ViewportClickedCommand.Execute(new OverlayPixel(200, 200));
        svc.NoteSurvey("Vein", new MetreOffset(East: 0, North: 50));
        var s = vm.SurveyPins[0];

        vm.SelectedSurveyPin = s;            // select the survey pin
        vm.NudgeSelected(7, -3);             // drag its overlay onto the real ping
        s.Corrected.Should().BeTrue();
        s.OverlayX.Should().BeApproximately(207, 1e-6);
        s.OverlayY.Should().BeApproximately(147, 1e-6);

        // Re-select & move the player; corrected overlay must NOT follow.
        vm.SetPlayerPositionCommand.Execute(null); // player exists → selects it
        vm.IsPlayerSelected.Should().BeTrue();
        vm.NudgeSelected(50, 50);
        s.OverlayX.Should().BeApproximately(207, 1e-6); // stuck (real ping is fixed)
        s.OverlayY.Should().BeApproximately(147, 1e-6);
        s.ProjX.Should().BeApproximately(250, 1e-6);    // projection still moves
    }

    [Fact]
    public void Placement_nudge_still_works()
    {
        var vm = new CalibrationSessionViewModel(new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            Refs = { Ref("A", 0, 0) },
        });
        vm.SelectedReference = vm.References[0];
        vm.PlaceSelectedAtCommand.Execute(new OverlayPixel(100, 100));

        vm.CanNudge.Should().BeTrue();
        vm.NudgeSelected(-3, 4);

        vm.Placements[0].X.Should().Be(97);
        vm.Placements[0].Y.Should().Be(104);
    }

    [Fact]
    public void Active_nudge_target_is_singular_visible_and_named()
    {
        var vm = new CalibrationSessionViewModel(new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            Refs = { Ref("A", 0, 0), Ref("B", 100, 0) },
        });

        vm.NudgeTargetText.Should().BeNull(); // nothing armed yet

        vm.SelectedReference = vm.References[0];
        vm.PlaceSelectedAtCommand.Execute(new OverlayPixel(10, 10));
        var a = vm.Placements[0];
        a.IsSelected.Should().BeTrue();
        vm.NudgeTargetText.Should().Contain("A");

        vm.SelectedReference = vm.References[1];
        vm.PlaceSelectedAtCommand.Execute(new OverlayPixel(200, 10));
        var b = vm.Placements[1];
        // Exactly one is highlighted — the new one.
        b.IsSelected.Should().BeTrue();
        a.IsSelected.Should().BeFalse();
        vm.NudgeTargetText.Should().Contain("B");

        // Click-selecting the first row in the Placed list moves the highlight.
        vm.SelectedPlacement = a;
        a.IsSelected.Should().BeTrue();
        b.IsSelected.Should().BeFalse();

        // Clearing drops the target entirely.
        vm.ClearPlacementsCommand.Execute(null);
        vm.SelectedPlacement.Should().BeNull();
        vm.NudgeTargetText.Should().BeNull();
        a.IsSelected.Should().BeFalse();
    }

    [Fact]
    public void Armed_player_click_sets_the_player_pin_not_a_placement()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            Refs = { Ref("A", 0, 0) },
            CurrentCalibration = new AreaCalibration(1, 0, 0, 0, 2, 0.1),
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.SelectedReference = vm.References[0];

        vm.SetPlayerPositionCommand.Execute(null);            // arm
        vm.ViewportClickedCommand.Execute(new OverlayPixel(120, 80));

        vm.HasPlayerPin.Should().BeTrue();
        vm.PlayerPinX.Should().Be(120);
        vm.PlayerPinY.Should().Be(80);
        vm.IsPlayerSelected.Should().BeTrue();
        vm.Placements.Should().BeEmpty(); // it was a player click, not a placement
    }

    [Fact]
    public void Survey_projects_from_player_in_the_correct_direction()
    {
        // Identity calibration: north → up, east → right, from the player pin.
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            CurrentCalibration = new AreaCalibration(1.0, 0.0, 0, 0, 3, 0.2),
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.SetPlayerPositionCommand.Execute(null);
        vm.ViewportClickedCommand.Execute(new OverlayPixel(200, 200));

        svc.NoteSurvey("Iron Vein", new MetreOffset(East: 0, North: 50));
        var pin = vm.SurveyPins.Should().ContainSingle().Subject;
        pin.Name.Should().Be("Iron Vein");
        pin.ProjX.Should().BeApproximately(200, 1e-6);
        pin.ProjY.Should().BeApproximately(150, 1e-6);   // north → up
        pin.OverlayX.Should().BeApproximately(200, 1e-6); // overlay starts on projection
        pin.OverlayY.Should().BeApproximately(150, 1e-6);

        svc.NoteSurvey("Slab", new MetreOffset(East: 30, North: 0));
        vm.SurveyPins[1].ProjX.Should().BeApproximately(230, 1e-6); // east → right
        vm.SurveyPins[1].ProjY.Should().BeApproximately(200, 1e-6);
    }

    [Fact]
    public void Survey_without_calibration_or_player_records_why_no_pin()
    {
        var noCal = new FakeService { CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"), CurrentAreaFriendlyName = "Eltibule" };
        var vm1 = new CalibrationSessionViewModel(noCal);
        noCal.NoteSurvey("X", new MetreOffset(1, 1));
        vm1.SurveyPins.Should().BeEmpty();
        vm1.LastSurveyText.Should().Contain("X");
        vm1.LastSurveyText.Should().Contain("solve a calibration");

        var hasCal = new FakeService
        {
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            CurrentAreaFriendlyName = "Eltibule",
            CurrentCalibration = new AreaCalibration(1, 0, 0, 0, 2, 0),
        };
        var vm2 = new CalibrationSessionViewModel(hasCal);
        hasCal.NoteSurvey("X", new MetreOffset(1, 1)); // calibrated but no player pin
        vm2.SurveyPins.Should().BeEmpty();
        vm2.LastSurveyText.Should().Contain("Set player position");
    }

    [Fact]
    public void Survey_pin_math_text_reports_projected_vs_corrected_ratio()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            CurrentCalibration = new AreaCalibration(1.0, 0.0, 0, 0, 3, 0.1),
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.SetPlayerPositionCommand.Execute(null);
        vm.ViewportClickedCommand.Execute(new OverlayPixel(0, 0));
        svc.NoteSurvey("Vein", new MetreOffset(East: 100, North: 0)); // 100m → proj 100px
        var s = vm.SurveyPins[0];

        s.MathText.Should().Contain("100m");
        s.MathText.Should().Contain("proj=100px");
        s.MathText.Should().Contain("not yet corrected");

        vm.SelectedSurveyPin = s;
        vm.NudgeSelected(-50, 0); // drag corrected to 50px from player
        s.MathText.Should().Contain("corrected=50px");
        s.MathText.Should().Contain("ratio=0.500");      // half → implied scale halved
        s.MathText.Should().Contain("impliedScale=0.5000px/m");
    }

    // ---- #454 freehand pin calibration ----------------------------------

    private static CalibrationSessionViewModel ArmedPinVm(out FakeService svc, out TestDomainEventBus bus)
    {
        svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule"),
            SolveResult = new AreaCalibration(0.4, 0.1, 5, 6, 3, 0.0),
        };
        bus = new TestDomainEventBus();
        var vm = new CalibrationSessionViewModel(svc, bus);
        vm.TogglePinCalibrationCommand.Execute(null);
        vm.PinCalibrationArmed.Should().BeTrue();
        return vm;
    }

    [Fact]
    public void Disarmed_pins_are_dropped_so_area_entry_replay_cannot_leak()
    {
        var svc = new FakeService { CurrentScene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule") };
        var bus = new TestDomainEventBus();
        var vm = new CalibrationSessionViewModel(svc, bus);

        // Bulk area-entry replay arrives BEFORE the user arms — must be ignored.
        bus.Publish(new MapPinAdded(-521.96, 368.39, "", 0, 0, default));
        bus.Publish(new MapPinAdded(367.28, 2798.03, "", 0, 0, default));
        vm.PendingPinCount.Should().Be(0);

        // A click while disarmed/empty doesn't fabricate a placement.
        vm.ViewportClickedCommand.Execute(new OverlayPixel(100, 100));
        vm.Placements.Should().BeEmpty();
    }

    [Fact]
    public void Pins_pair_with_overlay_clicks_in_turn_order_never_by_label()
    {
        var vm = ArmedPinVm(out var svc, out var bus);

        // The bus hands the VM a bare world point — there is structurally
        // no label the VM could key off. Drop three distinct world points…
        var w0 = new WorldCoord(-521.96, 0, 368.39);
        var w1 = new WorldCoord(367.28, 0, 2798.03);
        var w2 = new WorldCoord(1145.39, 0, 1323.40);
        bus.Publish(new MapPinAdded(w0.X, w0.Z, "", 0, 0, default));
        bus.Publish(new MapPinAdded(w1.X, w1.Z, "", 0, 0, default));
        bus.Publish(new MapPinAdded(w2.X, w2.Z, "", 0, 0, default));
        vm.PendingPinCount.Should().Be(3);

        // …then click three overlay spots. Pairing is OLDEST-first (turn
        // order): click#i ↔ pin#i, irrespective of anything else.
        vm.ViewportClickedCommand.Execute(new OverlayPixel(10, 11));
        vm.ViewportClickedCommand.Execute(new OverlayPixel(20, 22));
        vm.ViewportClickedCommand.Execute(new OverlayPixel(30, 33));

        vm.Placements.Should().HaveCount(3);
        vm.PendingPinCount.Should().Be(0);
        vm.CanSolve.Should().BeTrue();

        vm.SolveCommand.Execute(null);
        svc.LastSolvePairs.Should().Equal(
            (w0, new OverlayPixel(10, 11)),
            (w1, new OverlayPixel(20, 22)),
            (w2, new OverlayPixel(30, 33)));
    }

    [Fact]
    public void ClearPlacements_disarms_and_drops_pending_pins()
    {
        var vm = ArmedPinVm(out _, out var bus);
        bus.Publish(new MapPinAdded(1, 2, "", 0, 0, default));
        vm.PendingPinCount.Should().Be(1);

        vm.ClearPlacementsCommand.Execute(null);

        vm.PinCalibrationArmed.Should().BeFalse();
        vm.PendingPinCount.Should().Be(0);
        // A pin arriving now is ignored again (disarmed).
        bus.Publish(new MapPinAdded(3, 4, "", 0, 0, default));
        vm.PendingPinCount.Should().Be(0);
    }

    private sealed class FakeService : IAreaCalibrationService
    {
        public List<CalibrationReference> Refs { get; } = new();
        public AreaCalibration? SolveResult { get; set; }
        public List<(WorldCoord, OverlayPixel)>? LastSolvePairs { get; private set; }
        public bool ClearCalled { get; private set; }

        public List<AreaEntry> Areas { get; } = new();
        public string? SelectedAreaKey { get; private set; }

        public MapSceneRef? CurrentScene { get; set; }
        public string? CurrentAreaFriendlyName { get; set; }
        public bool IsCurrentAreaCalibrated => CurrentCalibration is not null;
        public AreaCalibration? CurrentCalibration { get; set; }
        public WorldToOverlayCalibration? CurrentOverlayCalibration => CurrentCalibration is { } c
            ? new WorldToOverlayCalibration(c.OriginX, c.OriginY, c.Scale, c.RotationRadians, c.MirrorNorth)
            : null;
        public IReadOnlyList<CalibrationReference> CurrentAreaReferences => Refs;
        public IReadOnlyList<AreaEntry> AllAreas => Areas;
        public event EventHandler? Changed;

        public void SelectScene(MapSceneRef scene)
        {
            SelectedAreaKey = scene.ParentAreaKey;
            CurrentScene = scene;
            CurrentAreaFriendlyName = Areas.FirstOrDefault(a => a.Key == scene.ParentAreaKey)?.FriendlyName ?? scene.ParentAreaKey;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public double LastCalibrationZoom { get; private set; } = 1.0;

        public AreaCalibration? CalibrateCurrentArea(
            IReadOnlyList<(WorldCoord World, OverlayPixel Pixel)> placements,
            double calibrationZoom = 1.0)
        {
            LastSolvePairs = placements.Select(p => (p.World, p.Pixel)).ToList();
            LastCalibrationZoom = calibrationZoom;
            CurrentCalibration = SolveResult;
            return CurrentCalibration;
        }

        public void ClearCurrentAreaCalibration()
        {
            ClearCalled = true;
            CurrentCalibration = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler<CalibrationSurveyObservation>? SurveyObserved;

        public void NoteSurvey(string name, MetreOffset offset) =>
            SurveyObserved?.Invoke(this, new CalibrationSurveyObservation(name, offset));
    }
}
