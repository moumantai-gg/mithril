using FluentAssertions;
using Xunit;

namespace Arda.Dispatch.Tests;

public class VerbExtractorTests
{
    [Theory]
    [InlineData("LocalPlayer: ProcessDeleteItem(12345)", "ProcessDeleteItem")]
    [InlineData("LocalPlayer: ProcessAddItem(GoblinCap(84741837), -1, False)", "ProcessAddItem")]
    [InlineData("LocalPlayer: ProcessUpdateSkill(Toolcrafting, 7)", "ProcessUpdateSkill")]
    [InlineData("LocalPlayer: ProcessStartInteraction(123, 0, 45.5, Idle, \"NPC_Marna\")", "ProcessStartInteraction")]
    public void Parse_ProcessVerb_ReturnsVerbName(string line, string expectedVerb)
    {
        var result = VerbExtractor.Parse(line.AsSpan());
        result.Verb.ToString().Should().Be(expectedVerb);
    }

    [Fact]
    public void Parse_LoadingLevel_ReturnsSyntheticKey()
    {
        var result = VerbExtractor.Parse("LOADING LEVEL AreaSerbule".AsSpan());
        result.Verb.ToString().Should().Be(Verbs.LoadingLevel);
    }

    [Fact]
    public void Parse_InitializingArea_ReturnsSyntheticKey()
    {
        var result = VerbExtractor.Parse("!!! Initializing area! (502934): AreaSerbule".AsSpan());
        result.Verb.ToString().Should().Be(Verbs.InitializingArea);
    }

    [Fact]
    public void Parse_DownloadingMap_ReturnsSyntheticKey()
    {
        var line = "Downloading Map [0e88b64bdd834cc41a23b8357802f254] GUID 0e88b64bdd834cc41a23b8357802f254 for area Kur Mountains runtime key 0e88b64bdd834cc41a23b8357802f254[Map_AreaKurMountains]";
        var result = VerbExtractor.Parse(line.AsSpan());
        result.Verb.ToString().Should().Be(Verbs.DownloadingMap);
        result.Args.ToString().Should().Be(
            "[0e88b64bdd834cc41a23b8357802f254] GUID 0e88b64bdd834cc41a23b8357802f254 for area Kur Mountains runtime key 0e88b64bdd834cc41a23b8357802f254[Map_AreaKurMountains]");
    }

    [Fact]
    public void Parse_DownloadingMapAlone_ReturnsEmpty()
    {
        // Defensive: bare "Downloading Map" without a space-prefixed body should not match;
        // it's a line shape we've never observed and shouldn't synthesize.
        var result = VerbExtractor.Parse("Downloading Map".AsSpan());
        result.IsEmpty.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("some random text")]
    [InlineData("Download appearance loop @model(params)")]
    public void Parse_UnrecognizedLine_ReturnsEmpty(string line)
    {
        var result = VerbExtractor.Parse(line.AsSpan());
        result.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Parse_LocalPlayerPrefix_NoParens_ReturnsWholeAfterPrefix()
    {
        var result = VerbExtractor.Parse("LocalPlayer: SomethingWithoutParens".AsSpan());
        result.Verb.ToString().Should().Be("SomethingWithoutParens");
        result.Args.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Parse_ProcessVerb_ReturnsArgsIncludingParen()
    {
        var result = VerbExtractor.Parse("LocalPlayer: ProcessDeleteItem(12345, True)".AsSpan());
        result.Args.ToString().Should().Be("(12345, True)");
    }

    [Fact]
    public void Parse_LoadingLevel_ReturnsAreaKey()
    {
        var result = VerbExtractor.Parse("LOADING LEVEL AreaSerbule".AsSpan());
        result.Args.ToString().Should().Be("AreaSerbule");
    }

    [Fact]
    public void Parse_InitializingArea_ReturnsContent()
    {
        var result = VerbExtractor.Parse("!!! Initializing area! (502934): AreaSerbule".AsSpan());
        result.Args.ToString().Should().Be("(502934): AreaSerbule");
    }

    [Fact]
    public void Parse_UnrecognizedLine_ArgsEmpty()
    {
        var result = VerbExtractor.Parse("some random text".AsSpan());
        result.Args.IsEmpty.Should().BeTrue();
    }

    // ── Chat verb extraction ────────────────────────────────────────────

    [Fact]
    public void Parse_ChatLoginBanner_ReturnsSyntheticKey()
    {
        var result = VerbExtractor.Parse("**** Logged In As Emraell. Server Laeth. Timezone Offset 01:00:00.".AsSpan());
        result.Verb.ToString().Should().Be(Verbs.ChatLoginBanner);
    }

    [Fact]
    public void Parse_StatusInventory_ReturnsSyntheticKey()
    {
        var result = VerbExtractor.Parse("[Status] Apple x2 added to inventory.".AsSpan());
        result.Verb.ToString().Should().Be(Verbs.StatusInventory);
    }

    [Fact]
    public void Parse_StatusNonInventory_ReturnsEmpty()
    {
        var result = VerbExtractor.Parse("[Status] You gained 50 XP.".AsSpan());
        result.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Parse_StatusLocationHint_ReturnsEmpty()
    {
        var result = VerbExtractor.Parse("[Status] The Iron Vein is 25m east and 30m north".AsSpan());
        result.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Parse_ChatPlayerLine_ReturnsSyntheticKey()
    {
        var result = VerbExtractor.Parse("[Trade] Emraell: WTS something".AsSpan());
        result.Verb.ToString().Should().Be(Verbs.ChatPlayerLine);
    }

    [Fact]
    public void Parse_BareLoadingLevel_ReturnsSyntheticKey()
    {
        var result = VerbExtractor.Parse("LOADING LEVEL".AsSpan());
        result.Verb.ToString().Should().Be(Verbs.LoadingLevel);
    }

    [Fact]
    public void Parse_LoadingLevelS_DoesNotMatch()
    {
        var result = VerbExtractor.Parse("LOADING LEVELS".AsSpan());
        result.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Parse_BareLoadingLevel_ArgsEmpty()
    {
        var result = VerbExtractor.Parse("LOADING LEVEL".AsSpan());
        result.Args.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Parse_ChatLoginBanner_ArgsFullLine()
    {
        var line = "**** Logged In As Emraell. Server Laeth. Timezone Offset 01:00:00.";
        var result = VerbExtractor.Parse(line.AsSpan());
        result.Args.ToString().Should().Be(line);
    }

    [Fact]
    public void Parse_StatusInventory_ArgsFullLine()
    {
        var line = "[Status] Apple x2 added to inventory.";
        var result = VerbExtractor.Parse(line.AsSpan());
        result.Args.ToString().Should().Be(line);
    }

    [Fact]
    public void Parse_StatusNonInventory_ArgsEmpty()
    {
        var result = VerbExtractor.Parse("[Status] You gained 50 XP.".AsSpan());
        result.Args.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Parse_ChatPlayerLine_ArgsFullLine()
    {
        var line = "[Trade] Emraell: WTS something";
        var result = VerbExtractor.Parse(line.AsSpan());
        result.Args.ToString().Should().Be(line);
    }
}
