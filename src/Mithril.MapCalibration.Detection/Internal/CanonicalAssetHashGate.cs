using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Verifies that a runtime-extracted asset's <c>pixelSha256</c> matches the
/// committed canonical hash for the detected PG version (issue #931). This
/// restores the "validated input is frozen" property the bundled blobs used to
/// give before #931 stopped shipping PG art: the sidecar re-derives the pixels at
/// runtime, and this gate confirms they hash to the value the dev validated once
/// and committed (a hash, not the art).
///
/// <para>Decision table (mirrors the spec):</para>
/// <list type="bullet">
///   <item><b>match</b> → accept.</item>
///   <item><b>mismatch</b> → reject + warn (decode-tool drift / corruption caught).</item>
///   <item><b>PG version absent from catalogue</b> → accept-with-warn (trust the
///         extraction + lean on the confidence gate; never hard-fail a newer PG
///         patch the catalogue hasn't caught up to).</item>
///   <item><b>artifact key absent for a catalogued PG version</b> → accept-with-warn
///         (same rationale — a new area we haven't catalogued yet).</item>
/// </list>
///
/// <para>The catalogue ships as the embedded <c>canonical-asset-hashes.json</c>
/// resource — our own hashes are redistribution-clean, so embedding is fine.</para>
/// </summary>
internal sealed class CanonicalAssetHashGate
{
    private const string CatalogueResource = "Mithril.MapCalibration.BundledData.canonical-asset-hashes.json";

    private readonly CanonicalAssetHashes _catalogue;
    private readonly ILogger? _logger;

    private CanonicalAssetHashGate(CanonicalAssetHashes catalogue, ILogger? logger)
    {
        _catalogue = catalogue;
        _logger = logger;
    }

    /// <summary>
    /// Loads the committed catalogue from the embedded resource. On any failure
    /// (absent/malformed) returns a gate over an empty catalogue, i.e. every
    /// check resolves to accept-with-warn — fail-soft, never fail-closed against a
    /// good extraction.
    /// </summary>
    public static CanonicalAssetHashGate Load(ILogger? logger)
    {
        var catalogue = ReadCatalogue(logger)
            ?? new CanonicalAssetHashes(2, new Dictionary<string, Dictionary<string, CanonicalAssetHashEntry>>(StringComparer.Ordinal));
        return new CanonicalAssetHashGate(catalogue, logger);
    }

    /// <summary>For tests: a gate over an in-memory catalogue.</summary>
    public static CanonicalAssetHashGate FromCatalogue(CanonicalAssetHashes catalogue, ILogger? logger = null)
        => new(catalogue, logger);

    /// <summary>
    /// Checks <paramref name="actualSha256"/> for the artifact <paramref name="artifactKey"/>
    /// (<c>"icons"</c> or an area key) under <paramref name="pgVersion"/>.
    /// </summary>
    public HashVerdict Check(string? pgVersion, string artifactKey, string actualSha256)
    {
        if (string.IsNullOrWhiteSpace(pgVersion))
        {
            _logger?.LogWarning(
                "Canonical-hash gate: no PG version supplied for {Artifact} — accept-with-warn (cannot look up catalogue).",
                artifactKey);
            return new HashVerdict(true, true, "no PG version supplied");
        }

        if (!_catalogue.ByPgVersion.TryGetValue(pgVersion, out var byArtifact))
        {
            _logger?.LogWarning(
                "Canonical-hash gate: PG version {PgVersion} not in catalogue for {Artifact} — accept-with-warn (newer patch?).",
                pgVersion, artifactKey);
            return new HashVerdict(true, true, $"PG version {pgVersion} not catalogued");
        }

        if (!byArtifact.TryGetValue(artifactKey, out var entry) || string.IsNullOrEmpty(entry?.Sha))
        {
            _logger?.LogWarning(
                "Canonical-hash gate: no canonical hash for {Artifact} under PG {PgVersion} — accept-with-warn.",
                artifactKey, pgVersion);
            return new HashVerdict(true, true, $"no canonical hash for {artifactKey} under {pgVersion}");
        }

        if (string.Equals(entry.Sha, actualSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new HashVerdict(true, false, "match");
        }

        _logger?.LogWarning(
            "Canonical-hash gate: hash mismatch for {Artifact} under PG {PgVersion} (canonical {Expected}, actual {Actual}) — rejected (decode-tool drift / corruption).",
            artifactKey, pgVersion, entry.Sha, actualSha256);
        return new HashVerdict(false, false, $"hash mismatch (canonical {entry.Sha}, actual {actualSha256})");
    }

    private static CanonicalAssetHashes? ReadCatalogue(ILogger? logger)
    {
        var assembly = typeof(CanonicalAssetHashGate).Assembly;
        using var stream = assembly.GetManifestResourceStream(CatalogueResource);
        if (stream is null)
        {
            logger?.LogWarning("Canonical-asset-hash catalogue {Resource} not found — gate accepts all (safe-degrade).", CatalogueResource);
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(stream, DetectionJsonContext.Default.CanonicalAssetHashes);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Canonical-asset-hash catalogue {Resource} failed to parse — gate accepts all (safe-degrade).", CatalogueResource);
            return null;
        }
    }
}

/// <summary>
/// Outcome of a canonical-hash check. <see cref="Accepted"/> is the gate verdict;
/// <see cref="WithWarning"/> marks the accept-with-warn fallbacks (uncatalogued PG
/// version / artifact) so a caller can distinguish a clean match from a
/// trust-the-extraction pass.
/// </summary>
internal sealed record HashVerdict(bool Accepted, bool WithWarning, string Reason);
