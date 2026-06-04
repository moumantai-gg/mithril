using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// Skippable real-screenshot replay. Gated on <c>study/screenshots/Area*.png</c>
/// + <c>study/textures/*.png</c> existing (the <c>study/</c> tree is gitignored
/// and absent in CI). When the assets are missing the theory yields no cases and
/// a guard test logs "SKIPPED" loudly — the coverage gap is never silent (memory:
/// no silent caps). When present, asserts the engine recovers each area's
/// committed baseline within tolerance.
/// </summary>
public sealed class ReplayFixtureTests
{
    private readonly ITestOutputHelper _output;
    public ReplayFixtureTests(ITestOutputHelper output) => _output = output;

    private static readonly string[] Areas = ["AreaSerbule", "AreaEltibule", "AreaKurMountains"];

    private static string? StudyRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            if (File.Exists(Path.Combine(dir, "Mithril.slnx")))
            {
                var study = Path.Combine(dir, "study");
                return Directory.Exists(study) ? study : null;
            }
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        return null;
    }

    // The bundled map textures carry a version infix — study/textures holds
    // Map_<area>.v4.png (e.g. Map_AreaSerbule.v4.png), not Map_<area>.png. Try
    // the unversioned name first for forward-compat, then the .v4 name, then
    // glob Map_<area>*.png so a future .v5 still resolves without a test edit.
    private static string? ResolveTexture(string root, string area)
    {
        var texDir = Path.Combine(root, "textures");
        var plain = Path.Combine(texDir, "Map_" + area + ".png");
        if (File.Exists(plain)) return plain;
        var v4 = Path.Combine(texDir, "Map_" + area + ".v4.png");
        if (File.Exists(v4)) return v4;
        if (Directory.Exists(texDir))
        {
            var matches = Directory.GetFiles(texDir, "Map_" + area + "*.png");
            if (matches.Length > 0)
            {
                Array.Sort(matches, StringComparer.Ordinal);
                return matches[^1];
            }
        }
        return null;
    }

    public static IEnumerable<object?[]> PresentAreas()
    {
        var root = StudyRoot();
        bool any = false;
        if (root is not null)
        {
            foreach (var area in Areas)
            {
                var shot = Path.Combine(root, "screenshots", area + ".png");
                var tex = ResolveTexture(root, area);
                if (File.Exists(shot) && tex is not null)
                {
                    any = true;
                    yield return new object?[] { area, shot, tex };
                }
            }
        }
        // xUnit fails a [Theory] with zero data rows ("No data found"). Yield a
        // single sentinel row when no study/ assets exist; the test recognises
        // null paths and skips loudly (a [Theory] can't be conditionally [Fact],
        // and we avoid taking a new SkippableFact package dep for v1).
        if (!any) yield return new object?[] { null, null, null };
    }

    [Fact]
    public void Replay_assets_presence_is_reported()
    {
        var root = StudyRoot();
        if (root is null || !PresentAreas().Any())
        {
            _output.WriteLine("SKIPPED — study/ replay assets absent (gitignored; run locally to exercise real-screenshot replay).");
            return;
        }
        _output.WriteLine($"study/ assets present at {root}: {PresentAreas().Count()} area(s) will replay.");
    }

    [Theory]
    [MemberData(nameof(PresentAreas))]
    public void Recovers_committed_baseline_for_area(string? area, string? screenshotPath, string? texturePath)
    {
        if (area is null || screenshotPath is null || texturePath is null)
        {
            _output.WriteLine("SKIPPED — study/ replay assets absent (gitignored; run locally to exercise real-screenshot replay).");
            return;
        }

        var baseline = Mithril.MapCalibration.Internal.BundledBaselineLoader.Load(logger: null);
        // PR #1048 (closes #1041) migrated baseline keys from bare area names to
        // per-scene Map_<X> keys. Screenshot/refs file paths still key off the bare
        // area name (study/screenshots/<area>.png, study/refs/<area>.json), so the
        // Map_ prefix is local to the baseline lookup.
        var assetKey = "Map_" + area;
        baseline.Should().ContainKey(assetKey, "the area must have a committed baseline to compare against");
        var expected = baseline[assetKey];

        var shot = WicImageLoader.LoadGray(screenshotPath);
        var tex = WicImageLoader.LoadGray(texturePath);

        // The screenshot here is assumed already cropped to the map rect (1:1
        // texture extent) for the replay; real captures crop via the production
        // IMapRegionRefiner (FeatureMatchingRefiner) in Phase 2.
        var rect = new MapRect(0, 0, shot.Width, shot.Height, tex.Width, tex.Height);
        // #931: icon templates are no longer embedded — they're loaded from the
        // asset-extractor cache dir. For this fixture-gated replay the cache (a dev
        // populated it via the sidecar) lives under study/templates. Absent → Empty
        // → the assertions below would fail, so a dev running the replay must
        // populate it; CI never reaches here (study/ is gitignored).
        var templatesDir = Path.Combine(StudyRoot()!, "templates");
        var templates = BundledIconTemplateLoader.LoadFromDirectory(templatesDir, logger: null);
        var refs = LoadStudyRefs(area);
        refs.Should().NotBeEmpty($"study/refs/{area}.json must list the area's landmark/NPC references for replay");

        // Proven gate-study recipe: render size pinned at 16 px (the empirical
        // sweet-spot), per-blob type-NCC floor 0.80. A lower floor (0.65) floods
        // false positives and RANSAC converges on a degenerate few-inlier fit at
        // the wrong orientation; the auto render-size sweep collapses to the
        // smallest/blurriest size that correlates with everything (mithril#916).
        var request = new DetectionRequest(shot, tex, rect, templates, RimMaskMode.DeviationFlood,
            LowNcc: 0.5, TypeFloor: 0.80,
            BlobOptions: new BlobOptions(MinArea: 12, MaxIconArea: 900, MinSolidity: 0.35, MaxAspect: 2.5, MinPeak: 0.7))
        {
            RenderSizePx = 16,
        };

        var engine = new MapCalibrationSolveEngine(new DeviationBlobCalibrationDetector(), new CalibrationConfidenceGate());
        var result = engine.Solve(request, refs);

        result.Calibration.Should().NotBeNull($"the engine must cold-solve {area}");
        var cal = result.Calibration!;

        // Emit the recovered-vs-baseline numbers so a replay run is self-documenting
        // (the headline evidence for #916: the lifted engine reproduces the
        // #897/#913 gate-study cold solves end-to-end).
        _output.WriteLine(
            $"[{area}] recovered: scale={cal.Scale:0.000000} rot={cal.RotationRadians:0.######} " +
            $"origin=({cal.OriginX:0.000},{cal.OriginY:0.000}) residual={cal.ResidualPixels:0.000}px refs={cal.ReferenceCount}");
        _output.WriteLine(
            $"[{area}] baseline : scale={expected.Scale:0.000000} rot={expected.RotationRadians:0.######} " +
            $"origin=({expected.OriginX:0.000},{expected.OriginY:0.000}) residual={expected.ResidualPixels:0.000}px refs={expected.ReferenceCount}");
        _output.WriteLine(
            $"[{area}] delta    : scale={(Math.Abs(cal.Scale - expected.Scale) / expected.Scale) * 100:0.000}% " +
            $"originX={Math.Abs(cal.OriginX - expected.OriginX):0.000}px originY={Math.Abs(cal.OriginY - expected.OriginY):0.000}px");

        (Math.Abs(cal.Scale - expected.Scale) / expected.Scale).Should().BeLessThan(0.02, "scale within 2%");
        Math.Abs(cal.OriginX - expected.OriginX).Should().BeLessThan(5.0, "origin X within 5 px");
        Math.Abs(cal.OriginY - expected.OriginY).Should().BeLessThan(5.0, "origin Y within 5 px");
        // Compare orientation CLASS (the engine enumerates the discrete {0, π} map
        // orientation), not raw sign: Serbule's baseline rotation is +7e-5 rad —
        // numerically zero — so a recovered −1e-9 has the opposite Math.Sign while
        // being the same orientation. Bucket by nearest-to-0 vs nearest-to-π.
        NearPi(cal.RotationRadians).Should().Be(NearPi(expected.RotationRadians), "orientation class (0 vs π) matches");
    }

    // Orientation class: true when the rotation is closer to ±π than to 0 (the
    // mirrored/180° map orientation). Robust to near-zero sign flips.
    private static bool NearPi(double radians)
    {
        double a = Math.Abs(radians);
        return Math.Abs(a - Math.PI) < a; // closer to π than to 0
    }

    private sealed record StudyRef(string Type, string Name, double X, double Z);

    // Reads study/refs/<area>.json — a self-contained list the local user
    // provides alongside the screenshots ([{type,name,x,z}, ...]) so the replay
    // doesn't drag in the Mithril.Shared reference project graph. Returns empty
    // if absent (the presence guard test already reports the skip).
    private static List<LandmarkReference> LoadStudyRefs(string area)
    {
        var root = StudyRoot();
        if (root is null) return [];
        var path = Path.Combine(root, "refs", area + ".json");
        if (!File.Exists(path)) return [];
        using var stream = File.OpenRead(path);
        var entries = JsonSerializer.Deserialize<List<StudyRef>>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? [];
        return entries.Select(e => new LandmarkReference(e.Type, e.Name, new WorldCoord(e.X, 0, e.Z))).ToList();
    }
}
