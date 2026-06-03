using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Internal;
using Mithril.Shared.Game;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// #945 Gap 3: the icon-template bootstrap's decision logic, tested headless over a
/// fake <see cref="IAssetExtractor"/> + a temp cache dir (no real exe). Covers the
/// four gates: empty cache + InstallRoot set → invokes <c>--icons</c>; cache already
/// populated → skips; InstallRoot empty → skips; extractor failure/throw → no throw.
/// (#959: the gate keys off the Steam <c>InstallRoot</c>, not the data-dir GameRoot.)
/// </summary>
public sealed class IconTemplateBootstrapTests : IDisposable
{
    private readonly string _cacheDir;

    public IconTemplateBootstrapTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "mithril-iconboot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, recursive: true); } catch { /* best effort */ }
    }

    private IconTemplateBootstrap Build(IAssetExtractor extractor, string installRoot, string? cacheDir = null) =>
        new(extractor,
            new GameConfig { InstallRoot = installRoot },
            cacheDir ?? _cacheDir,
            pgVersion: null,
            NullLogger.Instance);

    private void WritePopulatedManifest()
    {
        // A minimal valid manifest (camelCase, schema-versioned, non-empty Icons +
        // pixelSha256) is all IconTemplateCache.IsPopulated needs to read "populated".
        var json =
            "{\"schemaVersion\":1,\"pixelSha256\":\"deadbeef\"," +
            "\"icons\":[{\"name\":\"x\",\"landmarkType\":\"Cave\",\"pivotX\":0,\"pivotY\":0,\"width\":1,\"height\":1}]}";
        File.WriteAllText(Path.Combine(_cacheDir, "icon-templates.json"), json);
    }

    [Fact]
    public async Task Empty_cache_with_install_root_invokes_icons_extraction()
    {
        var extractor = new RecordingAssetExtractor();
        var sut = Build(extractor, installRoot: @"C:\Games\PG");

        var invoked = await sut.RunOnceAsync(CancellationToken.None);

        invoked.Should().BeTrue();
        extractor.Calls.Should().ContainSingle();
        var call = extractor.Calls.Single();
        call.Kind.Should().Be(ExtractKind.Icons);
        call.InstallRoot.Should().Be(@"C:\Games\PG");
        call.OutDir.Should().Be(_cacheDir);
        call.AreaKey.Should().BeNull();
    }

    [Fact]
    public async Task Already_populated_cache_does_not_invoke()
    {
        WritePopulatedManifest();
        var extractor = new RecordingAssetExtractor();
        var sut = Build(extractor, installRoot: @"C:\Games\PG");

        var invoked = await sut.RunOnceAsync(CancellationToken.None);

        invoked.Should().BeFalse();
        extractor.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_install_root_does_not_invoke()
    {
        var extractor = new RecordingAssetExtractor();
        var sut = Build(extractor, installRoot: "");

        var invoked = await sut.RunOnceAsync(CancellationToken.None);

        invoked.Should().BeFalse();
        extractor.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Extractor_failure_does_not_throw()
    {
        var failing = new RecordingAssetExtractor(
            new ExtractResult(false, ProcessAssetExtractor.ExitMissingExe, Array.Empty<ExtractedArtifact>(), "no exe"));
        var sut = Build(failing, installRoot: @"C:\Games\PG");

        var invoked = await sut.RunOnceAsync(CancellationToken.None);

        // The sidecar WAS invoked (it's the gate that decides invocation, not the
        // sidecar's success); the failure is swallowed fail-soft.
        invoked.Should().BeTrue();
        failing.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Extractor_throwing_is_swallowed_fail_soft()
    {
        var sut = Build(new ThrowingAssetExtractor(), installRoot: @"C:\Games\PG");

        var act = async () => await sut.RunOnceAsync(CancellationToken.None);

        // The throw is caught inside RunOnceAsync; it still reports "invoked".
        (await act.Should().NotThrowAsync()).Which.Should().BeTrue();
    }
}
