using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace LanChat.Desktop.Views;

public partial class MainWindow : Window
{
    private WindowState _restoreState = WindowState.Normal;
    public bool AllowClose { get; set; }
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, args) => { if (!AllowClose) { args.Cancel = true; Hide(); } };
        PropertyChanged += (_, args) =>
        {
            if (args.Property != WindowStateProperty) return;
            var expanded = WindowState is WindowState.Maximized or WindowState.FullScreen;
            WindowFrame.Classes.Set("expanded", expanded);
            WindowSurface.Classes.Set("expanded", expanded);
            if (WindowState == WindowState.Minimized) Hide(); else _restoreState = WindowState;
        };
        AddHandler(PointerPressedEvent, ResizePointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    public void Restore()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = _restoreState;
        Activate();
    }

    private void TitleBarPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (args.Source is Avalonia.Visual visual && Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Button>(visual) is not null) return;
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (args.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else BeginMoveDrag(args);
    }

    private void ResizePointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (WindowState != WindowState.Normal || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var position = args.GetPosition(this);
        var frame = WindowFrame.Bounds;
        const int grip = 5;
        var left = position.X < frame.Left + grip; var right = position.X > frame.Right - grip;
        var top = position.Y < frame.Top + grip; var bottom = position.Y > frame.Bottom - grip;
        if (!left && !right && !top && !bottom) return;
        var edge = top ? (left ? WindowEdge.NorthWest : right ? WindowEdge.NorthEast : WindowEdge.North)
            : bottom ? (left ? WindowEdge.SouthWest : right ? WindowEdge.SouthEast : WindowEdge.South)
            : left ? WindowEdge.West : WindowEdge.East;
        BeginResizeDrag(edge, args); args.Handled = true;
    }

    private void MinimizeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => WindowState = WindowState.Minimized;
    private void CloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => Hide();
}
