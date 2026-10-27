using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LanChat.Desktop.Services;

public sealed class NotificationService
{
    private Window? _popup;
    public void Show(string title, string text, Action clicked)
    {
        _popup?.Close();
        var popup = new Window
        {
            Width = 372,
            Height = 148,
            SystemDecorations = SystemDecorations.None,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            CanResize = false,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Title = "飞驴 · 收到文件"
        };
        var content = new StackPanel { Spacing = 9, Margin = new Thickness(20, 16) };
        content.Children.Add(new TextBlock { Text = "飞驴  ·  " + title, FontWeight = FontWeight.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
        content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxHeight = 40, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 });
        var border = new Border { Child = content, Margin = new Thickness(16), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        border.Bind(Border.BackgroundProperty, popup.GetResourceObservable("PanelBrush"));
        border.Bind(Border.BorderBrushProperty, popup.GetResourceObservable("WindowBorderBrush"));
        border.Bind(Border.BoxShadowProperty, popup.GetResourceObservable("WindowShadow"));
        popup.Content = border;
        popup.PointerPressed += (_, _) => { popup.Close(); clicked(); };
        popup.Opened += (_, _) =>
        {
            var area = popup.Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            var scale = popup.RenderScaling;
            popup.Position = new PixelPoint(area.Right - (int)(popup.Width * scale) - 16, area.Bottom - (int)(popup.Height * scale) - 16);
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        timer.Tick += (_, _) => { timer.Stop(); popup.Close(); };
        popup.Closed += (_, _) => timer.Stop();
        _popup = popup;
        popup.Show(); timer.Start();
    }

    public void Close() => _popup?.Close();
}
