using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MahApps.Metro.IconPacks;

namespace Mithril.Shared.Wpf;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StringToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s ? (Color)ColorConverter.ConvertFromString(s)! : Colors.Gray;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Two-way bridge between an ARGB-hex string property (canonical
/// <c>#AARRGGBB</c>) and a WPF <see cref="Color"/>, for binding hex-string
/// settings to color-picker controls (e.g. <c>mah:ColorPicker.SelectedColor</c>).
/// <para>
/// <see cref="Convert"/> (hex → Color): malformed / null input falls back to
/// opaque magenta — matching the existing fail-loud convention used by
/// Legolas's settings layer so a bad persisted value is visible everywhere
/// rather than silently transparent.
/// </para>
/// <para>
/// <see cref="ConvertBack"/> (Color → hex): non-<see cref="Color"/> input
/// (including <see langword="null"/>) returns <see cref="Binding.DoNothing"/>
/// so the source property is NOT mutated. MahApps' <c>SelectedColor</c>
/// dependency property is typed <c>Color?</c> and dispatches <see langword="null"/>
/// on initialisation races / mid-edit hex clears / future reset paths;
/// fabricating a literal hex on that signal would silently clobber the user's
/// saved color with opaque magenta.
/// </para>
/// </summary>
public sealed class ArgbHexToColorConverter : IValueConverter
{
    private static readonly Color Fallback = Colors.Magenta; // #FFFF00FF

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && TryParseArgb(s, out var color) ? color : Fallback;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Color c
            ? $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}"
            : Binding.DoNothing;

    private static bool TryParseArgb(string input, out Color color)
    {
        color = Fallback;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = input.Trim();
        if (s.StartsWith('#')) s = s[1..];
        // Accept 6-digit RGB (alpha = FF) or 8-digit ARGB.
        if (s.Length is not (6 or 8)) return false;
        foreach (var ch in s)
        {
            if (!Uri.IsHexDigit(ch)) return false;
        }
        byte a = s.Length == 8
            ? byte.Parse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : (byte)0xFF;
        int rgbStart = s.Length == 8 ? 2 : 0;
        byte r = byte.Parse(s.AsSpan(rgbStart, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte g = byte.Parse(s.AsSpan(rgbStart + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte b = byte.Parse(s.AsSpan(rgbStart + 4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        color = Color.FromArgb(a, r, g, b);
        return true;
    }
}

public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class PositiveIntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}

/// <summary>
/// Splits a PascalCase / camelCase internal name into space-separated words for display.
/// e.g. "MainHand" → "Main Hand", "OffHand" → "Off Hand", "Head" → "Head". Heuristic
/// (no strings_all.json lookup) — works for known equip-slot names which are all
/// PascalCase ASCII identifiers.
/// </summary>
public sealed class CamelCaseSplitConverter : IValueConverter
{
    private static readonly Regex SplitPattern = new("(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);

    /// <summary>
    /// Splits a PascalCase / camelCase token into space-separated words.
    /// Exposed for non-XAML callers (VMs, services) that want the same heuristic
    /// without going through the converter pipeline.
    /// </summary>
    public static string Split(string value) =>
        string.IsNullOrEmpty(value) ? value : SplitPattern.Replace(value, " ");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s ? Split(s) : (value ?? "");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Maps a <see cref="LinkGlyph"/> to its concrete <see cref="PackIconLucideKind"/> for
/// the <see cref="Link"/> template's lead glyph. Delegates to
/// <see cref="Link.ToLucideKind"/> so the mapping has one home (and is unit-tested
/// there). <see cref="LinkGlyph.None"/> yields <see cref="PackIconLucideKind.None"/> —
/// the template independently collapses the glyph element via
/// <see cref="LinkGlyphToVisibilityConverter"/>.
/// </summary>
public sealed class LinkGlyphToLucideKindConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is LinkGlyph g ? Link.ToLucideKind(g) : PackIconLucideKind.None;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Collapses the <see cref="Link"/> lead-glyph element when the glyph is
/// <see cref="LinkGlyph.None"/> (the name stands alone), Visible otherwise.
/// </summary>
public sealed class LinkGlyphToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is LinkGlyph g && g != LinkGlyph.None ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// The G3-amend-2 "<c>FontSize × n</c>" converter — the single mechanism by which every
/// Silmarillion grammar tier sizes itself in <em>em</em> (relative to inherited
/// <c>FontSize</c>) rather than fixed px, so sizes track the Appearance base-size slider
/// (<c>docs/silmarillion-visual-grammar.md</c> · "All sizes are em-relative, not px").
/// <para>
/// <c>value</c> is the inherited <c>FontSize</c> (a <see cref="double"/>, supplied via a
/// <c>RelativeSource Self</c>/<c>AncestorType</c> binding to the element whose inherited
/// font-size defines 1em); <c>parameter</c> is the factor (a <see cref="double"/>,
/// parsed culture-<em>invariantly</em> so a XAML <c>ConverterParameter=1.125</c> is not
/// mis-read under comma-decimal locales). Returns <c>value * factor</c>.
/// </para>
/// Null / non-numeric / non-positive inputs degrade to <see cref="double.NaN"/> ("size
/// to content") rather than throwing — a missing inherited FontSize must never crash a
/// detail pane.
/// </summary>
public sealed class FontSizeTimesConverter : IValueConverter
{
    /// <summary>
    /// Pure size math, factored out so it is unit-testable without the converter /
    /// visual tree. Returns <c>fontSize * factor</c>; <see cref="double.NaN"/> when
    /// <paramref name="fontSize"/> is not a positive finite number.
    /// </summary>
    public static double Scale(double fontSize, double factor) =>
        double.IsFinite(fontSize) && fontSize > 0 ? fontSize * factor : double.NaN;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double fontSize = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => double.NaN,
        };

        double factor = parameter switch
        {
            double d => d,
            float f => f,
            int i => i,
            string s when double.TryParse(
                s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
            _ => double.NaN,
        };

        return double.IsFinite(factor) ? Scale(fontSize, factor) : double.NaN;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Returns <see cref="IValueConverter.Convert"/>'s parameter (cast to string) when the
/// input is null, empty, or all-whitespace; otherwise returns the input value unchanged.
/// Used as the WPF fallback for binding to string properties whose empty-string value
/// should display a placeholder (WPF's <c>TargetNullValue</c> only fires for null, not
/// for <c>""</c>).
/// </summary>
public sealed class EmptyStringFallbackConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s)) return s;
        return parameter as string ?? string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Two-way converter between an enum value and <c>bool</c> for radio-button bindings.
/// Usage: <c>IsChecked="{Binding Foo, Converter={StaticResource EnumToBoolConverter}, ConverterParameter=SomeEnumValue}"</c>.
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is string s && !string.IsNullOrEmpty(s))
            return Enum.Parse(targetType, s);
        return Binding.DoNothing;
    }
}
