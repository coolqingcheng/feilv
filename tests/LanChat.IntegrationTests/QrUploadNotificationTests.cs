using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LanChat.Core.Localization;
using LanChat.Core.Models;
using LanChat.Desktop.Services;
using LanChat.Desktop.ViewModels;
using LanChat.Desktop.Views;
using LanChat.Infrastructure.Networking;
using LanChat.Infrastructure.Storage;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static void RunQrUploadNotificationTests()
    {
        var store = new IndexedFileStore(Path.Combine(TestRoot, "qr-notifications"));
        var profile = new UserProfile { AutoReceive = false, StartWithWindows = false };
        var node = new LanService(store, profile, new NodeOptions { EnableDiscovery = false });
        var ui = new TestUiServices();
        using var model = new MainWindowViewModel(node, store, ui);
        Pump(model.Inbox.InitializeAsync());
        var window = new MainWindow { DataContext = model }; window.Show();
        var prompt = new ReceiveFileWindow { DataContext = model.Incoming };
        using var notifications = new ReceivePromptController(window, prompt, model.Incoming, profile);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            Layout();
            var folder = window.FindControl<Button>("OpenDownloadsButton")!;
            var qr = window.FindControl<Button>("QrShareButton")!;
            var point = folder.TranslatePoint(new Point(folder.Bounds.Width / 2, folder.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            var downloads = Path.Combine(store.DataDirectory, "Files");
            Check(Math.Abs(folder.TranslatePoint(new Point(folder.Bounds.Width, 0), window)!.Value.X - qr.TranslatePoint(default, window)!.Value.X) < 1
                && folder.Command == model.Settings.OpenDownloadsCommand, "the folder SVG sits beside QR and uses the existing downloads command");
            Check(ui.LastOpened == downloads && Directory.Exists(downloads) && !model.QrShare.IsRunning && model.IsFiles,
                "clicking the folder shortcut creates and opens ordinary Files without starting sharing or navigating");
            var uploadRoot = Path.Combine(TestRoot, "自定义手机上传 with spaces");
            model.QrShare.UploadDirectory = uploadRoot; Pump(model.QrShare.ApplyDirectoriesCommand.ExecuteAsync(null));
            Pump(model.QrShare.StartAsync());
            folder.Command!.Execute(folder.CommandParameter);
            Check(ui.LastOpened == downloads && downloads != uploadRoot, "the toolbar folder continues to open Files when QR uploads use a custom directory");
            model.Page = "Settings"; window.WindowState = WindowState.Minimized; Layout();
            var position = window.Position; var activations = 0;
            window.Activated += (_, _) => activations++;
            const string name = "手机 资料 &{name}.txt";
            Upload(name, Encoding.UTF8.GetBytes("二维码上传后的文件定位"));
            var first = model.Incoming.Current!;
            var actualPath = Path.Combine(uploadRoot, name);
            Check(prompt.IsVisible && model.Incoming.IsCompleted && model.Incoming.Progress == 100 && !model.Incoming.CanRespond
                && !model.Incoming.ShowDecisionActions && !model.Incoming.SpeedText.Contains("连接", StringComparison.Ordinal),
                "a committed browser upload opens the existing completed-receive prompt with full progress even in manual mode");
            Check(!window.IsVisible && window.WindowState == WindowState.Minimized && model.IsSettings && window.Position == position
                && activations == 0 && !prompt.ShowActivated && !prompt.ShowInTaskbar,
                "QR upload notifications stay in the corner without restoring, activating or navigating the main window");
            Check(first.LocalPath == actualPath && first.PeerId == QrUpload.SourceId && first.PeerAddress == "127.0.0.1" && first.FileSize == new FileInfo(actualPath).Length
                && model.Incoming.Source == "来自二维码上传" && model.Incoming.CompletedPath == actualPath,
                "QR completion displays the real filename, size, source IP and custom destination");
            model.Incoming.OpenReceivedFolderCommand.Execute(null);
            Check(ui.LastRevealed == actualPath && !window.IsVisible && model.Incoming.IsCompleted, "QR completion locates the exact saved file using the same reveal operation as LAN reception");
            ui.RevealFailure = true; model.Incoming.OpenReceivedFolderCommand.Execute(null);
            Check(model.Incoming.HasError && model.Incoming.IsCompleted, "a failed QR file reveal retains the completed prompt for retry or close");
            ui.RevealFailure = false; model.Incoming.OpenReceivedFolderCommand.Execute(null);
            Check(!model.Incoming.HasError, "retrying QR file reveal clears the previous error");
            foreach (var theme in new[] { "Dark", "Light" })
            {
                Pump(model.Settings.SetThemeCommand.ExecuteAsync(theme)); Layout();
                Render(prompt, "qr-upload-completed-" + theme.ToLowerInvariant() + ".png");
                Check(prompt.FindControl<Button>("OpenReceivedFolderButton")!.IsVisible && prompt.FindControl<Button>("CloseReceiveButton")!.IsVisible,
                    theme + " QR completion offers Show in folder and Close");
            }
            Pump(model.Settings.SetLanguageCommand.ExecuteAsync("en-US")); Layout();
            Check(prompt.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "From QR upload")
                && AutomationProperties.GetName(folder) == "Open downloads folder" && model.Incoming.FileName == name,
                "QR receipts and the folder shortcut switch to English while preserving the uploaded filename");
            Pump(model.Settings.SetLanguageCommand.ExecuteAsync("System"));
            var row = model.Inbox.Files.Single(x => x.Model.Id == first.Id);
            Pump(row.ActCommand.ExecuteAsync(null));
            Check(row.HasLocalFile && row.ActionLabel == "打开文件夹" && ui.LastRevealed == actualPath
                && row.Source == "来自二维码上传" && row.Address == "IP 127.0.0.1", "QR uploads participate in searchable history with the normal folder action, size, source and IP");
            Upload(name, [42]);
            Check(model.Incoming.Current!.Id == first.Id && model.Incoming.QueueHint.Contains("1 个已接收", StringComparison.Ordinal),
                "multiple browser uploads queue completion notices without replacing the file being viewed");
            model.Incoming.Defer(); Layout();
            Check(model.Incoming.IsCompleted && model.Incoming.FileName == "手机 资料 &{name} (1).txt"
                && model.Incoming.CompletedPath == Path.Combine(uploadRoot, model.Incoming.FileName), "closing a QR completion advances to the actual renamed file instead of prompting to receive it again");
            model.Incoming.Defer(); Layout(); Check(!prompt.IsVisible, "closing the final QR completion dismisses only the corner window");
            var pending = new ChatMessage { PeerId = Guid.NewGuid().ToString("N"), SenderId = "lan-sender", Kind = MessageKind.File, FileName = "正在接收的内网文件.bin",
                AttachmentId = Guid.NewGuid().ToString("N"), Delivery = DeliveryState.Received };
            model.Incoming.Enqueue(pending); model.Incoming.IsBusy = true;
            Upload("queued.txt", [3]);
            Check(model.Incoming.Current == pending && model.Incoming.IsBusy, "a completed QR upload cannot interrupt an active LAN receive");
            model.Incoming.IsBusy = false; model.Incoming.Defer(); Layout();
            Check(model.Incoming.IsCompleted && model.Incoming.FileName == "queued.txt", "a queued QR receipt becomes a completed prompt when the prior LAN item is dismissed");
            model.Incoming.Defer();
            profile.ShowNotifications = false; profile.AutoReceive = true;
            Upload("quiet-empty.txt", []); Layout();
            Check(model.Incoming.IsCompleted && model.Incoming.Size == "0 B" && !prompt.IsVisible && !window.IsVisible,
                "notification opt-out still stores zero-byte QR uploads and completion metadata quietly");
            model.Incoming.Defer(); profile.ShowNotifications = true; profile.AutoReceive = false;
            var largeBatch = ReceiveOffersViewModel.MaxCompletedPrompts + 5;
            for (var i = 0; i < largeBatch; i++) Upload("batch-" + i + ".txt", []);
            var notices = 0;
            while (model.Incoming.IsOpen) { notices++; model.Incoming.Defer(); }
            Check(notices == ReceiveOffersViewModel.MaxCompletedPrompts, "a browser batch keeps a bounded completion queue");
            var reload = new IndexedFileStore(store.DataDirectory).LoadFileHistoryAsync(pageSize: 200); Pump(reload);
            Check(reload.Result.Messages.Count == largeBatch + 4 && reload.Result.Messages.All(x => x.PeerId == QrUpload.SourceId && File.Exists(x.LocalPath))
                && reload.Result.Messages.Single(x => x.Id == first.Id).LocalPath == actualPath, "all QR files remain in persisted history after completion notices are bounded or closed");
            Pump(row.DeleteCommand.ExecuteAsync(null));
            var deleted = store.FindMessageAsync(first.PeerId, first.Id); Pump(deleted);
            Check(deleted.Result is null && File.Exists(actualPath), "deleting a QR history record preserves its uploaded file");
            var historicalSearch = store.LoadFileHistoryAsync("资料", pageSize: 50); Pump(historicalSearch);
            Check(historicalSearch.Result.Messages.Count == 1 && historicalSearch.Result.Messages[0].FileName == "手机 资料 &{name} (1).txt",
                "QR history supports filename search and deletion across a store reload");
        }
        finally
        {
            Localizer.SetPreference("System");
            Pump(model.QrShare.DisposeAsync().AsTask()); model.Dispose();
            prompt.AllowClose = true; prompt.Close(); window.AllowClose = true; window.Close();
            Pump(node.DisposeAsync().AsTask());
        }

        void Upload(string fileName, byte[] body)
        {
            var uri = new Uri(model.QrShare.Url);
            var task = client.PostAsync($"http://127.0.0.1:{uri.Port}{uri.AbsolutePath}upload?name=" + Uri.EscapeDataString(fileName), new ByteArrayContent(body));
            Pump(task); using var response = task.Result;
            if (response.StatusCode != HttpStatusCode.Created) throw new InvalidOperationException("QR upload failed: " + response.StatusCode);
            Layout();
        }
        void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump(Task.Delay(20)); }
    }
}
