namespace Legolas.Domain;

/// <summary>
/// One projected reference (landmark/NPC) for the "Validate calibration"
/// overlay (#494/#495). <see cref="Pixel"/> is the area's persisted calibration
/// applied to the reference's true world coordinate via
/// <see cref="WorldToOverlayCalibration.ToOverlay(WorldCoord)"/> — rendered as a distinct hollow
/// magenta marker the user eyeballs against the real in-game map feature. A
/// consistent offset across markers is the diagnostic (recalibrate; usually a
/// map-zoom change). <see cref="Name"/> is drawn as a label beside the dot;
/// <see cref="ShowLabel"/> is the greedy-declutter decision (the dot always
/// draws — only crowded labels are suppressed; see
/// <see cref="GhostLabelDeclutter"/>). Rendered by a WPF ItemsControl layered
/// over the D2D surface (the live-overlay calibration-marker idiom); shared
/// VM↔view record.
/// </summary>
public readonly record struct GhostMarker(string Name, OverlayPixel Pixel, bool ShowLabel);
