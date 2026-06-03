using System.Collections.Generic;
using FluentAssertions;
using Mithril.Shared.Game;
using Xunit;

namespace Mithril.Shared.Tests.Game;

/// <summary>
/// #919: GameProcessName + CalibrationGoodResidualPx relocated from
/// LegolasSettings to the shared <see cref="GameConfig"/>. These tests pin the
/// contracts the relocated consumers (ForegroundFocusGate substring match,
/// PinCalibrationCoordinator residual gate) rely on.
/// </summary>
public sealed class GameConfigSettingsTests
{
    [Fact]
    public void GameProcessName_default_is_ProjectGorgon()
        => new GameConfig().GameProcessName.Should().Be("ProjectGorgon");

    [Fact]
    public void GameProcessName_trims_leading_and_trailing_whitespace_on_set()
    {
        var c = new GameConfig { GameProcessName = "  PG_x64  " };
        c.GameProcessName.Should().Be("PG_x64");
    }

    [Fact]
    public void GameProcessName_preserves_internal_whitespace()
    {
        // Substring match allows launchers whose image name contains a space.
        var c = new GameConfig { GameProcessName = "Project Gorgon" };
        c.GameProcessName.Should().Be("Project Gorgon");
    }

    [Fact]
    public void GameProcessName_null_becomes_empty()
    {
        var c = new GameConfig { GameProcessName = null! };
        c.GameProcessName.Should().Be(string.Empty);
    }

    [Fact]
    public void GameProcessName_raises_PropertyChanged_on_change()
    {
        var c = new GameConfig();
        var changed = new List<string?>();
        c.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        c.GameProcessName = "Different";

        changed.Should().Contain(nameof(GameConfig.GameProcessName));
    }

    [Fact]
    public void GameProcessName_does_not_raise_PropertyChanged_when_post_trim_unchanged()
    {
        var c = new GameConfig { GameProcessName = "ProjectGorgon" };
        var changed = new List<string?>();
        c.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        c.GameProcessName = "  ProjectGorgon  "; // post-trim equal to existing

        changed.Should().NotContain(nameof(GameConfig.GameProcessName));
    }

    [Fact]
    public void CalibrationGoodResidualPx_default_is_12()
        => new GameConfig().CalibrationGoodResidualPx.Should().Be(12.0);

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public void CalibrationGoodResidualPx_non_positive_resets_to_12(double bad)
    {
        var c = new GameConfig { CalibrationGoodResidualPx = bad };
        c.CalibrationGoodResidualPx.Should().Be(12.0);
    }

    [Fact]
    public void CalibrationGoodResidualPx_accepts_positive_value()
    {
        var c = new GameConfig { CalibrationGoodResidualPx = 20.0 };
        c.CalibrationGoodResidualPx.Should().Be(20.0);
    }

    [Fact]
    public void CalibrationGoodResidualPx_raises_PropertyChanged_on_change()
    {
        var c = new GameConfig();
        var changed = new List<string?>();
        c.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        c.CalibrationGoodResidualPx = 25.0;

        changed.Should().Contain(nameof(GameConfig.CalibrationGoodResidualPx));
    }

    // #959: InstallRoot = the Steam install dir consumed by the asset-extractor
    // sidecar, distinct from GameRoot (the LocalLow data dir).
    [Fact]
    public void InstallRoot_default_is_empty()
        => new GameConfig().InstallRoot.Should().BeEmpty();

    [Fact]
    public void InstallRoot_does_not_affect_data_dir_paths()
    {
        // InstallRoot must NOT participate in the Player.log/ChatLogs/Reports
        // recomputation — those stay GameRoot-only.
        var c = new GameConfig
        {
            GameRoot = @"C:\Data\PG",
            InstallRoot = @"D:\Steam\common\Project Gorgon",
        };

        c.PlayerLogPath.Should().Be(@"C:\Data\PG\Player.log");
        c.ChatLogDirectory.Should().Be(@"C:\Data\PG\ChatLogs");
        c.ReportsDirectory.Should().Be(@"C:\Data\PG\Reports");
    }

    [Fact]
    public void InstallRoot_raises_PropertyChanged_on_change()
    {
        var c = new GameConfig();
        var changed = new List<string?>();
        c.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        c.InstallRoot = @"D:\Steam\common\Project Gorgon";

        changed.Should().Contain(nameof(GameConfig.InstallRoot));
    }

    [Fact]
    public void InstallRoot_change_does_not_raise_data_dir_path_changes()
    {
        var c = new GameConfig { GameRoot = @"C:\Data\PG" };
        var changed = new List<string?>();
        c.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        c.InstallRoot = @"D:\Steam\common\Project Gorgon";

        changed.Should().NotContain(nameof(GameConfig.PlayerLogPath));
        changed.Should().NotContain(nameof(GameConfig.ChatLogDirectory));
        changed.Should().NotContain(nameof(GameConfig.ReportsDirectory));
    }
}
