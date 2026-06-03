using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection.Internal;

namespace Mithril.MapCalibration.Internal;

/// <summary>
/// Runs the out-of-process asset-extractor sidecar via
/// <see cref="System.Diagnostics.Process"/> (issue #931). The exe path is
/// supplied by the caller (resolved next to the running shell, NOT hardcoded to
/// <see cref="AppContext.BaseDirectory"/>), and the actual process launch is an
/// injectable seam so the JSON-parse / exit-code-map / timeout-kill logic is
/// unit-testable over a fake launcher without a real exe.
///
/// <para><b>Fail-soft:</b> missing exe, non-zero exit, timeout, crash, or
/// unparseable output all yield <see cref="ExtractResult.Ok"/> = false rather
/// than throwing. stderr is piped to <see cref="ILogger"/> diagnostics. No decode
/// type ever loads into the app graph — the only link is the process boundary.</para>
/// </summary>
public sealed class ProcessAssetExtractor : IAssetExtractor
{
    /// <summary>
    /// Launches the process described by <paramref name="psi"/> and returns its
    /// exit code + captured stdout/stderr, honouring the cancellation token (the
    /// real implementation kills the child on cancellation). The seam the timeout
    /// + fake-launcher tests swap.
    /// </summary>
    public delegate Task<ProcessRunResult> ProcessLauncher(ProcessStartInfo psi, CancellationToken ct);

    /// <summary>Adapter-side exit codes (negative; the sidecar's own codes are 0..5).</summary>
    public const int ExitMissingExe = -1;
    public const int ExitTimeout = -2;
    public const int ExitLaunchFailed = -3;
    public const int ExitUnparseableOutput = -4;
    public const int ExitStatusNotOk = -5;

    private readonly string _exePath;
    private readonly TimeSpan _timeout;
    private readonly ProcessLauncher _launcher;
    private readonly ILogger? _logger;

    public ProcessAssetExtractor(
        string exePath,
        TimeSpan timeout,
        ProcessLauncher? launcher = null,
        ILogger? logger = null)
    {
        _exePath = exePath;
        _timeout = timeout;
        _launcher = launcher ?? DefaultLaunch;
        _logger = logger;
    }

    public async Task<ExtractResult> ExtractAsync(ExtractRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_exePath) || !File.Exists(_exePath))
        {
            _logger?.LogWarning(
                "Asset-extractor sidecar not found at {ExePath} — skipping extraction (safe-degrade; auto-calibration won't run).",
                _exePath);
            return ExtractResult.Failure(ExitMissingExe, $"sidecar exe not found at '{_exePath}'");
        }

        var psi = BuildStartInfo(request);

        // Hard timeout on top of the caller's token: kill the child if it hangs.
        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        ProcessRunResult run;
        try
        {
            run = await _launcher(psi, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger?.LogWarning(
                "Asset-extractor sidecar timed out after {Timeout} ({Kind} {Area}) — child killed (safe-degrade).",
                _timeout, request.Kind, request.AreaKey);
            return ExtractResult.Failure(ExitTimeout, $"sidecar timed out after {_timeout}");
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInformation("Asset-extractor sidecar cancelled by caller ({Kind} {Area}).", request.Kind, request.AreaKey);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Asset-extractor sidecar failed to launch ({Kind} {Area}) — safe-degrade.", request.Kind, request.AreaKey);
            return ExtractResult.Failure(ExitLaunchFailed, $"sidecar launch failed: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(run.Stderr))
        {
            // Sidecar diagnostics are human-facing; surface at the level its exit
            // code implies (non-zero = warning).
            if (run.ExitCode == 0)
                _logger?.LogInformation("Asset-extractor stderr: {Stderr}", run.Stderr.Trim());
            else
                _logger?.LogWarning("Asset-extractor stderr (exit {ExitCode}): {Stderr}", run.ExitCode, run.Stderr.Trim());
        }

        if (run.ExitCode != 0)
        {
            var msg = MapExitCode(run.ExitCode, request);
            _logger?.LogWarning("Asset-extractor sidecar exited {ExitCode}: {Message} — safe-degrade.", run.ExitCode, msg);
            return ExtractResult.Failure(run.ExitCode, msg);
        }

        var parsed = ParseResult(run.Stdout);
        if (parsed is null)
        {
            return ExtractResult.Failure(ExitUnparseableOutput, "sidecar exit 0 but stdout had no parseable JSON result line");
        }
        if (!string.Equals(parsed.Status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return ExtractResult.Failure(ExitStatusNotOk, $"sidecar reported status '{parsed.Status}'");
        }

        var artifacts = new List<ExtractedArtifact>(parsed.Artifacts?.Count ?? 0);
        if (parsed.Artifacts is not null)
        {
            foreach (var a in parsed.Artifacts)
                artifacts.Add(new ExtractedArtifact(a.Kind, a.Area, a.Path, a.PixelSha256));
        }

        _logger?.LogInformation(
            "Asset-extractor sidecar ok (pgVersion {PgVersion}, {Count} artifact(s)).",
            parsed.PgVersion, artifacts.Count);
        return new ExtractResult(true, 0, artifacts, null);
    }

    private ProcessStartInfo BuildStartInfo(ExtractRequest request)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--install");
        psi.ArgumentList.Add(request.InstallRoot);
        psi.ArgumentList.Add("--out");
        psi.ArgumentList.Add(request.OutDir);
        if (request.Kind == ExtractKind.Icons)
        {
            psi.ArgumentList.Add("--icons");
        }
        else
        {
            psi.ArgumentList.Add("--area");
            psi.ArgumentList.Add(request.AreaKey ?? string.Empty);
        }
        if (!string.IsNullOrWhiteSpace(request.ExpectPgVersion))
        {
            psi.ArgumentList.Add("--expect-pg-version");
            psi.ArgumentList.Add(request.ExpectPgVersion);
        }
        if (!string.IsNullOrWhiteSpace(request.TpkPath))
        {
            psi.ArgumentList.Add("--tpk");
            psi.ArgumentList.Add(request.TpkPath);
        }
        return psi;
    }

    private static string MapExitCode(int exitCode, ExtractRequest request) => exitCode switch
    {
        2 => $"PG install not found at '{request.InstallRoot}'",
        3 => $"map bundle missing for area '{request.AreaKey}'",
        4 => "asset decode failed",
        5 => $"output dir not writable '{request.OutDir}'",
        _ => $"sidecar exited with code {exitCode}",
    };

    private SidecarResult? ParseResult(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        // The sidecar emits exactly one JSON result line on stdout (plus possibly
        // other lines if a future version adds them); scan for the first line that
        // parses as the result object.
        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{') continue;
            try
            {
                var result = JsonSerializer.Deserialize(trimmed, DetectionJsonContext.Default.SidecarResult);
                if (result is not null && !string.IsNullOrEmpty(result.Status))
                    return result;
            }
            catch (JsonException ex)
            {
                _logger?.LogWarning(ex, "Asset-extractor stdout line failed to parse as a result object.");
            }
        }
        return null;
    }

    private static async Task<ProcessRunResult> DefaultLaunch(ProcessStartInfo psi, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!process.Start())
            throw new InvalidOperationException("Process.Start returned false");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Timeout or caller cancel: kill the child so it doesn't outlive us.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
            throw;
        }

        return new ProcessRunResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

/// <summary>Captured outcome of a sidecar process run.</summary>
public sealed record ProcessRunResult(int ExitCode, string Stdout, string Stderr);
