using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Mithril.MapCalibration;

/// <summary>
/// The single <c>src/**</c> touchpoint that knows the out-of-process
/// asset-extractor sidecar exists (issue #931). It runs the sidecar
/// (<c>tools/Mithril.AssetExtractor</c>), which decodes PG assets with
/// AssetsTools.NET + System.Drawing in a child process and writes the
/// pre-decoded manifest+blob cache to disk; the app then loads that cache
/// BCL-only. The contract is the manifest schema + the JSON/exit-code result,
/// NOT an ABI — no decode type ever enters the app's load context.
///
/// <para><b>Fail-soft:</b> a missing exe / non-zero exit / timeout / crash →
/// <see cref="ExtractResult.Ok"/> false (never throws for those). The caller
/// degrades to whatever cache already exists (or none), and the confidence gate
/// rejects an uncalibratable run.</para>
/// </summary>
public interface IAssetExtractor
{
    Task<ExtractResult> ExtractAsync(ExtractRequest request, CancellationToken ct);
}

/// <summary>What to extract and where to put it.</summary>
/// <param name="InstallRoot">PG install root passed to the sidecar's <c>--install</c>.</param>
/// <param name="OutDir">Cache directory passed to the sidecar's <c>--out</c>.</param>
/// <param name="Kind">Icons or a single area's base texture.</param>
/// <param name="MapAssetName">Required when <see cref="Kind"/> is <see cref="ExtractKind.Texture"/>.</param>
/// <param name="ExpectPgVersion">Optional <c>--expect-pg-version</c> assertion.</param>
/// <param name="TpkPath">Optional explicit <c>--tpk</c> path to the UABEA
/// <c>classdata.tpk</c> the icon decoder needs (#960). When set, the sidecar
/// prefers it over its beside-exe / repo-default resolution; when null, the
/// sidecar falls back to its old resolution and fail-softs if none is present.</param>
public sealed record ExtractRequest(
    string InstallRoot,
    string OutDir,
    ExtractKind Kind,
    string? MapAssetName,
    string? ExpectPgVersion,
    string? TpkPath = null);

public enum ExtractKind
{
    Icons,
    Texture,
}

/// <summary>
/// The outcome of a sidecar run. <see cref="ExitCode"/> mirrors the sidecar's
/// process exit code (0 ok · 2 install-not-found · 3 bundle-missing-for-area ·
/// 4 decode-failed · 5 output-unwritable; negative values are adapter-side
/// failures such as missing exe / timeout / unparseable output).
/// </summary>
public sealed record ExtractResult(
    bool Ok,
    int ExitCode,
    IReadOnlyList<ExtractedArtifact> Artifacts,
    string? Error)
{
    public static ExtractResult Failure(int exitCode, string error) =>
        new(false, exitCode, [], error);
}

/// <summary>One artifact the sidecar reported on stdout.</summary>
public sealed record ExtractedArtifact(string Kind, string? Area, string Path, string PixelSha256);
