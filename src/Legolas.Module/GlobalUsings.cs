// The per-area projection types were lifted to Mithril.MapCalibration (#836).
// A global using keeps the lift mechanical: existing Legolas code that wrote
// `AreaCalibration` / `WorldCoord` / `OverlayPixel` / `LandmarkCalibrationSolver`
// compiles unchanged. New code touching these types should still prefer an
// explicit `using Mithril.MapCalibration;` so the dependency direction is
// visible at the file level.
global using AreaCalibration = Mithril.MapCalibration.AreaCalibration;
global using CalibrationSource = Mithril.MapCalibration.CalibrationSource;
global using LandmarkCalibrationSolver = Mithril.MapCalibration.LandmarkCalibrationSolver;
global using OverlayPixel = Mithril.MapCalibration.OverlayPixel;
global using WorldCoord = Mithril.MapCalibration.WorldCoord;
global using WorldToOverlayCalibration = Mithril.MapCalibration.WorldToOverlayCalibration;
