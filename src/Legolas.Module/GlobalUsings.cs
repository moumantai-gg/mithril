// The per-area projection types were lifted to Mithril.MapCalibration (#836).
// A global using keeps the lift mechanical: existing Legolas code that wrote
// `AreaCalibration` / `WorldCoord` / `OverlayPixel` / `LandmarkCalibrationSolver`
// compiles unchanged. New code touching these types should still prefer an
// explicit `using Mithril.MapCalibration;` so the dependency direction is
// visible at the file level.
//
// #1076 Phase 5a/5b: the legacy `PixelPoint` alias was retired. All Legolas
// consumer code that used to write `PixelPoint` now writes `OverlayPixel`
// (per P.3 audit, every site in Legolas resolved to overlay-frame).
global using AreaCalibration = Mithril.MapCalibration.AreaCalibration;
global using CalibrationSource = Mithril.MapCalibration.CalibrationSource;
global using LandmarkCalibrationSolver = Mithril.MapCalibration.LandmarkCalibrationSolver;
global using OverlayPixel = Mithril.MapCalibration.OverlayPixel;
global using WorldCoord = Mithril.MapCalibration.WorldCoord;
global using WorldToOverlayCalibration = Mithril.MapCalibration.WorldToOverlayCalibration;
