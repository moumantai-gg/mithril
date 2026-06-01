using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class ProbeReferencesTests
{
    [Fact]
    public void Loads_eltibule_refs_with_canonical_types()
    {
        var landmarks = ProbeReferences.DefaultLandmarksPath();
        var npcs = ProbeReferences.DefaultNpcsPath();
        var refs = ProbeReferences.Load(landmarks, npcs, area: "AreaEltibule");

        refs.Should().NotBeEmpty();
        refs.Select(r => r.LandmarkType).Distinct().Should().BeSubsetOf(new[]
        {
            "Portal", "MeditationPillar", "TeleportationPlatform", "Npc",
        });
        refs.Should().HaveCount(38);
    }
}
