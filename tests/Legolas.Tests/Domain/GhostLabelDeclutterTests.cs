using FluentAssertions;
using Legolas.Domain;
using Legolas.Tests.TestSupport;

namespace Legolas.Tests.Domain;

/// <summary>
/// #495 — the "Validate calibration" label declutter: every reference still
/// yields a marker (the dot always draws); only labels whose boxes would
/// collide with an already-placed label are suppressed, greedily by input
/// order (the area service emits NPCs first, so NPC labels win for free).
/// </summary>
public class GhostLabelDeclutterTests
{
    // Scale 1, no rotation/origin, +Z convention: ProjectWorld(x,0,z) = (x, -z).
    private static readonly AreaCalibration Cal = new(1, 0, 0, 0, 3, 0);

    private static CalibrationReference Ref(string name, double x, double z) =>
        new(name, "Landmark", new WorldCoord(x, 0, z));

    [Fact]
    public void Every_reference_yields_a_marker_even_when_labels_collide()
    {
        var refs = new[] { Ref("A", 0, 0), Ref("B", 0, 0), Ref("C", 0, 0) };

        var markers = GhostLabelDeclutter.Build(refs, Cal);

        markers.Should().HaveCount(3, "the dot always draws — only labels declutter");
        markers.Select(m => m.Name).Should().Equal("A", "B", "C");
    }

    [Fact]
    public void First_in_input_order_wins_a_label_contest()
    {
        // Same projected point ⇒ identical label boxes ⇒ all but the first
        // collide. Input order is the priority (service: NPCs before landmarks).
        var refs = new[] { Ref("First", 0, 0), Ref("Second", 0, 0) };

        var markers = GhostLabelDeclutter.Build(refs, Cal);

        markers[0].ShowLabel.Should().BeTrue();
        markers[1].ShowLabel.Should().BeFalse();
    }

    [Fact]
    public void Reversing_input_order_flips_which_label_shows()
    {
        var markers = GhostLabelDeclutter.Build(
            new[] { Ref("Second", 0, 0), Ref("First", 0, 0) }, Cal);

        markers[0].Name.Should().Be("Second");
        markers[0].ShowLabel.Should().BeTrue();
        markers[1].ShowLabel.Should().BeFalse();
    }

    [Fact]
    public void Well_separated_references_all_keep_their_labels()
    {
        var refs = new[]
        {
            Ref("North", 0, 0),
            Ref("East", 500, -300),
            Ref("Far", -800, 600),
        };

        var markers = GhostLabelDeclutter.Build(refs, Cal);

        markers.Should().OnlyContain(m => m.ShowLabel);
        markers[0].Pixel.Should().Be(Cal.WorldToWindow(new WorldCoord(0, 0, 0)).AsOverlay());
    }
}
