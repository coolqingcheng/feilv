using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LanChat.Desktop.Views;

public partial class ProfileReminderWindow : Window
{
    public ProfileReminderWindow() => InitializeComponent();
    private void LaterClicked(object? sender, RoutedEventArgs e) => Close();
}
