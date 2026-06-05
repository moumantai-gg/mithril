using System;
using System.Collections.Generic;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// #931 canonical-hash gate: match → accept, mismatch → reject+warn, PG version
/// absent from the catalogue → accept-with-warn (never hard-fail a newer patch).
/// </summary>
public sealed class CanonicalAssetHashGateTests
{
    private static CanonicalAssetHashGate Gate()
    {
        var catalogue = new CanonicalAssetHashes(2, new Dictionary<string, Dictionary<string, CanonicalAssetHashEntry>>(StringComparer.Ordinal)
        {
            ["1.2.3"] = new(StringComparer.Ordinal)
            {
                ["icons"] = new CanonicalAssetHashEntry("aaaa", 1024, 1024),
                ["AreaSerbule"] = new CanonicalAssetHashEntry("bbbb", 1024, 1024),
            },
        });
        return CanonicalAssetHashGate.FromCatalogue(catalogue);
    }

    [Fact]
    public void Match_accepts_without_warning()
    {
        var v = Gate().Check("1.2.3", "icons", "aaaa");

        v.Accepted.Should().BeTrue();
        v.WithWarning.Should().BeFalse();
    }

    [Fact]
    public void Mismatch_rejects()
    {
        var v = Gate().Check("1.2.3", "AreaSerbule", "deadbeef");

        v.Accepted.Should().BeFalse();
        v.Reason.Should().Contain("mismatch");
    }

    [Fact]
    public void Uncatalogued_pg_version_accepts_with_warning()
    {
        var v = Gate().Check("9.9.9", "icons", "whatever");

        v.Accepted.Should().BeTrue();
        v.WithWarning.Should().BeTrue();
    }

    [Fact]
    public void Uncatalogued_artifact_for_known_version_accepts_with_warning()
    {
        var v = Gate().Check("1.2.3", "AreaNewZone", "whatever");

        v.Accepted.Should().BeTrue();
        v.WithWarning.Should().BeTrue();
    }

    [Fact]
    public void No_pg_version_accepts_with_warning()
    {
        var v = Gate().Check(null, "icons", "whatever");

        v.Accepted.Should().BeTrue();
        v.WithWarning.Should().BeTrue();
    }

    [Fact]
    public void Embedded_catalogue_loads_and_defaults_to_accept_with_warn()
    {
        // The committed canonical-asset-hashes.json ships an empty catalogue for
        // v1 → every check is accept-with-warn (trust extraction + confidence gate).
        var gate = CanonicalAssetHashGate.Load(logger: null);

        var v = gate.Check("1.2.3", "icons", "anything");
        v.Accepted.Should().BeTrue();
        v.WithWarning.Should().BeTrue();
    }

    [Fact]
    public void Case_insensitive_hash_comparison()
    {
        var v = Gate().Check("1.2.3", "icons", "AAAA");

        v.Accepted.Should().BeTrue();
        v.WithWarning.Should().BeFalse();
    }
}
