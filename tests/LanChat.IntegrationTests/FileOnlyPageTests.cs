using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LanChat.Core.Models;
using LanChat.Desktop.ViewModels;
using LanChat.Desktop.Views;
using LanChat.Infrastructure.Networking;
using LanChat.Infrastructure.Storage;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static void RunFileOnlyPageTests(MainWindow window, MainWindowViewModel model, LanService node,
        LanService remote, IndexedFileStore remoteStore, TestUiServices ui, string imagePath)
    {
        var menuButton = window.FindControl<Button>("MainMenuButton")!;
        var menu = (MenuFlyout)menuButton.Flyout!;
        Check(menu.Items.OfType<MenuItem>().Select(x => x.CommandParameter).SequenceEqual(new object[] { "Files", "Shares", "Settings" })
            && window.FindControl<Control>("PeerList") is null && window.FindControl<Control>("Composer") is null,
            "file-only shell exposes only files, shares and settings with no conversation list or composer");
        model.Page = "Settings"; Layout();
        model.NavigateCommand.Execute("Chats");
        Check(model.IsSettings && window.Width == 420 && model.PageTitle == "设置", "chat navigation is removed and settings stays in the compact window");
        var settings = window.GetVisualDescendants().OfType<SettingsView>().Single();
        Check(settings.IsEffectivelyVisible && settings.Bounds.Width >= window.ClientSize.Width - 40
            && !window.GetVisualDescendants().OfType<FileInboxView>().Single().IsEffectivelyVisible,
            "settings fills the page without a file list, chat sidebar or other page content");
        Check(settings.GetVisualDescendants().OfType<TextBlock>().All(x => x.Text?.Contains("聊天", StringComparison.Ordinal) != true), "settings labels describe device and file options only");
        var notices = 0;
        void Notified(PeerViewModel peer, ChatMessage message) => notices++;
        model.IncomingNotification += Notified;
        var receiver = remote.ConnectAsync("127.0.0.1", node.Port); Pump(receiver);
        var count = model.Inbox.Files.Count;
        Pump(remote.SendTextAsync(receiver.Result, "旧版客户端文字消息")); Layout();
        Check(model.IsSettings && notices == 0 && model.Inbox.Files.Count == count, "legacy text packets never open a chat, change pages or produce a file notification");
        var picture = remote.SendFileAsync(receiver.Result, imagePath, true); Pump(picture); Layout();
        var pictureRow = model.Inbox.Files.Single(x => x.Model.Id == picture.Result.Id);
        Check(notices == 1 && pictureRow.Model.LocalPath is null && pictureRow.ActionLabel == "接收" && model.IsSettings,
            "images arrive as file offers for manual receipt, without automatic chat downloads");
        Pump(pictureRow.ActCommand.ExecuteAsync(null)); Layout();
        Check(notices == 1 && pictureRow.ActionLabel == "打开文件夹", "completing a file download does not produce a second incoming-file notification");
        Check(model.Inbox.TransferSummary.StartsWith("接收完成", StringComparison.Ordinal) && model.Inbox.TransferSummary.Contains("/s", StringComparison.Ordinal),
            "receiver UI displays measured file transfer speed and completion");
        model.IncomingNotification -= Notified;
        model.Settings.ManualAddress = "127.0.0.1"; model.Settings.ManualPort = remote.Port.ToString();
        Pump(model.Settings.ConnectCommand.ExecuteAsync(null)); Layout();
        Check(model.IsSettings && model.Inbox.Recipients.Any(x => x.Id == remote.Profile.Id), "manual connection stays in settings and makes the device available for file sending");
        model.Settings.ManualPort = "0"; Pump(model.Settings.ConnectCommand.ExecuteAsync(null));
        Check(model.Status.Contains("端口", StringComparison.Ordinal) && model.IsSettings, "settings validates manual connection input without leaving the page");
        model.Settings.ManualPort = remote.Port.ToString();
        var shared = Path.Combine(TestRoot, "file-only-shared"); Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "共享文件.txt"), "共享页面的文件内容");
        Pump(remote.AddSharedFolderAsync(shared));
        model.Page = "Shares";
        model.Shares.Peer = model.Shares.Devices.Single(x => x.Id == remote.Profile.Id);
        Pump(model.Shares.RefreshCommand.ExecuteAsync(null)); Layout();
        Check(model.IsShares && window.Width == 420 && model.Shares.RemoteEntries.Any(x => x.Name == Path.GetFileName(shared)),
            "shared folders use their own device picker and browse without conversation navigation");
        var root = model.Shares.RemoteEntries.Single(x => x.Name == Path.GetFileName(shared));
        Pump(model.Shares.OpenEntryCommand.ExecuteAsync(root)); Layout();
        var entry = model.Shares.RemoteEntries.Single(x => x.Name == "共享文件.txt");
        Pump(model.Shares.OpenEntryCommand.ExecuteAsync(entry)); Layout();
        Check(model.Inbox.Files.Any(x => x.FileName == "共享文件.txt" && x.HasLocalFile) && model.IsShares,
            "downloading from standalone shares adds the file to history and stays on the shares page");
        ui.NextFolder = shared; Pump(model.Shares.AddFolderCommand.ExecuteAsync(null));
        Check(model.Shares.LocalFolders.Single().Name == Path.GetFileName(shared), "standalone shares can add a local folder");
        var localFolder = model.Shares.LocalFolders.Single();
        Pump(model.Shares.RemoveFolderCommand.ExecuteAsync(localFolder));
        Check(model.Shares.LocalFolders.Count == 0, "standalone shares can stop sharing a local folder");
        foreach (var theme in new[] { "Dark", "Light" })
        {
            Pump(model.Settings.SetThemeCommand.ExecuteAsync(theme));
            foreach (var width in new[] { 420, 360 })
            {
                window.Width = width;
                model.Page = "Settings"; Layout();
                var settingsScroll = settings.FindControl<ScrollViewer>("SettingsScroll")!;
                settingsScroll.ScrollToHome(); Layout();
                Check(settingsScroll.Extent.Width <= settingsScroll.Viewport.Width + 1, theme + " settings fits " + width + " px without horizontal overflow");
                Render(window, $"file-only-settings-{theme.ToLowerInvariant()}-{width}.png");
                settingsScroll.ScrollToEnd(); Layout();
                Render(window, $"file-only-settings-storage-{theme.ToLowerInvariant()}-{width}.png");
                model.Page = "Shares"; Layout();
                var sharesScroll = window.GetVisualDescendants().OfType<SharesView>().Single().FindControl<ScrollViewer>("SharesScroll")!;
                sharesScroll.ScrollToEnd(); Layout();
                Check(sharesScroll.Extent.Width <= sharesScroll.Viewport.Width + 1, theme + " shares fits " + width + " px without horizontal overflow");
                Render(window, $"file-only-shares-{theme.ToLowerInvariant()}-{width}.png");
            }
        }
        window.Width = 420; model.Page = "Settings"; Layout();
        var back = window.FindControl<Button>("BackToFilesButton")!;
        back.Command!.Execute(back.CommandParameter); Layout();
        Check(model.IsFiles && window.Width == 420 && remoteStore.LoadMessagesAsync().GetAwaiter().GetResult().Count > 0, "back returns directly to files and preserves transfer state");
        void Layout() { window.UpdateLayout(); Pump(Task.Delay(40)); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); Pump(Task.Delay(20)); }
    }
}
