using System.ComponentModel;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibration.Harness;
using Xunit;

namespace Mithril.Tools.MapCalibration.Harness.Tests;

public class CalibrationRefTests
{
    private static CalibrationRef NewRef() => new()
    {
        Name = "Wardrobe",
        Kind = "Npc",
        Source = CalibrationRefSource.Manual,
        Confidence = 1.0,
        World = new WorldCoord(1, 0, 2),
        TexturePixel = new TexturePixel(3, 4),
    };

    [Fact]
    public void Defaults_to_enabled()
    {
        NewRef().Enabled.Should().BeTrue();
    }

    [Fact]
    public void Mutating_texture_pixel_raises_change_notification()
    {
        var r = NewRef();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)r).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        r.TexturePixel = new TexturePixel(9, 9);

        raised.Should().Contain(nameof(CalibrationRef.TexturePixel));
    }

    [Fact]
    public void Toggling_enabled_raises_change_notification()
    {
        var r = NewRef();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)r).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        r.Enabled = false;

        raised.Should().Contain(nameof(CalibrationRef.Enabled));
    }
}
