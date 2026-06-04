using System.Threading;
using System.Windows;
using FluentAssertions;
using Mithril.Shared.Wpf;
using Xunit;

namespace Mithril.Shared.Tests.Wpf;

public sealed class WindowCaptureExclusionTests
{
    // STA-fact wrapper: WPF Window construction requires an STA thread; xUnit's
    // default Fact runs MTA. We spin a thread per test to scope the STA cost.
    private static void RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured is not null) throw captured;
    }

    [Fact]
    public void ExcludeFromCapture_BeforeSourceInitialized_DoesNotThrow()
    {
        RunSta(() =>
        {
            var window = new Window();
            // HWND not yet created — helper must hook SourceInitialized.
            var act = () => WindowCaptureExclusion.ExcludeFromCapture(window);
            act.Should().NotThrow();
            window.Close();
        });
    }

    [Fact]
    public void ExcludeFromCapture_AfterHwndCreated_DoesNotThrow()
    {
        RunSta(() =>
        {
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Width = 1,
                Height = 1,
                Left = -10_000, // off-screen
                Top = -10_000,
            };
            window.Show(); // forces SourceInitialized → HWND exists
            var act = () => WindowCaptureExclusion.ExcludeFromCapture(window);
            act.Should().NotThrow();
            window.Close();
        });
    }

    [Fact]
    public void ExcludeFromCapture_NullWindow_Throws()
    {
        Action act = () => WindowCaptureExclusion.ExcludeFromCapture(null!);
        act.Should().Throw<System.ArgumentNullException>();
    }
}
