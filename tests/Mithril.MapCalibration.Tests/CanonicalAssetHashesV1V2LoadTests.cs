using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// mithril#1081 — the canonical-asset-hashes catalogue widens from
/// Schema v1 (<c>byPgVersion[pg][key] = "sha"</c>) to v2
/// (<c>byPgVersion[pg][key] = { sha, width, height }</c>). v1 files load via the
/// loader's wrapping fallback so existing hash-gate consumers don't break;
/// dim consumers see 0/0 and treat as catalogue miss.
/// </summary>
public sealed class CanonicalAssetHashesV1V2LoadTests
{
    [Fact]
    public void V1Json_LoadsWithWrappedEntries_ZeroDims()
    {
        var v1Json = """
            {
              "schemaVersion": 1,
              "byPgVersion": {
                "1.234": {
                  "Map_AreaTest": "abc123def"
                }
              }
            }
            """;

        var catalogue = CanonicalAssetHashesLoader.Parse(v1Json);

        catalogue.SchemaVersion.Should().Be(1, "loader preserves the source schema version for diagnostics");
        catalogue.ByPgVersion.Should().ContainKey("1.234");
        var entry = catalogue.ByPgVersion["1.234"]["Map_AreaTest"];
        entry.Sha.Should().Be("abc123def");
        entry.Width.Should().Be(0, "v1 records had no dims → dim consumers see catalogue miss");
        entry.Height.Should().Be(0);
    }

    [Fact]
    public void V2Json_LoadsNatively()
    {
        var v2Json = """
            {
              "schemaVersion": 2,
              "byPgVersion": {
                "1.234": {
                  "Map_AreaTest": { "sha": "abc123def", "width": 1024, "height": 768 }
                }
              }
            }
            """;

        var catalogue = CanonicalAssetHashesLoader.Parse(v2Json);

        catalogue.SchemaVersion.Should().Be(2);
        var entry = catalogue.ByPgVersion["1.234"]["Map_AreaTest"];
        entry.Sha.Should().Be("abc123def");
        entry.Width.Should().Be(1024);
        entry.Height.Should().Be(768);
    }

    [Fact]
    public void EmptyV1Stub_LoadsWithEmptyByPgVersion()
    {
        // The catalogue file ships today as an empty v1 stub.
        var stubJson = """{ "schemaVersion": 1, "byPgVersion": {} }""";

        var catalogue = CanonicalAssetHashesLoader.Parse(stubJson);

        catalogue.ByPgVersion.Should().BeEmpty();
    }

    [Fact]
    public void MissingSchemaVersion_DefaultsToV1Wrapping()
    {
        // Defensive: if a future hand-edit accidentally omits schemaVersion,
        // the loader treats the file as v1 (bare-string values) to avoid a
        // hard-crash on a recoverable shape.
        var noVersionJson = """
            {
              "byPgVersion": {
                "1.234": { "Map_AreaTest": "abc123def" }
              }
            }
            """;

        var catalogue = CanonicalAssetHashesLoader.Parse(noVersionJson);

        catalogue.ByPgVersion["1.234"]["Map_AreaTest"].Sha.Should().Be("abc123def");
        catalogue.ByPgVersion["1.234"]["Map_AreaTest"].Width.Should().Be(0);
    }
}
