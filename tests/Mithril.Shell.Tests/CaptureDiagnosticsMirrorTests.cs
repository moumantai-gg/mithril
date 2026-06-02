using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Mithril.Shell.DependencyInjection;
using Xunit;

namespace Mithril.Shell.Tests;

/// <summary>
/// #984 Task 9: the Settings → Diagnostics calibration-bundle-dump checkbox flips
/// <see cref="ShellSettings.DumpCalibrationBundles"/>, which must mirror onto the
/// live <see cref="CaptureDiagnosticsOptions"/> singleton the engine reads —
/// without re-resolving the DI graph. Exercises the exact seed + PropertyChanged
/// wiring <c>ShellComposition</c> registers (<see cref="ShellComposition.MirrorCaptureDiagnostics"/>).
/// </summary>
public sealed class CaptureDiagnosticsMirrorTests
{
    [Fact]
    public void Seeds_the_options_from_current_settings()
    {
        var settings = new ShellSettings
        {
            DumpCalibrationBundles = true,
        };

        var options = ShellComposition.MirrorCaptureDiagnostics(settings);

        options.DumpCalibrationBundles.Should().BeTrue();
    }

    [Fact]
    public void Defaults_are_off_when_settings_are_off()
    {
        var options = ShellComposition.MirrorCaptureDiagnostics(new ShellSettings());

        options.DumpCalibrationBundles.Should().BeFalse();
    }

    [Fact]
    public void Flipping_the_bundle_dump_setting_mirrors_onto_the_singleton()
    {
        var settings = new ShellSettings();
        var options = ShellComposition.MirrorCaptureDiagnostics(settings);

        settings.DumpCalibrationBundles = true;
        options.DumpCalibrationBundles.Should().BeTrue("flipping the setting must mirror live onto the options POCO");

        settings.DumpCalibrationBundles = false;
        options.DumpCalibrationBundles.Should().BeFalse("turning the setting back off must mirror too");
    }
}
