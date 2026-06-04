using System.Windows;
using System.Windows.Input;
using Mithril.Shared.Settings;
using Legolas.Controls;
using Legolas.Domain;

namespace Legolas.Views;

public partial class InventoryOverlayView : Window
{
    public InventoryOverlayView()
    {
        InitializeComponent();
        Mithril.Shared.Wpf.WindowCaptureExclusion.ExcludeFromCapture(this); // #965
    }

    public InventoryOverlayView(LegolasSettings settings, SettingsAutoSaver<LegolasSettings> saver) : this()
    {
        WindowLayoutBinder.Bind(this, settings.InventoryOverlay, saver.Touch);

        // Inventory overlay uses *partial* click-through — the body (inventory
        // grid) becomes click-through to the game, but the header (drag) and a
        // 6 px frame around the window edge (resize) stay interactive. 6 px
        // matches the thickness of the ResizeGrips Thumb tracks.
        PartialClickThrough? partial = null;
        Loaded += (_, _) =>
        {
            partial = PartialClickThrough.Attach(this, resizeBorderThickness: 6, HeaderRegion);
            partial.SetActive(settings.ClickThroughInventory);
        };
        // Re-assert TOPMOST on every show + on a low-frequency timer while
        // visible — Loaded/Activated alone miss the Hide()/Show() cycle the
        // OverlayController drives when the game holds the foreground.
        ClickThrough.KeepTopmost(this);
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LegolasSettings.ClickThroughInventory))
                partial?.SetActive(settings.ClickThroughInventory);
        };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }
}
