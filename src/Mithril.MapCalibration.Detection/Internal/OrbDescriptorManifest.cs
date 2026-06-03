namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Per-area ORB descriptor cache manifest. Sits alongside
/// <c>map-texture-&lt;area&gt;.json</c>; the descriptor payload is the
/// DeflateStream-compressed sibling <c>map-texture-&lt;area&gt;.orb.bin</c>.
///
/// <para><b>Cache key.</b> A cached pair is valid iff:</para>
/// <list type="bullet">
/// <item><c>SchemaVersion</c> matches what the current binary expects.</item>
/// <item><c>PixelSha256</c> matches the sibling source texture's manifest
/// <c>PixelSha256</c> — cache invalidates whenever the texture is
/// rebuilt.</item>
/// <item><c>OrbParamsHash</c> matches the SHA-256 of the canonical ORB
/// param struct — cache invalidates whenever any param changes.</item>
/// <item>The actual <c>.orb.bin</c>'s SHA-256 matches
/// <c>BlobSha256</c> — guards against truncation / corruption.</item>
/// </list>
/// </summary>
internal sealed record OrbDescriptorManifest(
    int SchemaVersion,
    string Area,
    string? PgVersion,
    int KeypointCount,
    int DescriptorDim,        // 32 for ORB
    string OrbParamsHash,
    string PixelSha256,
    string BlobSha256);
