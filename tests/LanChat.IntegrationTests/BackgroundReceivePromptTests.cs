using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using LanChat.Desktop.Services;
using LanChat.Desktop.ViewModels;
using LanChat.Desktop.Views;
using LanChat.Infrastructure.Networking;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static void RunBackgroundReceivePromptTests(MainWindow window, MainWindowViewModel model, LanService node, LanService remote, TestUiServices ui)
    {
        while (model.Incoming.IsOpen) model.Incoming.Defer();
        model.Settings.ShowNotifications = true; Pump(model.Settings.SaveCommand.ExecuteAsync(null));
        Pump(model.Settings.SetThemeCommand.ExecuteAsync("Dark"));
        var prompt = new ReceiveFileWindow { DataContext = model.Incoming };
        using var notifications = new ReceivePromptController(window, prompt, model.Incoming, node.Profile);
        var receiver = remote.ConnectAsync("127.0.0.1", node.Port); Pump(receiver);
        var source = Path.Combine(TestRoot, "后台文件接收 空格.txt"); File.WriteAllText(source, "仅右下角确认，不唤起主窗口");
        var activations = 0;
        void Activated(object? sender, EventArgs args) => activations++;
        window.Activated += Activated;
        try
        {
            model.Page = "Settings";
            window.WindowState = WindowState.Minimized; Layout();
            var position = window.Position;
            var first = remote.SendFileAsync(receiver.Result, source, false); Pump(first); Layout();
            Check(prompt.IsVisible && !window.IsVisible && window.WindowState == WindowState.Minimized && model.IsSettings
                && window.Position == position && activations == 0,
                "incoming file only shows confirmation while the main window stays minimized on its original page");
            Check(!prompt.ShowActivated && !prompt.ShowInTaskbar && prompt.Topmost && model.Incoming.Current?.Id == first.Result.Id,
                "background confirmation requests no focus and has no taskbar window");
            var area = prompt.Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            Check(prompt.Position.X == area.Right - (int)(prompt.Bounds.Width * prompt.RenderScaling) - 12
                && prompt.Position.Y == area.Bottom - (int)(prompt.Bounds.Height * prompt.RenderScaling) - 12,
                "background confirmation stays at the bottom right of the screen working area");
            Render(prompt, "background-receive-dark.png");
            var second = remote.SendFileAsync(receiver.Result, source, false); Pump(second); Layout();
            Check(prompt.IsVisible && model.Incoming.Current?.Id == first.Result.Id && model.Incoming.QueueHint.Contains("1 个", StringComparison.Ordinal)
                && !window.IsVisible && activations == 0,
                "additional background files queue in the same confirmation without activating the main window");

            Pump(model.Incoming.AcceptCommand.ExecuteAsync(null)); Layout();
            Check(prompt.IsVisible && model.Incoming.Current?.Id == first.Result.Id && model.Incoming.IsCompleted
                && model.Inbox.Files.Single(x => x.Model.Id == first.Result.Id).HasLocalFile
                && !window.IsVisible && window.WindowState == WindowState.Minimized && model.IsSettings && activations == 0,
                "accepting from the corner retains a completed-file prompt without restoring the main window");
            var folderButton = prompt.FindControl<Button>("OpenReceivedFolderButton")!;
            var closeButton = prompt.FindControl<Button>("CloseReceiveButton")!;
            Check(folderButton.IsEffectivelyVisible && folderButton.IsEnabled && closeButton.IsEffectivelyVisible
                && !prompt.FindControl<Button>("AcceptReceiveButton")!.IsEffectivelyVisible,
                "completion replaces receive actions with open-folder and close buttons");
            var completedPath = model.Inbox.Files.Single(x => x.Model.Id == first.Result.Id).Model.LocalPath;
            folderButton.Command!.Execute(folderButton.CommandParameter);
            Check(ui.LastRevealed == completedPath && File.Exists(ui.LastRevealed) && prompt.IsVisible && !window.IsVisible && activations == 0,
                "open folder reveals the exact received file, including Unicode and spaces, without activating the main window");
            ui.RevealFailure = true; folderButton.Command.Execute(folderButton.CommandParameter);
            Check(model.Incoming.HasError && model.Incoming.IsCompleted && prompt.IsVisible && !window.IsVisible,
                "file manager launch failure retains completion controls and reports a recoverable error");
            ui.RevealFailure = false; folderButton.Command.Execute(folderButton.CommandParameter);
            Check(!model.Incoming.HasError, "opening the file folder again clears a previous launch error");
            foreach (var theme in new[] { "Dark", "Light" })
            {
                Pump(model.Settings.SetThemeCommand.ExecuteAsync(theme)); Layout();
                Render(prompt, "receive-completed-" + theme.ToLowerInvariant() + ".png");
            }
            closeButton.Command!.Execute(closeButton.CommandParameter); Layout();
            Check(prompt.IsVisible && model.Incoming.Current?.Id == second.Result.Id && !model.Incoming.IsCompleted && !window.IsVisible && activations == 0,
                "closing completion advances to the next waiting file and leaves the main window minimized");
            model.Incoming.Defer(); Layout();
            Check(!prompt.IsVisible && !window.IsVisible && !model.Inbox.Files.Single(x => x.Model.Id == second.Result.Id).HasLocalFile,
                "deferring hides the final confirmation and preserves the unreceived file in history");

            window.Restore(); model.Page = "Shares"; Layout();
            window.Close(); Layout(); // Closing to the tray hides a normal window, rather than minimizing it.
            activations = 0;
            var hidden = remote.SendFileAsync(receiver.Result, source, false); Pump(hidden); Layout();
            Check(prompt.IsVisible && !window.IsVisible && window.WindowState == WindowState.Normal && model.IsShares && activations == 0,
                "a main window closed to the tray also stays hidden when a file arrives");
            Pump(model.Incoming.AcceptCommand.ExecuteAsync(null)); Layout();
            Check(prompt.IsVisible && model.Incoming.IsCompleted && !window.IsVisible && model.IsShares
                && model.Inbox.Files.Single(x => x.Model.Id == hidden.Result.Id).HasLocalFile && activations == 0,
                "a completed background receive stays available for file-location actions while the main window stays hidden");
            closeButton.Command.Execute(closeButton.CommandParameter); Layout();
            Check(!prompt.IsVisible && !window.IsVisible && !model.Incoming.IsOpen,
                "closing the last completion dismisses only the corner prompt");

            model.Settings.ShowNotifications = false; Pump(model.Settings.SaveCommand.ExecuteAsync(null));
            var quiet = remote.SendFileAsync(receiver.Result, source, false); Pump(quiet); Layout();
            Check(!prompt.IsVisible && !window.IsVisible && model.Incoming.Current?.Id == quiet.Result.Id,
                "notification opt-out keeps both windows hidden while preserving the receive offer");
            window.Restore(); Layout();
            Check(window.IsVisible && prompt.IsVisible && model.IsShares,
                "manually reopening the main window reveals the pending confirmation without changing pages");
            model.Incoming.Defer(); Layout();

            model.Settings.ShowNotifications = true; Pump(model.Settings.SaveCommand.ExecuteAsync(null));
            window.Hide(); activations = 0;
            var directory = Path.Combine(TestRoot, "后台文件夹"); Directory.CreateDirectory(Path.Combine(directory, "嵌套"));
            File.WriteAllText(Path.Combine(directory, "嵌套", "文件.txt"), "文件夹也只在角落确认");
            var folderOffer = remote.SendFolderAsync(receiver.Result, directory); Pump(folderOffer); Layout();
            Check(prompt.IsVisible && model.Incoming.Current?.IsFolder == true && model.Incoming.Header.Contains("文件夹", StringComparison.Ordinal)
                && !window.IsVisible && activations == 0, "background folder offer uses the corner confirmation while the main window stays hidden");
            Pump(model.Incoming.AcceptCommand.ExecuteAsync(null)); Layout();
            model.Incoming.OpenReceivedFolderCommand.Execute(null);
            Check(model.Incoming.IsCompleted && Directory.Exists(ui.LastRevealed) && File.Exists(Path.Combine(ui.LastRevealed!, "嵌套", "文件.txt"))
                && !window.IsVisible && activations == 0, "completed folder reception reveals the actual received directory without restoring the main window");
            var folderRow = model.Inbox.Files.Single(x => x.Model.Id == folderOffer.Result.Id);
            Pump(folderRow.ActCommand.ExecuteAsync(null));
            Check(folderRow.ActionLabel == "打开文件夹" && ui.LastRevealed == folderRow.Model.LocalPath && !window.IsVisible,
                "folder history selects the received directory through the same file-location action");
            model.Incoming.Defer(); Layout();
            notifications.Dispose(); window.Hide();
            var afterDispose = remote.SendFileAsync(receiver.Result, source, false); Pump(afterDispose); Layout();
            Check(!prompt.IsVisible && !window.IsVisible && model.Incoming.Current?.Id == afterDispose.Result.Id,
                "disposing the prompt controller detaches its offer and activation subscriptions");
            model.Incoming.Defer();
        }
        finally
        {
            window.Activated -= Activated;
            ui.RevealFailure = false;
            notifications.Dispose(); prompt.AllowClose = true; prompt.Close();
            window.Restore(); model.Page = "Files";
        }

        void Layout()
        {
            prompt.UpdateLayout(); window.UpdateLayout(); Pump(Task.Delay(50));
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(); prompt.UpdateLayout(); window.UpdateLayout(); Pump(Task.Delay(20));
        }
    }
}
