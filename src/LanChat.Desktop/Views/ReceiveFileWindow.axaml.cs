using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using LanChat.Desktop.ViewModels;

namespace LanChat.Desktop.Views;

public partial class ReceiveFileWindow : Window
{
    public bool AllowClose { get; set; }
    public ReceiveFileWindow()
    {
        InitializeComponent();
        Opened += (_, _) => PlaceAtCorner();
        PropertyChanged += (_, args) => { if (args.Property == BoundsProperty && IsVisible) PlaceAtCorner(); };
        Closing += (_, args) =>
        {
            if (AllowClose) return;
            args.Cancel = true;
            if (DataContext is ReceiveOffersViewModel { IsBusy: false } model) model.Defer();
            else Hide();
        };
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || DataContext is not ReceiveOffersViewModel { IsBusy: false } model) return;
            model.Defer(); args.Handled = true;
        };
    }

    private void PlaceAtCorner()
    {
        var area = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        Position = new PixelPoint(area.Right - (int)(Bounds.Width * RenderScaling) - 12,
            area.Bottom - (int)(Bounds.Height * RenderScaling) - 12);
    }
}
