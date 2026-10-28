using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Automation;
using LanChat.Core.Localization;

namespace LanChat.Desktop.Views;

public partial class PeerRemarkWindow : Window
{
    public PeerRemarkWindow() => InitializeComponent();
    public PeerRemarkWindow(string name, string current) : this() { PeerName.Text = name; RemarkInput.Text = current; }
    public PeerRemarkWindow(string path, string current, bool shareName) : this(path, current)
    {
        if (!shareName) return;
        Title = PromptTitle.Text = Localizer.Text("设置共享名称");
        PromptHint.Text = Localizer.Text("其他设备会看到这个共享名称。");
        RemarkInput.Watermark = Localizer.Text("输入共享名称");
        AutomationProperties.SetName(RemarkInput, Localizer.Text("设置共享名称"));
    }
    private void CancelClicked(object? sender, RoutedEventArgs e) => Close(null);
    private void ConfirmClicked(object? sender, RoutedEventArgs e) => Close((RemarkInput.Text ?? "").Trim());
}
