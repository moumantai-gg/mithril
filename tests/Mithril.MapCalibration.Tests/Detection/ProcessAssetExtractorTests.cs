using System.Diagnostics;
using System.IO;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// #931: unit-tests the app-side adapter (<see cref="ProcessAssetExtractor"/>)
/// over a fake process launcher — arg-build + JSON-parse + exit-code-map +
/// timeout-kill, all without a real exe. The real <c>Process.Start</c> path is
/// manual/integration-verified (no CI for the PG-asset path).
/// </summary>
public sealed class ProcessAssetExtractorTests : IDisposable
{
    private readonly string _fakeExe;

    public ProcessAssetExtractorTests()
    {
        // The adapter checks File.Exists on the exe path before launching; a real
        // existing file (any file) satisfies that without ever being run, since the
        // launcher is faked.
        _fakeExe = Path.Combine(Path.GetTempPath(), "mithril931-fakeexe-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(_fakeExe, "not a real exe");
    }

    public void Dispose()
    {
        try { File.Delete(_fakeExe); } catch { /* best effort */ }
    }

    private const string OkJson =
        "{\"status\":\"ok\",\"pgVersion\":\"1.2.3\",\"extractorVersion\":\"1.0.0\"," +
        "\"artifacts\":[{\"kind\":\"icons\",\"area\":null,\"path\":\"C:/cache/icon-templates.json\",\"pixelSha256\":\"abc123\"}]}";

    [Fact]
    public async Task Ok_json_parses_into_result_with_artifacts()
    {
        ProcessStartInfo? captured = null;
        var sut = new ProcessAssetExtractor(_fakeExe, TimeSpan.FromSeconds(5),
            launcher: (psi, _) =>
            {
                captured = psi;
                return Task.FromResult(new ProcessRunResult(0, OkJson, ""));
            });

        var result = await sut.ExtractAsync(
            new ExtractRequest("C:/PG", "C:/cache", ExtractKind.Icons, null, null), CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.ExitCode.Should().Be(0);
        result.Artifacts.Should().ContainSingle();
        result.Artifacts[0].Kind.Should().Be("icons");
        result.Artifacts[0].PixelSha256.Should().Be("abc123");

        // Arg construction: --install <root> --out <dir> --icons.
        captured.Should().NotBeNull();
        var args = captured!.ArgumentList;
        args.Should().ContainInOrder("--install", "C:/PG", "--out", "C:/cache");
        args.Should().Contain("--icons");
    }

    [Fact]
    public async Task Texture_request_builds_area_argument()
    {
        ProcessStartInfo? captured = null;
        var sut = new ProcessAssetExtractor(_fakeExe, TimeSpan.FromSeconds(5),
            launcher: (psi, _) =>
            {
                captured = psi;
                return Task.FromResult(new ProcessRunResult(0,
                    "{\"status\":\"ok\",\"pgVersion\":\"1\",\"extractorVersion\":\"1\",\"artifacts\":[]}", ""));
            });

        await sut.ExtractAsync(
            new ExtractRequest("C:/PG", "C:/cache", ExtractKind.Texture, "AreaSerbule", "1.2.3"), CancellationToken.None);

        var args = captured!.ArgumentList;
        args.Should().ContainInOrder("--asset", "AreaSerbule");
        args.Should().ContainInOrder("--expect-pg-version", "1.2.3");
        args.Should().NotContain("--icons");
    }

    [Fact]
    public async Task Tpk_path_is_added_to_args_when_set()
    {
        ProcessStartInfo? captured = null;
        var sut = new ProcessAssetExtractor(_fakeExe, TimeSpan.FromSeconds(5),
            launcher: (psi, _) =>
            {
                captured = psi;
                return Task.FromResult(new ProcessRunResult(0,
                    "{\"status\":\"ok\",\"pgVersion\":\"1\",\"extractorVersion\":\"1\",\"artifacts\":[]}", ""));
            });

        await sut.ExtractAsync(
            new ExtractRequest("C:/PG", "C:/cache", ExtractKind.Icons, null, null, TpkPath: "C:/cache/classdata.tpk"),
            CancellationToken.None);

        captured!.ArgumentList.Should().ContainInOrder("--tpk", "C:/cache/classdata.tpk");
    }

    [Fact]
    public async Task Tpk_path_is_omitted_when_null()
    {
        ProcessStartInfo? captured = null;
        var sut = new ProcessAssetExtractor(_fakeExe, TimeSpan.FromSeconds(5),
            launcher: (psi, _) =>
            {
                captured = psi;
                return Task.FromResult(new ProcessRunResult(0,
                    "{\"status\":\"ok\",\"pgVersion\":\"1\",\"extractorVersion\":\"1\",\"artifacts\":[]}", ""));
            });

        // Default TpkPath = null (omitted last positional arg).
        await sut.ExtractAsync(
            new ExtractRequest("C:/PG", "C:/cache", ExtractKind.Icons, null, null),
            CancellationToken.None);

        captured!.ArgumentList.Should().NotContain("--tpk");
    }

    [Fact]
    public async Task Exit_code_3_maps_to_failure_mentioning_the_area_bundle()
    {
        var sut = new ProcessAssetExtractor(_fakeExe, TimeSpan.FromSeconds(5),
            launcher: (_, _) => Task.FromResult(new ProcessRunResult(3, "", "no map bundle for area 'AreaSerbule'")));

        var result = await sut.ExtractAsync(
            new ExtractRequest("C:/PG", "C:/cache", ExtractKind.Texture, "AreaSerbule", null), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ExitCode.Should().Be(3);
        result.Error.Should().Contain("AreaSerbule");
        result.Error.Should().Contain("bundle");
    }

    [Fact]
    public async Task Timeout_kills_and_reports_failure()
    {
        // Fake launcher that never returns until its token cancels (the timeout).
        var sut = new ProcessAssetExtractor(_fakeExe, TimeSpan.FromMilliseconds(50),
            launcher: async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct); // throws OCE on timeout-token cancel
                return new ProcessRunResult(0, OkJson, "");
            });

        var result = await sut.ExtractAsync(
            new ExtractRequest("C:/PG", "C:/cache", ExtractKind.Icons, null, null), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ExitCode.Should().Be(ProcessAssetExtractor.ExitTimeout);
        result.Error.Should().Contain("timed out");
    }

    [Fact]
    public async Task Missing_exe_returns_failure_without_throwing()
    {
        var sut = new ProcessAssetExtractor(
            Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".exe"),
            TimeSpan.FromSeconds(5),
            launcher: (_, _) => throw new InvalidOperationException("launcher must not be called when exe is missing"));

        var result = await sut.ExtractAsync(
            new ExtractRequest("C:/PG", "C:/cache", ExtractKind.Icons, null, null), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ExitCode.Should().Be(ProcessAssetExtractor.ExitMissingExe);
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Sidecar_resolved_beside_the_host_fail_softs_when_absent()
    {
        // #945 Gap 1/2 contract: the Capture DI registers the extractor with
        // exePath = Path.Combine(AppContext.BaseDirectory, "mithril-asset-extract.exe").
        // In dev/F5 (and this test host) the sidecar isn't published beside the
        // binary, so that path doesn't exist. The registration is unconditional
        // (no File.Exists gate) precisely because this path fail-softs: assert it
        // returns ExitMissingExe and does NOT throw, so the DI graph stays
        // deterministic and the engine safe-degrades ("preparing map assets…").
        var exePath = Path.Combine(AppContext.BaseDirectory, "mithril-asset-extract.exe");
        File.Exists(exePath).Should().BeFalse("the sidecar isn't published beside the test host");

        var sut = new ProcessAssetExtractor(exePath, TimeSpan.FromSeconds(5),
            launcher: (_, _) => throw new InvalidOperationException("launcher must not run when the exe is absent"));

        var result = await sut.ExtractAsync(
            new ExtractRequest("C:/PG", "C:/cache", ExtractKind.Texture, "AreaSerbule", null), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ExitCode.Should().Be(ProcessAssetExtractor.ExitMissingExe);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_as_OperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var sut = new ProcessAssetExtractor(_fakeExe, TimeSpan.FromSeconds(30),
            launcher: async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new ProcessRunResult(0, OkJson, "");
            });

        var task = sut.ExtractAsync(
            new ExtractRequest("C:/PG", "C:/cache", ExtractKind.Icons, null, null), cts.Token);
        cts.Cancel();

        await FluentActions.Awaiting(() => task).Should().ThrowAsync<OperationCanceledException>();
    }
}
