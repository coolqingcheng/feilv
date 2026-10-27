using Avalonia;
using Avalonia.Controls;

namespace LanChat.Desktop.Views;

public partial class UpdateProgressWindow : Window
{
    public UpdateProgressWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            var area = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            Position = new PixelPoint(area.Right - (int)(Width * RenderScaling) - 12, area.Bottom - (int)(Height * RenderScaling) - 12);
        };
    }
}
