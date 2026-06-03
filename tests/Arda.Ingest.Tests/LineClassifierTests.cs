using Arda.Ingest.Classification;
using Arda.Ingest.Clock;
using FluentAssertions;
using Xunit;

namespace Arda.Ingest.Tests;

public class LineClassifierTests
{
    private readonly LineClassifier _sut;

    public LineClassifierTests()
    {
        var clock = new PlayerLogClock(TimeProvider.System);
        _sut = new LineClassifier(clock);
    }

    [Fact]
    public void Classify_ValidTimestampPrefix_ReturnsStrippedTextAndParsedTimestamp()
    {
        var line = "[14:30:05] LocalPlayer: ProcessAddItem(123, -1, False)".AsSpan();

        var result = _sut.Classify(line);

        result.Should().NotBeNull();
        result!.Value.Log.Should().Be("LocalPlayer: ProcessAddItem(123, -1, False)");
        result.Value.Timestamp.Should().NotBeNull();
        result.Value.Timestamp!.Value.Hour.Should().Be(14);
        result.Value.Timestamp!.Value.Minute.Should().Be(30);
        result.Value.Timestamp!.Value.Second.Should().Be(5);
    }

    [Fact]
    public void Classify_ConnectingSystemPattern_ReturnsClassifiedLineWithNullTimestamp()
    {
        var line = "Connecting to host port 1234".AsSpan();

        var result = _sut.Classify(line);

        result.Should().NotBeNull();
        result!.Value.Log.Should().Be("Connecting to host port 1234");
        result.Value.Timestamp.Should().BeNull();
    }

    [Fact]
    public void Classify_EventOkSystemPattern_ReturnsClassifiedLineWithNullTimestamp()
    {
        var line = "EVENT(Ok): connected, url=server.example.com, port=5000".AsSpan();

        var result = _sut.Classify(line);

        result.Should().NotBeNull();
        result!.Value.Log.Should().Be("EVENT(Ok): connected, url=server.example.com, port=5000");
        result.Value.Timestamp.Should().BeNull();
    }

    [Fact]
    public void Classify_DownloadingMapSystemPattern_ReturnsClassifiedLineWithNullTimestamp()
    {
        var line = "Downloading Map [44d50fb35fa65dd4cbb84e3af49ca0a4] GUID 44d50fb35fa65dd4cbb84e3af49ca0a4 for area Hogan's Basement runtime key 44d50fb35fa65dd4cbb84e3af49ca0a4[Map_HogansKeepBasement]".AsSpan();

        var result = _sut.Classify(line);

        result.Should().NotBeNull();
        result!.Value.Log.Should().Contain("[Map_HogansKeepBasement]");
        result.Value.Timestamp.Should().BeNull();
        result.Value.Raw.Should().BeNull();
    }

    [Theory]
    [InlineData("LoadAssetAsync: eq-x-m2-head-0. Status=None.")]
    [InlineData("Shader warmup: 15 shaders loaded")]
    [InlineData("GC collected 12345 bytes")]
    public void Classify_EngineNoise_ReturnsNull(string line)
    {
        var result = _sut.Classify(line.AsSpan());

        result.Should().BeNull();
    }

    [Fact]
    public void Classify_EmptyString_ReturnsNull()
    {
        var result = _sut.Classify(ReadOnlySpan<char>.Empty);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("Just some random text")]
    [InlineData("12345")]
    [InlineData("no timestamp here at all")]
    public void Classify_RandomText_ReturnsNull(string line)
    {
        var result = _sut.Classify(line.AsSpan());

        result.Should().BeNull();
    }

    [Fact]
    public void Classify_TimestampedLine_StripsPrefix()
    {
        var line = "[09:15:42] Some game event occurred".AsSpan();

        var result = _sut.Classify(line);

        result.Should().NotBeNull();
        result!.Value.Log.Should().Be("Some game event occurred");
        result.Value.Log.Should().NotContain("[09:15:42]");
    }

    [Fact]
    public void Classify_TimestampedLine_RawIsNull()
    {
        var line = "[14:30:05] LocalPlayer: action".AsSpan();

        var result = _sut.Classify(line);

        result.Should().NotBeNull();
        result!.Value.Raw.Should().BeNull();
    }

    [Fact]
    public void Classify_SystemPattern_RawIsNull()
    {
        var line = "Connecting to gameserver port 5555".AsSpan();

        var result = _sut.Classify(line);

        result.Should().NotBeNull();
        result!.Value.Raw.Should().BeNull();
    }
}
