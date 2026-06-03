using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection;
using Mithril.Shared.Game;
using Mithril.Shared.MapCalibration;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// #945 Gap 3 — non-blocking icon-template cache warm-up. The engine's
/// base-texture cache-miss path only ever requests <see cref="ExtractKind.Texture"/>
/// (per-area); on a fresh install the icon cache starts empty. This hosted service
/// runs the sidecar's <c>--icons</c> mode once, on startup, when the icon cache is
/// not yet populated, so the manifest+blob land on disk before the first
/// calibration attempt.
///
/// <para><b>Same-session effect (#949).</b> Icon templates resolve via the
/// per-attempt <see cref="IIconTemplateProvider"/> (it re-reads the cache on every
/// attempt), so a populate from this warm-up takes effect on the very next attempt
/// — no app restart. This warm-up is therefore a <i>latency optimisation</i> (have
/// icons ready before the first attempt), and the engine's own <c>--icons</c>
/// demand-trigger (<c>AutoCalibrationEngine.EnsureIconTemplatesAsync</c>) is the
/// correctness backstop if the warm-up hasn't finished by the first attempt.</para>
///
/// <para><b>Fire-and-forget StartAsync.</b> The extraction runs on a background task
/// so it never blocks host startup (the sidecar can take seconds on a cold disk).
/// Failure is irrelevant to startup — it's a cache warm-up.</para>
///
/// <para><b>Fail-soft (preserved end-to-end).</b> No exe / empty
/// <see cref="GameConfig.InstallRoot"/> / non-zero exit / timeout / crash → no icons,
/// no throw, no crash. The bootstrap is skipped entirely when the cache is already
/// populated (manifest present) so it runs at most once per fresh cache.</para>
/// </summary>
public sealed class IconTemplateBootstrap : IHostedService, IDisposable
{
    private readonly IAssetExtractor _extractor;
    private readonly GameConfig _gameConfig;
    private readonly string _assetCacheDir;
    private readonly string? _pgVersion;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();

    private Task? _bootstrap;

    public IconTemplateBootstrap(
        IAssetExtractor extractor,
        GameConfig gameConfig,
        string assetCacheDir,
        string? pgVersion,
        ILogger logger)
    {
        _extractor = extractor;
        _gameConfig = gameConfig;
        _assetCacheDir = assetCacheDir;
        _pgVersion = pgVersion;
        _logger = logger;
    }

    /// <summary>
    /// The downloaded <c>classdata.tpk</c> path inside the asset cache, or null when
    /// it isn't present yet (#960). When null the sidecar falls back to its old
    /// resolution and fail-softs (icons stay disabled until the user downloads it).
    /// </summary>
    private string? ResolveTpkPath()
    {
        if (string.IsNullOrWhiteSpace(_assetCacheDir)) return null;
        var tpk = Path.Combine(_assetCacheDir, ClassDataTpkProvisioner.TpkFileName);
        return File.Exists(tpk) ? tpk : null;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fire-and-forget: warming the icon cache must not block host startup. Run
        // under a private CTS (not the startup token, which signals startup-abort and
        // doesn't fire on shutdown) so StopAsync can cancel an in-flight extraction.
        _bootstrap = Task.Run(() => RunOnceAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Best-effort: cancel an in-flight extraction and wait briefly for it to
        // unwind so we don't leave a dangling child past host shutdown. Any fault /
        // cancellation is swallowed — RunOnceAsync is already fail-soft.
        _cts.Cancel();
        if (_bootstrap is { } b)
        {
            try { await b.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch { /* cancelled / faulted / shutdown-token tripped — nothing to do */ }
        }
    }

    /// <summary>
    /// The bootstrap decision + extraction, extracted for unit testing. Decides
    /// whether to invoke the sidecar's <c>--icons</c> mode and, if so, runs it
    /// fail-soft. Returns <c>true</c> iff the sidecar was invoked (regardless of
    /// the sidecar's own success/failure), <c>false</c> when skipped by a gate.
    /// </summary>
    internal async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_gameConfig.InstallRoot))
        {
            _logger.LogInformation(
                "Icon-template bootstrap skipped: PG install root not configured (GameConfig.InstallRoot empty). Safe-degrade.");
            return false;
        }

        // Already populated? The manifest's presence is the populated-cache signal
        // (IconTemplateCache reads the sidecar's icon-templates.json). Re-running the
        // sidecar on every launch would be wasted IO + a pointless child process.
        if (IconTemplateCache.IsPopulated(_assetCacheDir, _logger))
        {
            _logger.LogInformation(
                "Icon-template cache already populated at {CacheDir}; skipping --icons bootstrap.", _assetCacheDir);
            return false;
        }

        _logger.LogInformation(
            "Icon-template cache empty at {CacheDir}; invoking asset-extractor sidecar (--icons) warm-up. "
            + "Icons engage on the next calibration attempt (IIconTemplateProvider re-reads the cache per attempt).",
            _assetCacheDir);

        try
        {
            var request = new ExtractRequest(
                InstallRoot: _gameConfig.InstallRoot,
                OutDir: _assetCacheDir,
                Kind: ExtractKind.Icons,
                MapAssetName: null,
                ExpectPgVersion: _pgVersion,
                TpkPath: ResolveTpkPath());
            var result = await _extractor.ExtractAsync(request, ct).ConfigureAwait(false);
            if (result.Ok)
            {
                _logger.LogInformation(
                    "Icon-template bootstrap populated the cache ({Count} artifact(s)); icons engage on the next calibration attempt.",
                    result.Artifacts.Count);
            }
            else
            {
                _logger.LogWarning(
                    "Icon-template bootstrap: sidecar failed (exit {Exit}): {Error}. Icons stay disabled (safe-degrade).",
                    result.ExitCode, result.Error);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Icon-template bootstrap cancelled (host shutdown).");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Icon-template bootstrap threw; icons stay disabled (safe-degrade).");
        }

        return true;
    }

    public void Dispose() => _cts.Dispose();
}
