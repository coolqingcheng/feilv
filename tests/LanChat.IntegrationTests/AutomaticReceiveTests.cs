using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LanChat.Core.Models;
using LanChat.Core.Services;
using LanChat.Desktop.Services;
using LanChat.Desktop.ViewModels;
using LanChat.Desktop.Views;
using LanChat.Infrastructure.Networking;
using LanChat.Infrastructure.Storage;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static void RunAutomaticReceiveTests()
    {
        var store = new IndexedFileStore(Path.Combine(TestRoot, "automatic-receiver"));
        var node = new LanService(store, new UserProfile { Nickname = "自动接收电脑", StartWithWindows = false }, new NodeOptions { EnableDiscovery = false });
        var senderStore = new IndexedFileStore(Path.Combine(TestRoot, "automatic-sender"));
        var sender = new LanService(senderStore, new UserProfile { Nickname = "文件工作站" }, new NodeOptions { EnableDiscovery = false });
        var controlled = new ControlledReceiver(node);
        var ui = new TestUiServices();
        using var model = new MainWindowViewModel(controlled, store, ui);
        using var senderModel = new MainWindowViewModel(sender, senderStore, ui);
        Pump(model.InitializeAsync()); Pump(sender.StartAsync()); Pump(node.ConnectAsync("127.0.0.1", sender.Port));
        var receiver = sender.ConnectAsync("127.0.0.1", node.Port); Pump(receiver);
        var prompt = new ReceiveFileWindow { DataContext = model.Incoming };
        var window = new MainWindow { DataContext = model }; window.Show(); model.Page = "Settings"; Layout();
        Check(model.Settings.AutoReceive && window.GetVisualDescendants().OfType<SettingsView>().Single().FindControl<CheckBox>("AutoReceiveOption")!.IsChecked == true,
            "the automatic receive setting is checked on first launch");
        using var notifications = new ReceivePromptController(window, prompt, model.Incoming, node.Profile);
        var activations = 0;
        window.Activated += (_, _) => activations++;
        window.Hide(); activations = 0;
        var source = Path.Combine(TestRoot, "自动接收资料.txt"); File.WriteAllText(source, "自动接收真实 TLS 文件，按顺序处理后续请求。");
        try
        {
            var first = sender.SendFileAsync(receiver.Result, source, false); Pump(first); WaitBusy();
            Check(prompt.IsVisible && model.Incoming.IsBusy && !window.IsVisible && model.IsSettings && activations == 0 && controlled.Downloads == 1,
                "a default incoming file starts downloading automatically in the corner without restoring the main window");
            model.Incoming.UpdateTransfer(new(first.Result.Id, first.Result.FileName!, 45, 100, "下载中") { BytesPerSecond = 12.5 * 1024 * 1024 }); Layout();
            var progress = prompt.FindControl<ProgressBar>("ReceiveProgressBar")!;
            Check(progress.IsEffectivelyVisible && progress.Value == 45 && !progress.IsIndeterminate && model.Incoming.SpeedText.Contains("12.5 MB/s", StringComparison.Ordinal)
                && !prompt.FindControl<Button>("AcceptReceiveButton")!.IsEffectivelyVisible,
                "automatic reception shows determinate progress and speed while hiding confirmation actions");
            foreach (var theme in new[] { "Dark", "Light" })
            {
                Pump(model.Settings.SetThemeCommand.ExecuteAsync(theme)); Layout(); Render(prompt, "auto-receive-progress-" + theme.ToLowerInvariant() + ".png");
            }
            var second = sender.SendFileAsync(receiver.Result, source, false); Pump(second); Layout();
            var folder = Path.Combine(TestRoot, "自动接收文件夹"); Directory.CreateDirectory(Path.Combine(folder, "子目录"));
            File.WriteAllText(Path.Combine(folder, "子目录", "说明.txt"), "保留目录结构");
            var third = sender.SendFolderAsync(receiver.Result, folder); Pump(third); Layout();
            Check(controlled.Downloads == 1 && model.Incoming.QueueHint.Contains("2 个", StringComparison.Ordinal),
                "additional automatic file and folder offers queue without starting concurrent downloads");
            controlled.Release(); Pump(model.Incoming.AutomaticReceiveTask); Layout();
            Check(controlled.Downloads == 3 && controlled.MaximumActive == 1 && model.Incoming.IsCompleted && model.Incoming.Current?.Id == third.Result.Id
                && model.Inbox.Files.Count == 3 && model.Inbox.Files.All(x => x.HasLocalFile),
                "automatic reception drains the file/folder queue sequentially without requiring completion dismissal");
            Check(model.Incoming.Progress == 100 && progress.IsEffectivelyVisible && progress.Value == 100
                && prompt.FindControl<Button>("OpenReceivedFolderButton")!.IsEffectivelyVisible && prompt.FindControl<Button>("CloseReceiveButton")!.IsEffectivelyVisible
                && !window.IsVisible && activations == 0,
                "successful automatic reception retains a full progress bar and file-location/close actions in the corner");
            model.Incoming.OpenReceivedFolderCommand.Execute(null);
            Check(File.Exists(Path.Combine(ui.LastRevealed!, "子目录", "说明.txt")), "automatic folder completion reveals the actual recursively received directory");
            Render(prompt, "auto-receive-completed.png");
            model.Incoming.Defer(); Layout();

            model.Settings.ShowNotifications = false; Pump(model.Settings.SaveCommand.ExecuteAsync(null));
            var quiet = sender.SendFileAsync(receiver.Result, source, false); Pump(quiet); Pump(model.Incoming.AutomaticReceiveTask); Layout();
            Check(model.Inbox.Files.Single(x => x.Model.Id == quiet.Result.Id).HasLocalFile && !prompt.IsVisible && !window.IsVisible,
                "notification opt-out still receives automatically while keeping both windows hidden");
            model.Incoming.Defer();
            model.Settings.AutoReceive = false; model.Settings.ShowNotifications = true; Pump(model.Settings.SaveCommand.ExecuteAsync(null));
            var manual = sender.SendFileAsync(receiver.Result, source, false); Pump(manual); Layout();
            Check(!XmlConfiguration.Load(store.DataDirectory).Profile.AutoReceive && !model.Incoming.IsBusy
                && !model.Inbox.Files.Single(x => x.Model.Id == manual.Result.Id).HasLocalFile && prompt.FindControl<Button>("AcceptReceiveButton")!.IsEffectivelyVisible,
                "disabling automatic reception persists and restores explicit confirmation without downloading");
            var rejectButton = prompt.FindControl<Button>("RejectReceiveButton")!;
            Check(rejectButton.IsEffectivelyVisible && rejectButton.Command == model.Incoming.RejectCommand && model.Incoming.AcceptLabel == "接收",
                "manual reception offers explicit receive and reject actions");
            Pump(model.Incoming.RejectCommand.ExecuteAsync(null)); Layout();
            var rejectedRow = model.Inbox.Files.Single(x => x.Model.Id == manual.Result.Id);
            var senderRow = senderModel.Inbox.Files.Single(x => x.Model.Id == manual.Result.Id);
            Check(!model.Incoming.IsOpen && rejectedRow.IsRejected && !rejectedRow.CanAct && senderRow.IsRejected && senderRow.RejectionLabel == "对方已拒绝"
                && senderModel.Inbox.Recipients.Single(x => x.Id == node.Profile.Id).TransferStatus == "对方已拒绝",
                "rejecting from the corner updates both histories and the sender device row without downloading");

            var retryRejection = sender.SendFileAsync(receiver.Result, source, false); Pump(retryRejection); Layout();
            controlled.RejectFailure = true; Pump(model.Incoming.RejectCommand.ExecuteAsync(null)); Layout();
            Check(model.Incoming.HasError && model.Incoming.CanRespond && !senderModel.Inbox.Files.Single(x => x.Model.Id == retryRejection.Result.Id).IsRejected,
                "a failed rejection notification keeps the offer visible and does not claim the sender was notified");
            controlled.RejectFailure = false; Pump(model.Incoming.RejectCommand.ExecuteAsync(null)); Layout();
            Check(!model.Incoming.IsOpen && senderModel.Inbox.Files.Single(x => x.Model.Id == retryRejection.Result.Id).IsRejected,
                "a rejection notification can be retried successfully");
            manual = sender.SendFileAsync(receiver.Result, source, false); Pump(manual); Layout();
            model.Settings.AutoReceive = true; Pump(model.Settings.SaveCommand.ExecuteAsync(null)); Pump(model.Incoming.AutomaticReceiveTask); Layout();
            Check(model.Incoming.IsCompleted && model.Inbox.Files.Single(x => x.Model.Id == manual.Result.Id).HasLocalFile,
                "enabling automatic reception applies immediately to an already pending offer");
            model.Incoming.Defer();

            controlled.Pause();
            var broken = sender.SendFileAsync(receiver.Result, source, false); Pump(broken); WaitBusy();
            File.Delete(broken.Result.LocalPath!); controlled.Release(); Pump(model.Incoming.AutomaticReceiveTask); Layout();
            Check(model.Incoming.HasError && !model.Incoming.IsBusy && !model.Incoming.IsCompleted && model.Incoming.AcceptLabel == "重试接收"
                && prompt.FindControl<Button>("AcceptReceiveButton")!.IsEffectivelyVisible,
                "an automatic download failure stops retrying and exposes an explicit retry action");
            File.Copy(source, broken.Result.LocalPath!);
            Pump(model.Incoming.AcceptCommand.ExecuteAsync(null)); Layout();
            Check(model.Incoming.IsCompleted && !model.Incoming.HasError, "a failed automatic receive can be retried successfully");
            model.Incoming.Defer();
            var empty = Path.Combine(TestRoot, "自动接收空文件.txt"); File.WriteAllBytes(empty, []);
            var emptyOffer = sender.SendFileAsync(receiver.Result, empty, false); Pump(emptyOffer); Pump(model.Incoming.AutomaticReceiveTask); Layout();
            Check(model.Incoming.IsCompleted && model.Incoming.Progress == 100 && new FileInfo(model.Incoming.CompletedPath!).Length == 0,
                "empty files complete automatically with a full progress bar");
            model.Incoming.Defer();

            controlled.Pause();
            var cancelled = sender.SendFileAsync(receiver.Result, source, false); Pump(cancelled); WaitBusy();
            model.Dispose(); Pump(model.Incoming.AutomaticReceiveTask);
            Check(controlled.Active == 0 && !model.Incoming.IsBusy, "disposing the desktop cancels a waiting automatic download and releases its resources");
        }
        finally
        {
            controlled.Release(); notifications.Dispose(); model.Dispose(); prompt.AllowClose = true; prompt.Close(); window.AllowClose = true; window.Close();
            Pump(node.DisposeAsync().AsTask()); Pump(sender.DisposeAsync().AsTask());
        }

        void WaitBusy()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!model.Incoming.IsBusy) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Automatic receive did not start."); Pump(Task.Delay(10)); }
            Layout();
        }
        void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); prompt.UpdateLayout(); Pump(Task.Delay(40)); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    private sealed class ControlledReceiver(ILanService inner) : ILanService
    {
        private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Downloads { get; private set; }
        public int Active { get; private set; }
        public int MaximumActive { get; private set; }
        public bool RejectFailure { get; set; }
        public void Release() => _gate.TrySetResult();
        public void Pause() => _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public UserProfile Profile => inner.Profile;
        public int Port => inner.Port;
        public IReadOnlyCollection<Peer> Peers => inner.Peers;
        public IReadOnlyList<SharedFolder> SharedFolders => inner.SharedFolders;
        public event Action<Peer>? PeerChanged { add => inner.PeerChanged += value; remove => inner.PeerChanged -= value; }
        public event Action<ChatMessage>? MessageReceived { add => inner.MessageReceived += value; remove => inner.MessageReceived -= value; }
        public event Action<TransferUpdate>? TransferChanged { add => inner.TransferChanged += value; remove => inner.TransferChanged -= value; }
        public event Action<string>? Error { add => inner.Error += value; remove => inner.Error -= value; }
        public async Task<string> DownloadAttachmentAsync(Peer peer, ChatMessage message, CancellationToken ct = default)
        {
            Downloads++; Active++; MaximumActive = Math.Max(MaximumActive, Active);
            try { await _gate.Task.WaitAsync(ct); return await inner.DownloadAttachmentAsync(peer, message, ct); }
            finally { Active--; }
        }
        public Task StartAsync(CancellationToken ct = default) => inner.StartAsync(ct);
        public Task<Peer> ConnectAsync(string address, int port, CancellationToken ct = default) => inner.ConnectAsync(address, port, ct);
        public Task<ChatMessage> SendTextAsync(Peer peer, string text, CancellationToken ct = default) => inner.SendTextAsync(peer, text, ct);
        public Task<ChatMessage> SendFileAsync(Peer peer, string path, bool isImage, CancellationToken ct = default) => inner.SendFileAsync(peer, path, isImage, ct);
        public Task<ChatMessage> SendFolderAsync(Peer peer, string path, CancellationToken ct = default) => inner.SendFolderAsync(peer, path, ct);
        public Task RetryMessageAsync(Peer peer, ChatMessage message, CancellationToken ct = default) => inner.RetryMessageAsync(peer, message, ct);
        public Task<IReadOnlyList<SharedEntry>> BrowseAsync(Peer peer, string? rootId = null, string relativePath = "", CancellationToken ct = default) => inner.BrowseAsync(peer, rootId, relativePath, ct);
        public Task<string> DownloadSharedFileAsync(Peer peer, SharedEntry entry, CancellationToken ct = default) => inner.DownloadSharedFileAsync(peer, entry, ct);
        public Task RejectAttachmentAsync(Peer peer, ChatMessage message, CancellationToken ct = default) => RejectFailure ? Task.FromException(new IOException("模拟网络断开")) : inner.RejectAttachmentAsync(peer, message, ct);
        public Task AddSharedFolderAsync(string path, CancellationToken ct = default) => inner.AddSharedFolderAsync(path, ct);
        public Task RemoveSharedFolderAsync(string id, CancellationToken ct = default) => inner.RemoveSharedFolderAsync(id, ct);
        public Task UpdateProfileAsync(CancellationToken ct = default) => inner.UpdateProfileAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
