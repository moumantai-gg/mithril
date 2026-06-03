using System.IO;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Mithril.Shared.Tests.Architecture;

/// <summary>
/// Guards the load-bearing map-calibration invariant (issue #921): no project
/// under <c>src/**</c> — the shipped product graph — may take a dependency on a
/// Unity-asset reader (<c>AssetsTools.NET</c>) or an image decoder
/// (<c>System.Drawing.Common</c>), directly or via the dev-only
/// <c>Mithril.MapCalibration.Tools.Common</c> tool library.
///
/// The detect→solve engine the product needs lives decoder-free in
/// <c>src/Mithril.MapCalibration</c> (it loads pre-decoded icon templates +
/// base textures BCL-only from a runtime cache); the heavy decoders are confined
/// to <c>tools/</c> and the <c>Mithril.MapCalibration.Tools.slnx</c> solution.
/// This test fails red if any shipped project re-introduces a decoder, so the
/// boundary can't silently rot.
///
/// <para><b>Sanctioned out-of-process exception (issue #931):</b> the
/// <c>tools/Mithril.AssetExtractor</c> sidecar exe carries the decoders
/// (AssetsTools.NET + System.Drawing, via <c>Tools.Common</c>) and is published
/// next to the shell as a packaged artifact. The <i>only</i> app↔exe link is
/// <c>System.Diagnostics.Process</c> (<c>ProcessAssetExtractor</c>) — a process
/// boundary, never a <c>ProjectReference</c> / <c>PackageReference</c>. So the
/// sidecar runs decoders out-of-process while <c>src/**</c> stays decoder-free and
/// this test stays green truthfully (the sidecar lives under <c>tools/</c>, off
/// the scanned <c>src/**</c> graph). The assertions below are unchanged: loading
/// the extractor in-process via reflection to dodge this scan is explicitly
/// forbidden — the value is the process boundary, not a green check.</para>
///
/// <para><b>Sanctioned in-process OpenCv exception (issue #978, spec
/// map-calibration-detection-project-split):</b> screenshot↔texture feature-matching
/// locate (ORB + RANSAC via <see cref="Mithril.MapCalibration.Detection.FeatureMatchingRefiner"/>) runs IN-PROCESS in
/// the dedicated calibration detection assembly
/// (<c>Mithril.MapCalibration.Detection</c>). Maintainer decision: OpenCvSharp is an
/// <i>alignment</i> library, not an asset decoder; Mithril is Windows-only WPF (not
/// trimmed / not AOT) and detection runs per-attempt at user-perceived latency, so an
/// in-process call beats an out-of-process sidecar round-trip and its native-runtime
/// staging cost. The detection project is the SINGLE named OpenCv home; the capture
/// project is OpenCv-free (Win32 / WPF capture only). To keep that exception EXPLICIT
/// and stop OpenCv silently spreading, <c>OpenCvSharp</c> is in
/// <see cref="ForbiddenPackages"/> across <c>src/**</c> and re-permitted ONLY for the
/// named project in <see cref="PackageAllowlistByProject"/>. The #921 decoder-free
/// split is relaxed via that named allowlist, never removed and never replaced with a
/// sidecar.</para>
/// </summary>
public class ShippedGraphDecoderFreeTests
{
    // Substring match against PackageReference Include / ProjectReference Include.
    private static readonly string[] ForbiddenPackages =
    [
        "AssetsTools.NET",          // also covers AssetsTools.NET.Texture
        "System.Drawing.Common",
        "OpenCvSharp",              // #978: covers OpenCvSharp4 + OpenCvSharp4.runtime.win
    ];

    // #978: OpenCvSharp is permitted IN-PROCESS only in the explicitly named
    // shipped assembly — the dedicated calibration detection project. OpenCvSharp is
    // an alignment library, not an asset decoder; Mithril is Windows-only WPF (not
    // trimmed/AOT) and detection runs per-attempt at user-perceived latency, so
    // in-process beats an out-of-process sidecar. The capture project is OpenCv-free
    // (Win32 / WPF capture only). The #921 split is RELAXED via this named allowlist,
    // not removed: any OTHER src/** project taking an OpenCvSharp reference is a
    // violation. Keyed by csproj file name so the exception is unambiguous and cannot
    // widen silently. (AssetsTools.NET / System.Drawing.Common are NOT allowlisted
    // anywhere.)
    private static readonly Dictionary<string, string[]> PackageAllowlistByProject = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mithril.MapCalibration.Detection.csproj"] = ["OpenCvSharp"],
    };

    private static readonly string[] ForbiddenProjects =
    [
        "Mithril.MapCalibration.Tools.Common",
    ];

    [Fact]
    public void No_src_project_references_a_decoder_or_the_tools_common_lib()
    {
        var srcRoot = Path.Combine(RepoRoot(), "src");
        var projects = Directory.GetFiles(srcRoot, "*.csproj", SearchOption.AllDirectories);

        projects.Should().NotBeEmpty("the src/ tree must contain shipped projects to guard");

        var violations = new List<string>();

        foreach (var csproj in projects)
        {
            var doc = XDocument.Load(csproj);
            var rel = Path.GetRelativePath(RepoRoot(), csproj).Replace('\\', '/');
            var fileName = Path.GetFileName(csproj);
            var allowed = PackageAllowlistByProject.TryGetValue(fileName, out var a) ? a : [];

            foreach (var include in Includes(doc, "PackageReference"))
            {
                foreach (var bad in ForbiddenPackages)
                {
                    if (!include.Contains(bad, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // #978: a forbidden substring is fine only when this exact project
                    // is allowlisted for that substring (OpenCvSharp in the capture asm).
                    if (allowed.Any(ok => bad.Contains(ok, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    violations.Add($"{rel}: PackageReference '{include}' (matches forbidden '{bad}')");
                }
            }

            foreach (var include in Includes(doc, "ProjectReference"))
            {
                foreach (var bad in ForbiddenProjects)
                {
                    if (include.Contains(bad, StringComparison.OrdinalIgnoreCase))
                        violations.Add($"{rel}: ProjectReference '{include}' (matches forbidden '{bad}')");
                }
            }
        }

        violations.Should().BeEmpty(
            "the shipped product graph (src/**) must stay decoder-free (issue #921). " +
            "AssetsTools.NET / System.Drawing belong only to tools/ and " +
            "Mithril.MapCalibration.Tools.slnx, never the shipped app:\n" +
            string.Join("\n", violations));
    }

    // Element names are unqualified in SDK-style csproj (no default xmlns), so a
    // plain local-name match is sufficient and avoids namespace plumbing.
    private static IEnumerable<string> Includes(XDocument doc, string element) =>
        doc.Descendants()
            .Where(e => e.Name.LocalName == element)
            .Select(e => (string?)e.Attribute("Include"))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!);

    // Walk up from the test's bin dir to the folder that owns Mithril.slnx.
    // Deliberately inlined rather than reusing Tools.Common.RepoPaths — taking a
    // ProjectReference on Tools.Common here would re-create the very dependency
    // this test exists to forbid.
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            if (File.Exists(Path.Combine(dir, "Mithril.slnx"))) return dir;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        throw new InvalidOperationException(
            $"could not locate Mithril.slnx walking up from {AppContext.BaseDirectory}");
    }
}
