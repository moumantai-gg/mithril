using System.Globalization;
using FluentAssertions;
using Mithril.Shared.Wpf;
using Xunit;

namespace Mithril.Shared.Tests.Wpf;

/// <summary>
/// Coverage for <see cref="EmptyStringFallbackConverter"/> — the display-layer fix for
/// the Palantir unlabeled-pin regression introduced when PR-B removed the
/// <c>MapPinRow.From</c> projection that previously substituted "Unnamed pin" for
/// empty/null labels. WPF's <c>TargetNullValue</c> fires only for null, not for
/// empty string, so a converter is needed to cover all three cases.
/// </summary>
public sealed class EmptyStringFallbackConverterTests
{
    private static readonly EmptyStringFallbackConverter Sut = new();

    private static object? Convert(object? value, object? parameter)
        => Sut.Convert(value, typeof(string), parameter, CultureInfo.InvariantCulture);

    [Fact]
    public void Convert_NullInput_ReturnsParameter()
    {
        Convert(null, "Unnamed pin").Should().Be("Unnamed pin");
    }

    [Fact]
    public void Convert_EmptyInput_ReturnsParameter()
    {
        Convert("", "Unnamed pin").Should().Be("Unnamed pin");
    }

    [Fact]
    public void Convert_WhitespaceInput_ReturnsParameter()
    {
        Convert("   ", "Unnamed pin").Should().Be("Unnamed pin");
    }

    [Fact]
    public void Convert_NonEmptyInput_ReturnsInputUnchanged()
    {
        Convert("Tomb portal", "Unnamed pin").Should().Be("Tomb portal");
    }

    [Fact]
    public void Convert_NullParameter_ReturnsEmptyString()
    {
        Convert("", null).Should().Be("");
    }

    [Fact]
    public void ConvertBack_IsNotSupported()
    {
        var act = () => Sut.ConvertBack("anything", typeof(string), null, CultureInfo.InvariantCulture);
        act.Should().Throw<NotSupportedException>();
    }
}
