using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LanChat.Desktop.Views;

public partial class RemoteSharesWindow : Window
{
    public RemoteSharesWindow() => InitializeComponent();
    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();
}
