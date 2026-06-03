using FluentAssertions;
using Mithril.MapCalibration;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public class MapSceneRefTests
{
    [Fact]
    public void Construction_RequiresAllThreeFields()
    {
        var scene = new MapSceneRef("AreaCave1", "Hogan's Basement", "Map_HogansKeepBasement");
        scene.ParentAreaKey.Should().Be("AreaCave1");
        scene.SceneFriendlyName.Should().Be("Hogan's Basement");
        scene.MapAssetKey.Should().Be("Map_HogansKeepBasement");
    }

    [Fact]
    public void DirectlyRegisteredArea_HasNullSceneFriendlyName()
    {
        var scene = new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule");
        scene.SceneFriendlyName.Should().BeNull();
        scene.MapAssetKey.Should().Be("Map_AreaSerbule");
    }

    [Fact]
    public void WithExpression_AllowsPartialMutation()
    {
        var original = new MapSceneRef("AreaCave1", "Hogan's Basement", "Map_HogansKeepBasement");
        var next = original with { SceneFriendlyName = "Goblin Dungeon", MapAssetKey = "Map_GoblinDungeon" };
        next.ParentAreaKey.Should().Be("AreaCave1");
        next.SceneFriendlyName.Should().Be("Goblin Dungeon");
        next.MapAssetKey.Should().Be("Map_GoblinDungeon");
    }
}
