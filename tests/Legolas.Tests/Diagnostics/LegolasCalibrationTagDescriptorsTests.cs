using System.Linq;
using FluentAssertions;
using Legolas.Diagnostics;
using Mithril.Shared.Telemetry.Abstractions;
using Xunit;

namespace Legolas.Tests.Diagnostics;

/// <summary>
/// Catalog parity check for <see cref="LegolasCalibrationTagDescriptors"/> (#1093).
/// Mirrors <c>Mithril.Shared.Tests.Telemetry.MithrilSharedTagDescriptorsTests</c> +
/// <c>Arda.Dispatch.Tests.ArdaTagDescriptorsTests</c>. Asserts the new keys are
/// declared on the expected subsystem; no behaviour beyond a vocabulary check.
/// </summary>
public class LegolasCalibrationTagDescriptorsTests
{
    /// <summary>Subsystem scope under test — matches the new <c>ActivitySource</c>/<c>Meter</c> name.</summary>
    private const string Subsystem = "Mithril.Legolas.Calibration";

    /// <summary>Keys we expect declared by this provider — sourced from spec §4.4.</summary>
    /// <remarks>
    /// <c>outcome</c> is intentionally NOT in this list: it's already declared at
    /// <c>Mithril.Reference</c> by <c>MithrilSharedTagDescriptors</c>, and the
    /// catalog dedups by key (different subsystems would conflict). See the
    /// remarks on <see cref="LegolasCalibrationTagDescriptors"/>.
    /// </remarks>
    public static readonly string[] ExpectedKeys =
    {
        "area",
        "scene.asset_key",
        "scene.parent_area_key",
        "cal.source",
        "cal.frame",
        "cal.residual_px",
        "cal.refs",
        "cal.path",
        "consumer",
        "frame",
        "refs_count",
        "ghosts_built",
        "from",
        "to",
        "placements",
    };

    [Fact]
    public void Describe_is_non_empty()
    {
        new LegolasCalibrationTagDescriptors().Describe().Should().NotBeEmpty();
    }

    [Fact]
    public void All_descriptors_have_non_empty_key_and_subsystem_and_description()
    {
        var provider = new LegolasCalibrationTagDescriptors();
        foreach (var d in provider.Describe())
        {
            d.Key.Should().NotBeNullOrEmpty();
            d.Subsystem.Should().NotBeNullOrEmpty();
            d.Description.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void Keys_are_unique_within_provider()
    {
        var provider = new LegolasCalibrationTagDescriptors();
        var keys = provider.Describe().Select(d => d.Key).ToArray();
        keys.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void All_descriptors_are_scoped_to_LegolasCalibration_subsystem()
    {
        var provider = new LegolasCalibrationTagDescriptors();
        foreach (var d in provider.Describe())
        {
            d.Subsystem.Should().Be(Subsystem);
        }
    }

    [Fact]
    public void All_descriptors_are_Safe_classification()
    {
        // Per spec §6: every new tag value is a fixed-vocabulary string or a
        // numeric. None carries PII / path strings — all Safe.
        var provider = new LegolasCalibrationTagDescriptors();
        foreach (var d in provider.Describe())
        {
            d.Classification.Should().Be(PiiClassification.Safe);
        }
    }

    [Fact]
    public void Expected_keys_are_all_declared()
    {
        var provider = new LegolasCalibrationTagDescriptors();
        var declared = provider.Describe().Select(d => d.Key).ToHashSet();
        foreach (var expected in ExpectedKeys)
        {
            declared.Should().Contain(expected, $"spec §4.4 lists '{expected}' as part of the Legolas calibration vocabulary");
        }
    }
}
