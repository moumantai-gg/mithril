using System;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.DependencyInjection;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// Task 20 (#914): <see cref="CalibrationSource.AutoCapture"/> is an additive
/// enum value persisted by name (§D3 — no SchemaVersion bump). A gate-passing
/// auto solve is as trustworthy as a manual one, so it lands in the user store
/// via <c>SaveUserRefinement</c> and gets user-store precedence by construction
/// (no resolver branch on the enum value — see <c>MapCalibrationService.GetCalibration</c>).
/// </summary>
public sealed class AutoCaptureCalibrationSourceTests
{
    [Fact]
    public void AutoCapture_round_trips_by_name()
    {
        var cal = new AreaCalibration(1, 0, 10, 10, 6, 0.7) { Source = CalibrationSource.AutoCapture };

        var json = JsonSerializer.Serialize(cal, MapCalibrationJsonContext.Default.AreaCalibration);
        json.Should().Contain("\"AutoCapture\"", "UseStringEnumConverter emits the enum member name");
        json.Should().NotMatchRegex("\"source\"\\s*:\\s*3", "a numeric enum value must NOT appear");

        JsonSerializer.Deserialize(json, MapCalibrationJsonContext.Default.AreaCalibration)!
            .Source.Should().Be(CalibrationSource.AutoCapture);
    }

    [Fact]
    public void Downgrade_window_an_unknown_source_name_drops_only_that_entry_not_the_whole_store()
    {
        // §D3 downgrade-window documentation, post GATE-2 Fix A. AutoCapture is
        // persisted by NAME. A downgraded pre-AutoCapture build reading a
        // refinements.json that contains "AutoCapture" hits the source-gen
        // string-enum converter, which THROWS JsonException on an unknown member
        // name (verified here so the behaviour is pinned, not assumed). The real
        // consumer path is UserRefinementStore.Load, which now deserialises each
        // area entry INDIVIDUALLY and skips+warns ONLY the unparseable entry while
        // every other area survives — a durable, per-entry degrade rather than the
        // earlier whole-store wipe.
        var dir = Path.Combine(Path.GetTempPath(), "mithril-downgrade-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "refinements.json");
        try
        {
            // (a) the single-record converter genuinely throws on the unknown name.
            var directParse = () => JsonSerializer.Deserialize(
                "{\"source\":\"SomeFutureSource\"}", MapCalibrationJsonContext.Default.AreaCalibration);
            directParse.Should().Throw<JsonException>(
                "the source-gen string-enum converter rejects unknown member names");

            // (b) Two area entries: one valid UserRefinement, one carrying a Source
            // name the converter can't parse (stands in for a future/unknown source
            // name as seen by a build that predates it). The poisoned entry must be
            // dropped while the valid entry survives — NOT a whole-store wipe.
            //
            // Note: the file is v1 shape (bare area keys) intentionally — this
            // exercises BOTH the per-entry resilience (the original fix) AND the
            // v1→v2 prefix migrator (#1021 Task 18) at once. The survivor is
            // looked up under its migrated "Map_<X>" key.
            File.WriteAllText(path,
                "{\"schemaVersion\":1,\"calibrations\":{" +
                "\"AreaSerbule\":{" +
                "\"scale\":1,\"rotationRadians\":0,\"originX\":10,\"originY\":10," +
                "\"referenceCount\":6,\"residualPixels\":0.7,\"source\":\"SomeFutureSource\"}," +
                "\"AreaEltibule\":{" +
                "\"scale\":2,\"rotationRadians\":0,\"originX\":42,\"originY\":99," +
                "\"referenceCount\":8,\"residualPixels\":0.5,\"source\":\"UserRefinement\"}" +
                "}}");

            var build = () => MapCalibrationServiceCollectionExtensions.Build(dir);
            var service = build.Should().NotThrow(
                "a single unparseable entry must degrade per-entry, not crash the loader").Subject;

            // The poisoned entry (originX=10) is dropped: never served as a user
            // refinement (a bundled baseline may win, but never the bad transform).
            // Check under BOTH the bare v1 key and the migrated v2 key so the
            // assertion is robust regardless of whether per-entry resilience
            // skipped it before or after key transformation.
            foreach (var lookupKey in new[] { "AreaSerbule", "Map_AreaSerbule" })
            {
                var poisoned = service.GetCalibration(new MapSceneRef(string.Empty, null, lookupKey));
                (poisoned is null || poisoned.OriginX != 10 || poisoned.Source != CalibrationSource.UserRefinement)
                    .Should().BeTrue($"the unparseable user-store refinement must not be served (key={lookupKey})");
            }

            // The valid sibling entry SURVIVES — this is the resilience the fix
            // adds. Looked up under the migrated "Map_AreaEltibule" key per the
            // v1→v2 prefix migrator. Bare "AreaEltibule" is intentionally NOT
            // resolvable post-migration — that's the new keying contract.
            var survivor = service.GetCalibration(new MapSceneRef(string.Empty, null, "Map_AreaEltibule"));
            survivor.Should().NotBeNull("a poisoned sibling entry must not wipe the whole store");
            survivor!.OriginX.Should().Be(42, "the valid entry's transform is preserved verbatim");
            survivor.OriginY.Should().Be(99);
            survivor.Source.Should().Be(CalibrationSource.UserRefinement);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
