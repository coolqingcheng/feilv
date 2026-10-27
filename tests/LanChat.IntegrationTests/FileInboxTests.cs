using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
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
    private static void RunFileInboxTests(MainWindow window, MainWindowViewModel model, LanService node, IndexedFileStore store, LanService remote, IndexedFileStore remoteStore, TestUiServices ui)
    {
        var peer = model.Inbox.Recipients.Single(x => x.Id == remote.Profile.Id);
        var start = DateTimeOffset.UtcNow.AddDays(-8);
        Pump(SeedAsync());
        model.Page = "Files";
        model.Inbox.SelectedTab = 1;
        Pump(model.Inbox.InitializeAsync());
        var view = window.GetVisualDescendants().OfType<FileInboxView>().Single();
        var items = view.FindControl<ItemsControl>("FileItems")!;
        var scroll = view.FindControl<ScrollViewer>("FileScroll")!;
        Layout();
        Check(window.Width < 600 && model.Inbox.Files.Count == FileInboxViewModel.PageSize && model.Inbox.HasMore, "file home uses a compact window and loads only 50 recent records");
        Check(items.GetRealizedContainers().Count() is > 0 and < 35 && AtBottom(), "file inbox virtualizes its rows and opens at the newest record");
        Check(model.Inbox.Files.All(x => x.Source.Length > 0 && x.Address.StartsWith("IP ", StringComparison.Ordinal) && x.Time.Length == 16 && x.Size.Length > 0), "file rows show filename, size, source computer, IP and full date/time");
        model.Inbox.Search = "文件历史_0000"; Pump(model.Inbox.SearchTask); Layout();
        Check(model.Inbox.Files.Count == 1 && model.Inbox.Files[0].Model.FileSize == 0, "file search finds an unloaded old record and displays its zero-byte size");
        model.Inbox.Search = "不存在的文件"; Pump(model.Inbox.SearchTask); Layout();
        Check(model.Inbox.IsEmpty && items.ItemCount == 0, "file inbox displays an empty search state");
        model.Inbox.Search = "文件历史_0001";
        model.Inbox.Search = "文件历史_0002"; Pump(model.Inbox.SearchTask); Layout();
        Check(model.Inbox.Files.Single().FileName.StartsWith("文件历史_0002", StringComparison.Ordinal), "a superseded search cannot replace newer search results");
        model.Inbox.Search = ""; Pump(model.Inbox.SearchTask); Layout();
        scroll.Offset = new Vector(0, 200); Layout();
        var anchor = VisibleRow();
        var anchorItem = (FileHistoryItemViewModel)anchor.DataContext!;
        var before = anchor.TranslatePoint(default, scroll)!.Value.Y;
        var count = model.Inbox.Files.Count;
        Pump(view.LoadOlderAsync()); Layout();
        var after = items.ContainerFromItem(anchorItem)?.TranslatePoint(default, scroll)?.Y;
        Check(model.Inbox.Files.Count == count + 50 && after.HasValue && Math.Abs(before - after.Value) < 3, "prepending an older file page keeps the same visible row at the same position");
        for (var index = 0; model.Inbox.HasMore && index < 20; index++)
        {
            if (model.Inbox.IsLoading) Pump(Task.Delay(30));
            else Pump(view.LoadOlderAsync());
            Layout();
        }
        Check(!model.Inbox.HasMore && model.Inbox.Files.Count >= 620 && model.Inbox.Files.Select(x => x.Key).Distinct().Count() == model.Inbox.Files.Count, "upward paging reaches the oldest record without duplicates");
        Check(items.GetRealizedContainers().Count() < 35, "hundreds of loaded file records still create only nearby visual rows");
        scroll.ScrollToHome(); Layout();
        var incomingPath = Path.Combine(TestRoot, "收到的设计资料.txt");
        File.WriteAllText(incomingPath, "真实局域网文件列表下载验证");
        var destination = remote.ConnectAsync("127.0.0.1", node.Port); Pump(destination);
        var offer = remote.SendFileAsync(destination.Result, incomingPath, false); Pump(offer); Layout();
        var received = model.Inbox.Files.Single(x => x.Model.Id == offer.Result.Id);
        Check(scroll.Offset.Y < 100 && received.Source == "来自 " + remote.Profile.Nickname && received.Address == "IP 127.0.0.1", "an incoming file appends with authenticated source/IP without interrupting history reading");
        Pump(received.ActCommand.ExecuteAsync(null)); Layout();
        Check(received.ActionLabel == "打开文件夹" && received.Model.LocalPath is { } path && File.ReadAllText(path) == File.ReadAllText(incomingPath), "file inbox receives the real attachment and switches its action to Open Folder");
        Pump(received.ActCommand.ExecuteAsync(null));
        Check(ui.LastRevealed == received.Model.LocalPath && ui.LastOpened is null, "history opens the containing folder and selects the exact received file without launching it");
        Pump(remote.RetryMessageAsync(destination.Result, offer.Result)); Layout();
        Check(model.Inbox.Files.Count(x => x.Model.Id == received.Model.Id) == 1 && File.Exists(received.Model.LocalPath), "replayed file offers do not duplicate the row or lose a downloaded path");

        model.Inbox.BeginSendCommand.Execute(null); Layout();
        Check(model.Inbox.IsPickerOpen && model.Inbox.Recipients.Any(x => x.Id == peer.Id), "sending opens an online-device picker with device names and IPs");
        RenderInbox("file-inbox-picker-light.png");
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None); Layout();
        Check(!model.Inbox.IsPickerOpen, "Escape cancels the file recipient picker");
        var secondStore = new IndexedFileStore(Path.Combine(TestRoot, "inbox-other-recipient"));
        var secondNode = new LanService(secondStore, new UserProfile { Nickname = "另一台电脑" }, new NodeOptions { EnableDiscovery = false });
        Pump(secondNode.StartAsync()); Pump(node.ConnectAsync("127.0.0.1", secondNode.Port)); Layout();
        var other = model.Inbox.Recipients.Single(x => x.Id == secondNode.Profile.Id);
        var sendA = Path.Combine(TestRoot, "文件列表拖拽A.txt"); File.WriteAllText(sendA, "A");
        var sendB = Path.Combine(TestRoot, "文件列表拖拽B.txt"); File.WriteAllText(sendB, "B");
        using var data = new DataTransfer();
        foreach (var file in new[] { sendA, sendB, sendA })
        {
            var storageFile = window.StorageProvider.TryGetFileFromPathAsync(file); Pump(storageFile);
            data.Add(DataTransferItem.CreateFile(storageFile.Result!));
        }
        var dropPoint = view.TranslatePoint(new Point(50, 130), window)!.Value;
        var remoteCount = remoteStore.LoadMessagesAsync().GetAwaiter().GetResult().Count;
        window.DragDrop(dropPoint, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(dropPoint, RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None); Layout();
        Check(model.Inbox.IsPickerOpen && model.Inbox.PickerAction.Contains('3') && remoteStore.LoadMessagesAsync().GetAwaiter().GetResult().Count == remoteCount, "dropping files asks who to send to before any network send");
        model.Inbox.SelectedRecipient = peer;
        Action<ChatMessage> changeRecipient = _ => Dispatcher.UIThread.Post(() => model.Inbox.SelectedRecipient = other);
        remote.MessageReceived += changeRecipient;
        try { Pump(model.Inbox.ConfirmSendCommand.ExecuteAsync(null)); }
        finally { remote.MessageReceived -= changeRecipient; }
        Layout();
        Check(remoteStore.LoadMessagesAsync().GetAwaiter().GetResult().Count == remoteCount + 2 && secondStore.LoadMessagesAsync().GetAwaiter().GetResult().Count == 0,
            "multi-file inbox send deduplicates paths and locks the chosen recipient throughout the batch");
        Check(model.Inbox.Files[^1].FileName == Path.GetFileName(sendB) && model.Inbox.Files[^1].Source == "发给 " + peer.Nickname && AtBottom(), "sent files append at the bottom with recipient metadata and reveal the latest row");
        var sentItem = model.Inbox.Files[^1];
        Pump(sentItem.ActCommand.ExecuteAsync(null));
        Check(sentItem.ActionLabel == "打开文件夹" && ui.LastRevealed == sentItem.Model.LocalPath, "sent history also selects the exact local snapshot in its containing folder");
        model.Inbox.BeginSendCommand.Execute(null); model.Inbox.SelectedRecipient = peer; ui.NextFile = sendA;
        Pump(model.Inbox.ConfirmSendCommand.ExecuteAsync(null)); Layout();
        Check(!model.Inbox.IsPickerOpen && remoteStore.LoadMessagesAsync().GetAwaiter().GetResult().Count == remoteCount + 3, "Choose files sends the native picker selection to the selected computer");
        model.Inbox.BeginSendCommand.Execute(null); model.Inbox.SelectedRecipient = other;
        other.Update(other.Model with { IsOnline = false }); model.Inbox.UpdatePeer(other);
        Check(model.Inbox.SelectedRecipient is null && !model.Inbox.CanConfirm && !model.Inbox.Recipients.Contains(other), "an offline selected device disables sending instead of silently changing recipients");
        model.Inbox.CancelPicker();
        model.Inbox.Search = "文件列表拖拽"; Pump(model.Inbox.SearchTask); Layout();
        Check(model.Inbox.Files.Zip(model.Inbox.Files.Skip(1)).All(pair => (pair.First.Model.ReceivedAt ?? pair.First.Model.Timestamp) <= (pair.Second.Model.ReceivedAt ?? pair.Second.Model.Timestamp)), "search results retain oldest-to-newest ordering");
        model.ShowIncoming(peer, received.Model); Pump(model.Inbox.SearchTask); Layout();
        Check(!model.Inbox.HasSearch && model.IsFiles && AtBottom(), "opening a file notification clears the old search and reveals the latest file records");
        model.Inbox.Search = ""; Pump(model.Inbox.SearchTask); Layout();
        model.Inbox.Feedback = "";
        foreach (var theme in new[] { "Light", "Dark" })
        {
            Pump(model.Settings.SetThemeCommand.ExecuteAsync(theme)); Layout(); RenderInbox("file-inbox-" + theme.ToLowerInvariant() + ".png");
        }
        window.Width = 360; window.Height = 540; Layout();
        Check(items.GetRealizedContainers().All(control => control.Bounds.Width <= scroll.Viewport.Width + 1), "file inbox fits its minimum window width without horizontal scrolling");
        RenderInbox("file-inbox-compact.png");
        window.Width = 420; window.Height = 760; Layout();
        var menuButton = window.FindControl<Button>("MainMenuButton")!;
        var menu = (MenuFlyout)menuButton.Flyout!;
        menu.ShowAt(menuButton); Layout();
        var settingsMenuItem = menu.Items.OfType<MenuItem>().Single(x => Equals(x.Header, "设置"));
        Check(settingsMenuItem.Command is not null && settingsMenuItem.Command.CanExecute(settingsMenuItem.CommandParameter), "the home menu binds its navigation commands in the popup");
        settingsMenuItem.Command!.Execute(settingsMenuItem.CommandParameter); menu.Hide(); Layout();
        Check(window.Width == 420 && model.IsSettings, "the home menu opens standalone settings without expanding the file window");
        model.Page = "Files"; Layout();
        Check(window.Width == 420, "returning to files restores the compact window size");

        other.Update(other.Model with { IsOnline = true }); model.Inbox.UpdatePeer(other);
        model.Inbox.BeginSendCommand.Execute(null);
        foreach (var target in model.Inbox.Recipients) target.IsRecipientSelected = true;
        Layout();
        var recipientList = view.FindControl<ItemsControl>("RecipientList")!;
        Check(recipientList.GetVisualDescendants().OfType<CheckBox>().Count(x => x.IsChecked == true) == 2
            && model.Inbox.CanConfirm && model.Inbox.PickerSelectionHint.Contains("2 台", StringComparison.Ordinal),
            "recipient popup supports multiple independent checked devices");
        RenderInbox("file-inbox-multiple-recipients-dark.png");
        var remoteBefore = remoteStore.LoadMessagesAsync().GetAwaiter().GetResult().Count;
        var secondBefore = secondStore.LoadMessagesAsync().GetAwaiter().GetResult().Count;
        var order = new List<string>();
        void First(ChatMessage _) { lock (order) order.Add(peer.Id); }
        void Second(ChatMessage _) { lock (order) order.Add(other.Id); }
        remote.MessageReceived += First; secondNode.MessageReceived += Second;
        ui.NextFiles = [sendA, sendB];
        try { Pump(model.Inbox.ConfirmSendCommand.ExecuteAsync(null)); }
        finally { remote.MessageReceived -= First; secondNode.MessageReceived -= Second; }
        Check(remoteStore.LoadMessagesAsync().GetAwaiter().GetResult().Count == remoteBefore + 2
            && secondStore.LoadMessagesAsync().GetAwaiter().GetResult().Count == secondBefore + 2
            && order.SequenceEqual(new[] { peer.Id, peer.Id, other.Id, other.Id }),
            "multiple native file selections send requests recipient by recipient in order");
        var folderPath = Path.Combine(TestRoot, "界面发送文件夹"); Directory.CreateDirectory(Path.Combine(folderPath, "nested"));
        File.WriteAllText(Path.Combine(folderPath, "nested", "文档.txt"), "界面选文件夹");
        ui.NextFolders = [folderPath];
        Pump(model.Inbox.SendFolderToDeviceCommand.ExecuteAsync(other)); Layout();
        var folderOffer = secondStore.LoadMessagesAsync().GetAwaiter().GetResult().Last();
        Check(folderOffer.IsFolder && other.TransferStatus == "等待对方接收" && other.TransferName == Path.GetFileName(folderPath),
            "device folder picker creates a recursive offer and displays its waiting state on that row");
        using (var folders = new DataTransfer())
        {
            var storage = window.StorageProvider.TryGetFolderFromPathAsync(folderPath); Pump(storage);
            folders.Add(DataTransferItem.CreateFile(storage.Result!));
            model.Inbox.SelectedTab = 1; Layout();
            window.DragDrop(dropPoint, RawDragEventType.DragEnter, folders, DragDropEffects.Copy, RawInputModifiers.None);
            window.DragDrop(dropPoint, RawDragEventType.Drop, folders, DragDropEffects.Copy, RawInputModifiers.None); Layout();
            Check(model.Inbox.IsPickerOpen && model.Inbox.HasPendingItems, "dragged directories are accepted by the recipient picker");
            model.Inbox.CancelPicker();
        }
        Pump(model.Settings.SetThemeCommand.ExecuteAsync("System")); Layout();
        Check(Application.Current!.RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Default
            && XmlConfiguration.Load(store.DataDirectory).Profile.Theme == "System", "system theme follows Avalonia's platform theme and persists across restart");
        Pump(model.Settings.SetThemeCommand.ExecuteAsync("Dark"));
        Pump(secondNode.DisposeAsync().AsTask());

        async Task SeedAsync()
        {
            for (var index = 0; index < 620; index++)
                await store.SaveMessageAsync(new ChatMessage { PeerId = peer.Id, SenderId = peer.Id, Kind = MessageKind.File,
                    FileName = $"文件历史_{index:D4}" + (index % 11 == 0 ? "_这是一份较长名称的项目归档材料" : "") + ".zip", FileSize = index * 10240L,
                    Timestamp = start.AddSeconds(index), ReceivedAt = start.AddSeconds(index), PeerNickname = "研发工作站", PeerAddress = "192.168.1.32", Delivery = DeliveryState.Received });
        }
        bool AtBottom() => scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y < 60;
        Control VisibleRow() => items.GetRealizedContainers().First(control => control.TranslatePoint(default, scroll)!.Value.Y + control.Bounds.Height > 0);
        void Layout() { window.UpdateLayout(); Pump(Task.Delay(30)); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); Pump(Task.Delay(30)); }
        void RenderInbox(string name)
        {
            Layout();
            var output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "screenshots"); Directory.CreateDirectory(output);
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No file inbox frame");
            frame.Save(Path.Combine(output, name));
            Check(frame.PixelSize.Width is >= 360 and < 600, "render " + name);
        }
    }
}
