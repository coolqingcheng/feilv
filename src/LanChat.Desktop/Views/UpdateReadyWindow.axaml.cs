using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LanChat.Desktop.Views;

public partial class UpdateReadyWindow : Window
{
    public UpdateReadyWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            var area = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            Position = new PixelPoint(area.Right - (int)(Width * RenderScaling) - 12, area.Bottom - (int)(Height * RenderScaling) - 12);
        };
    }
    private void LaterClicked(object? sender, RoutedEventArgs e) => Hide();
}
