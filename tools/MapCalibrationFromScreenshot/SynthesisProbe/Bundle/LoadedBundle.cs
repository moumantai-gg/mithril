namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;

internal sealed record LoadedBundle(
    string Directory,
    AttemptJson Attempt,
    MapRectJson? MapRect,
    RecoveredCalibrationJson? RecoveredCal,
    DetectionsJson? Detections,
    string? DeviationPath);
