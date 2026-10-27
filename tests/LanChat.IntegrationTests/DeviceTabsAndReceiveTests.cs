using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
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
    private static void RunDeviceTabsAndReceiveTests(MainWindow window, MainWindowViewModel model, LanService node,
        IndexedFileStore store, LanService remote, IndexedFileStore remoteStore, TestUiServices ui)
    {
        model.Page = "Files"; model.Inbox.SelectedTab = 0;
        var view = window.GetVisualDescendants().OfType<FileInboxView>().Single();
        var tabs = view.FindControl<TabControl>("HomeTabs")!;
        var devices = view.FindControl<ItemsControl>("OnlineDeviceList")!;
        var peer = model.Inbox.Recipients.Single(x => x.Id == remote.Profile.Id);
        Layout();
        Check(peer.VersionLabel == "v" + AppReleases.CurrentVersion && devices.GetVisualDescendants().OfType<TextBlock>().Any(x => x.DataContext == peer && x.Text == peer.VersionLabel),
            "online device rows visibly show the version learned from the remote client");
        var currentPeer = peer.Model;
        peer.Update(currentPeer with { AppVersion = "" }); Layout();
        Check(devices.GetVisualDescendants().OfType<TextBlock>().Any(x => x.DataContext == peer && x.Text == "版本未知"), "legacy version fallback refreshes in an existing device row");
        peer.Update(currentPeer); Layout();
        Check(tabs.Items.OfType<TabItem>().Select(x => x.Header).SequenceEqual(new object[] { "在线设备", "收发记录" })
            && devices.IsEffectivelyVisible && view.FindControl<TextBox>("FileSearch")!.GetVisualRoot() is null,
            "home separates online devices from transfer history in two native tabs");
        ClickTab(1);
        model.Inbox.Search = "文件历史_0002"; Pump(model.Inbox.SearchTask); Layout();
        ClickTab(0); ClickTab(1);
        Check(model.Inbox.IsHistory && model.Inbox.Search == "文件历史_0002" && model.Inbox.Files.Count == 1,
            "switching tabs preserves history search without mixing devices and files");
        model.Inbox.Search = ""; Pump(model.Inbox.SearchTask); ClickTab(0);
        var otherStore = new IndexedFileStore(Path.Combine(TestRoot, "direct-device-recipient"));
        var otherNode = new LanService(otherStore, new UserProfile { Nickname = "工程电脑 · 文件接收工作站" }, new NodeOptions { EnableDiscovery = false });
        Pump(otherNode.StartAsync()); Pump(node.ConnectAsync("127.0.0.1", otherNode.Port)); Layout();
        var other = model.Inbox.Recipients.Single(x => x.Id == otherNode.Profile.Id);
        var sendA = Path.Combine(TestRoot, "直接拖给设备A.txt"); File.WriteAllText(sendA, "拖给指定的电脑 A");
        var sendB = Path.Combine(TestRoot, "直接拖给设备B.txt"); File.WriteAllText(sendB, "拖给指定的电脑 B");
        using var data = new DataTransfer();
        foreach (var path in new[] { sendA, sendB, sendA })
        {
            var storage = window.StorageProvider.TryGetFileFromPathAsync(path); Pump(storage);
            data.Add(DataTransferItem.CreateFile(storage.Result!));
        }
        var row = devices.ContainerFromItem(other)!.GetVisualDescendants().OfType<Border>().Single(x => x.Classes.Contains("deviceRow"));
        Click(window, row); Layout();
        Check(!row.Classes.Contains("selected") && !row.GetVisualAncestors().OfType<ListBoxItem>().Any(), "clicking a device row does not create a persistent selection");
        var point = row.TranslatePoint(new Point(70, row.Bounds.Height / 2), window)!.Value;
        window.DragDrop(point, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None); Layout();
        Check(row.Classes.Contains("drop-target") && !view.FindControl<Border>("DropOverlay")!.IsVisible,
            "dragging over a device highlights that row while keeping the device list visible");
        Render(window, "devices-drop-target.png");
        var originalCount = remoteStore.LoadMessagesAsync().GetAwaiter().GetResult().Count;
        Action<ChatMessage> changeSelected = _ => Dispatcher.UIThread.Post(() => model.Inbox.SelectedRecipient = peer);
        otherNode.MessageReceived += changeSelected;
        window.DragDrop(point, RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
        Pump(WaitForSendAsync()); Layout();
        otherNode.MessageReceived -= changeSelected;
        Check(!model.Inbox.IsPickerOpen && !row.Classes.Contains("drop-target") && otherStore.LoadMessagesAsync().GetAwaiter().GetResult().Count == 2
            && remoteStore.LoadMessagesAsync().GetAwaiter().GetResult().Count == originalCount,
            "dropping on a device immediately sends the deduplicated batch to that exact device without a recipient popup");
        var sendButton = row.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "发送"));
        ui.NextFile = sendA;
        Click(window, sendButton);
        var menu = (MenuFlyout)sendButton.Flyout!;
        var chooseFiles = menu.Items.OfType<MenuItem>().Single(x => Equals(x.Header, "选择文件"));
        Check(chooseFiles.Command == model.Inbox.SendToDeviceCommand && chooseFiles.CommandParameter == other, "device send menu binds file and folder actions to the correct row");
        chooseFiles.Command!.Execute(chooseFiles.CommandParameter); menu.Hide();
        if (model.Inbox.SendToDeviceCommand.ExecutionTask is { } choosing) Pump(choosing);
        Layout();
        Check(otherStore.LoadMessagesAsync().GetAwaiter().GetResult().Count == 3 && !model.Inbox.IsPickerOpen,
            "a device's Send button opens the file picker and sends directly to that device");
        var directOffer = otherStore.LoadMessagesAsync().GetAwaiter().GetResult().Last();
        var sendingPeer = otherNode.ConnectAsync("127.0.0.1", node.Port); Pump(sendingPeer);
        Pump(otherNode.DownloadAttachmentAsync(sendingPeer.Result, directOffer)); Layout();
        Check(other.TransferStatus == "发送完成" && other.TransferSpeed.Contains("/s", StringComparison.Ordinal) && other.HasTransfer && !model.Inbox.ShowTransferSummary,
            "sender UI shows actual upload speed when the other device accepts the file");
        other.Update(other.Model with { IsOnline = false }); model.Inbox.UpdatePeer(other);
        Pump(model.Inbox.SendDroppedFilesToDeviceAsync(other, [sendA]));
        Check(otherStore.LoadMessagesAsync().GetAwaiter().GetResult().Count == 3 && !model.Inbox.Recipients.Contains(other),
            "an offline device cannot receive a direct drop and disappears from the online list");
        other.Update(other.Model with { IsOnline = true }); model.Inbox.UpdatePeer(other);

        while (model.Incoming.IsOpen) model.Incoming.Defer();
        var receiver = remote.ConnectAsync("127.0.0.1", node.Port); Pump(receiver);
        var receiveA = Path.Combine(TestRoot, "等待确认_项目资料.txt"); File.WriteAllText(receiveA, "尚未点击接收");
        var receiveB = Path.Combine(TestRoot, "确认接收_设计文件.bin"); File.WriteAllBytes(receiveB, System.Security.Cryptography.RandomNumberGenerator.GetBytes(2 * 1024 * 1024));
        var offerA = remote.SendFileAsync(receiver.Result, receiveA, false); Pump(offerA);
        var offerB = remote.SendFileAsync(receiver.Result, receiveB, false); Pump(offerB); Layout();
        Check(model.Incoming.Current?.Id == offerA.Result.Id && model.Incoming.QueueHint.Contains("1 个", StringComparison.Ordinal)
            && model.Inbox.Files.Single(x => x.Model.Id == offerB.Result.Id).Model.LocalPath is null,
            "incoming offers queue for confirmation and no file is downloaded before acceptance");
        model.Inbox.SelectedTab = 1; Layout();
        Render(window, "pending-file-actions.png");
        var prompt = new ReceiveFileWindow { DataContext = model.Incoming }; prompt.Show(); Layout(prompt);
        Check(prompt.FindControl<Button>("AcceptReceiveButton")!.IsEnabled && model.Incoming.Source == "来自 " + remote.Profile.Nickname
            && model.Incoming.Address == "IP 127.0.0.1" && model.Incoming.Size.EndsWith(" B", StringComparison.Ordinal),
            "receive confirmation displays the real sender, IP, filename, size and explicit accept/defer actions");
        foreach (var theme in new[] { "Dark", "Light" })
        {
            Pump(model.Settings.SetThemeCommand.ExecuteAsync(theme)); Layout(prompt); Render(prompt, "receive-confirm-" + theme.ToLowerInvariant() + ".png");
        }
        Click(prompt, prompt.FindControl<Button>("DeferReceiveButton")!); Layout(prompt);
        Check(model.Incoming.Current?.Id == offerB.Result.Id && model.Inbox.Files.Single(x => x.Model.Id == offerA.Result.Id).Model.LocalPath is null,
            "deferring leaves the first file undownloaded and advances to the next queued offer");
        var downloading = model.Incoming.Current!;
        var connections = 0;
        void CountTransfer(TransferUpdate update) { if (update.Id == downloading.Id && update.Status == "连接中") connections++; }
        node.TransferChanged += CountTransfer;
        Click(prompt, prompt.FindControl<Button>("AcceptReceiveButton")!);
        var duplicateAccept = model.Inbox.ReceiveOfferedFileAsync(downloading);
        Pump(Task.WhenAll(model.Incoming.AcceptCommand.ExecutionTask!, duplicateAccept)); Layout();
        node.TransferChanged -= CountTransfer;
        var accepted = model.Inbox.Files.Single(x => x.Model.Id == offerB.Result.Id);
        Check(model.Incoming.IsCompleted && model.Incoming.CompletedPath == accepted.Model.LocalPath && accepted.ActionLabel == "打开文件夹"
            && File.ReadAllBytes(accepted.Model.LocalPath!).SequenceEqual(File.ReadAllBytes(receiveB)),
            "accepting the prompt downloads and verifies the file and retains its completed path");
        Check(connections == 1, "simultaneous prompt and history acceptance share a single download");
        Click(prompt, prompt.FindControl<Button>("CloseReceiveButton")!); Layout();
        var deferred = model.Inbox.Files.Single(x => x.Model.Id == offerA.Result.Id);
        Pump(deferred.ActCommand.ExecuteAsync(null));
        Check(deferred.HasLocalFile && !model.Incoming.IsOpen, "a deferred file remains receivable from transfer history");
        var broken = remote.SendFileAsync(receiver.Result, receiveA, false); Pump(broken); Layout();
        File.Delete(broken.Result.LocalPath!);
        Pump(model.Incoming.AcceptCommand.ExecuteAsync(null)); Layout(prompt);
        Check(model.Incoming.Current?.Id == broken.Result.Id && model.Incoming.HasError && model.Incoming.CanRespond,
            "failed receipt keeps the offer visible, reports the error and allows retry without accepting another file");
        File.Copy(receiveA, broken.Result.LocalPath!);
        Pump(model.Incoming.AcceptCommand.ExecuteAsync(null)); Layout();
        Check(model.Incoming.IsCompleted && !model.Incoming.HasError, "retry after a failed receive completes and clears the confirmation error");
        model.Incoming.Defer();
        var emptyPath = Path.Combine(TestRoot, "确认接收空文件.txt"); File.WriteAllBytes(emptyPath, []);
        var emptyOffer = remote.SendFileAsync(receiver.Result, emptyPath, false); Pump(emptyOffer); Layout();
        Pump(model.Incoming.AcceptCommand.ExecuteAsync(null)); Layout();
        Check(new FileInfo(model.Inbox.Files.Single(x => x.Model.Id == emptyOffer.Result.Id).Model.LocalPath!).Length == 0 && model.Incoming.IsCompleted,
            "receive confirmation also handles zero-byte files");
        model.Incoming.Defer();
        prompt.AllowClose = true; prompt.Close();

        foreach (var theme in new[] { "Dark", "Light" })
        {
            Pump(model.Settings.SetThemeCommand.ExecuteAsync(theme));
            foreach (var width in new[] { 420, 360 })
            {
                window.Width = width; model.Inbox.Feedback = ""; ClickTab(0);
                Check(devices.GetVisualDescendants().OfType<ScrollViewer>().All(x => x.Extent.Width <= x.Viewport.Width + 1), theme + " device rows fit " + width + " px without horizontal overflow");
                Render(window, $"online-devices-{theme.ToLowerInvariant()}-{width}.png");
                model.Inbox.RevealLatest(); Layout();
                Render(window, $"transfer-records-{theme.ToLowerInvariant()}-{width}.png");
            }
        }
        var foreground = BrushColor("TextBrush"); var muted = BrushColor("MutedBrush"); var panel = BrushColor("PanelBrush"); var card = BrushColor("CardBrush");
        Check(Contrast(foreground, panel) >= 7 && Contrast(muted, panel) >= 4.5 && Contrast(muted, card) >= 4.5,
            "main and secondary dark text retain readable contrast on the reference-style surfaces");
        window.Width = 420; model.Inbox.SelectedTab = 0; Layout();
        Pump(otherNode.DisposeAsync().AsTask());

        void ClickTab(int index) { Click(window, tabs.Items.OfType<TabItem>().ElementAt(index)); Layout(); }
        void Click(Window host, Control control)
        {
            var location = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), host)!.Value;
            host.MouseDown(location, MouseButton.Left); host.MouseUp(location, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        }
        async Task WaitForSendAsync() { while (model.Inbox.IsSending) await Task.Delay(15); }
        void Layout(Window? target = null) { (target ?? window).UpdateLayout(); Pump(Task.Delay(40)); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); (target ?? window).UpdateLayout(); Pump(Task.Delay(20)); }
        Color BrushColor(string key)
        {
            if (!Application.Current!.TryFindResource(key, ThemeVariant.Dark, out var resource) || resource is not ISolidColorBrush brush) throw new InvalidOperationException("Missing theme brush: " + key);
            return brush.Color;
        }
        static double Contrast(Color first, Color second)
        {
            static double Linear(byte component) { var value = component / 255.0; return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4); }
            static double Luminance(Color color) => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
            var a = Luminance(first); var b = Luminance(second); return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }
    }
}
