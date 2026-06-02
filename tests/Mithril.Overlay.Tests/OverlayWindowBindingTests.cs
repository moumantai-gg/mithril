using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using FluentAssertions;
using Mithril.Overlay.Internal;
using Xunit;

namespace Mithril.Overlay.Tests;

/// <summary>
/// B1 regression: the status chip's DataContext must be set to a source
/// that implements INotifyPropertyChanged (the OverlayWindowService in
/// production) so {Binding StatusMessage} resolves. The first cut used
/// RelativeSource=AncestorType=Window, which bound to the OverlayWindow
/// class — silently never firing because that class has no StatusMessage.
/// </summary>
public sealed class OverlayWindowBindingTests
{
    private sealed class FakeStatusSource : INotifyPropertyChanged
    {
        private string? _statusMessage;

        public string? StatusMessage
        {
            get => _statusMessage;
            set
            {
                if (string.Equals(_statusMessage, value, StringComparison.Ordinal)) return;
                _statusMessage = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusMessage)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasStatusMessage)));
            }
        }

        public bool HasStatusMessage => !string.IsNullOrEmpty(_statusMessage);

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    [Fact]
    public void Status_chip_TextBlock_updates_when_DataContext_StatusMessage_fires_PropertyChanged()
    {
        RunOnSta(() =>
        {
            var window = new OverlayWindow();
            var source = new FakeStatusSource();
            window.DataContext = source;

            // Realize the visual tree. Show() + Hide() is the minimum that
            // forces template expansion + binding resolution; Measure/Arrange
            // alone leave a Window in a partially-realized state.
            //
            // mithril#996: WindowStyle.None + AllowsTransparency bypass DWM,
            // so WindowState.Minimized isn't fully reliable at Show() time —
            // park the window off-screen too so a brief flash before the
            // minimize state takes effect can't reach the visible desktop.
            window.WindowState = WindowState.Minimized;
            window.ShowInTaskbar = false;
            window.Left = -10000;
            window.Top = -10000;
            window.Show();
            try
            {
                var textBlock = FindTextBlockBoundTo(window, nameof(FakeStatusSource.StatusMessage));
                textBlock.Should().NotBeNull(
                    "the status-chip TextBlock must be bound to StatusMessage — if this fails, "
                    + "the XAML binding regressed back to RelativeSource=AncestorType=Window");

                // Initially empty.
                textBlock!.Text.Should().BeNullOrEmpty();

                source.StatusMessage = "map not calibrated — use Legolas wizard";
                // Pump dispatcher so the binding update fires.
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                textBlock.Text.Should().Be("map not calibrated — use Legolas wizard");

                source.StatusMessage = null;
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                textBlock.Text.Should().BeNullOrEmpty();
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static TextBlock? FindTextBlockBoundTo(DependencyObject root, string propertyPath)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tb)
            {
                var binding = BindingOperations.GetBinding(tb, TextBlock.TextProperty);
                if (binding is not null && string.Equals(binding.Path?.Path, propertyPath, StringComparison.Ordinal))
                    return tb;
            }
            var nested = FindTextBlockBoundTo(child, propertyPath);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (captured is not null) throw captured;
    }
}
