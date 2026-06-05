// Mirror the production-side global usings so tests reference the lifted types
// (#836) without a per-file `using Mithril.MapCalibration;`. See
// src/Legolas.Module/GlobalUsings.cs for the rationale (incl. the #1076
// Phase 5a `PixelPoint` -> `OverlayPixel` rename and Phase 5b shim policy).
global using AreaCalibration = Mithril.MapCalibration.AreaCalibration;
global using CalibrationSource = Mithril.MapCalibration.CalibrationSource;
global using LandmarkCalibrationSolver = Mithril.MapCalibration.LandmarkCalibrationSolver;
global using OverlayPixel = Mithril.MapCalibration.OverlayPixel;
global using WorldCoord = Mithril.MapCalibration.WorldCoord;
global using WorldToOverlayCalibration = Mithril.MapCalibration.WorldToOverlayCalibration;
