using System.Collections.Generic;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// The single JSON line the asset-extractor sidecar writes to stdout on success
/// (issue #931). Mirrors the sidecar's emitter; parsed reflection-free via
/// <see cref="DetectionJsonContext"/>.
/// </summary>
internal sealed record SidecarResult(
    string Status,
    string? PgVersion,
    string? ExtractorVersion,
    List<SidecarArtifact> Artifacts);

internal sealed record SidecarArtifact(
    string Kind,
    string? Area,
    string Path,
    string PixelSha256);
