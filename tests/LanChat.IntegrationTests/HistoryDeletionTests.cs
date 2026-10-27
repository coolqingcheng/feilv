using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LanChat.Desktop.ViewModels;
using LanChat.Desktop.Views;
using LanChat.Infrastructure.Networking;
using LanChat.Infrastructure.Storage;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static void RunHistoryDeletionTests(MainWindow window, MainWindowViewModel model, LanService node, IndexedFileStore store, LanService remote)
    {
        while (model.Incoming.IsOpen) model.Incoming.Defer();
        model.Page = "Files"; model.Inbox.SelectedTab = 1;
        model.Inbox.Search = "可删除记录"; Pump(model.Inbox.SearchTask);
        var view = window.GetVisualDescendants().OfType<FileInboxView>().Single();
        var items = view.FindControl<ItemsControl>("FileItems")!;
        var receiver = remote.ConnectAsync("127.0.0.1", node.Port); Pump(receiver);
        var path = Path.Combine(TestRoot, "可删除记录_已接收.txt"); File.WriteAllText(path, "保留已下载的文件");
        var firstOffer = remote.SendFileAsync(receiver.Result, path, false); Pump(firstOffer); Layout();
        Pump(model.Incoming.AcceptCommand.ExecuteAsync(null)); Layout();
        var first = model.Inbox.Files.Single(x => x.Model.Id == firstOffer.Result.Id);
        var secondPath = Path.Combine(TestRoot, "可删除记录_第二条.txt"); File.WriteAllText(secondPath, "真实下载中的删除状态");
        var secondOffer = remote.SendFileAsync(receiver.Result, secondPath, false); Pump(secondOffer);
        var thirdPath = Path.Combine(TestRoot, "可删除记录_待接收.txt"); File.WriteAllText(thirdPath, "删除待接收的记录");
        var thirdOffer = remote.SendFileAsync(receiver.Result, thirdPath, false); Pump(thirdOffer); Layout();
        foreach (var theme in new[] { "Dark", "Light" })
        {
            Pump(model.Settings.SetThemeCommand.ExecuteAsync(theme)); window.Width = 360; Layout();
            var button = DeleteButton(first);
            var row = items.ContainerFromItem(first)!;
            var position = button.TranslatePoint(default, row)!.Value;
            Check(button.Bounds.Width == 24 && position.X > row.Bounds.Width / 2 && position.Y <= 15 && button.Command == first.DeleteCommand,
                theme + " history row exposes a small SVG delete cross at its upper right");
            Render(window, "history-delete-" + theme.ToLowerInvariant() + ".png");
        }
        using (var locked = new FileStream(store.StoragePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ClickDelete(first);
            Check(model.Inbox.Files.Contains(first) && first.CanDelete && model.Inbox.Feedback.StartsWith("删除记录失败", StringComparison.Ordinal),
                "a failed persistent delete keeps the row and allows retry");
        }
        ClickDelete(first);
        Check(!model.Inbox.Files.Contains(first) && File.Exists(first.Model.LocalPath) && File.Exists(path)
            && model.Incoming.Current?.Id == secondOffer.Result.Id,
            "clicking the cross removes only that record, preserves local files and advances its completed receive prompt");
        Pump(remote.RetryMessageAsync(receiver.Result, firstOffer.Result)); Layout();
        Check(!model.Inbox.Files.Any(x => x.Model.Id == firstOffer.Result.Id), "replaying a deleted offer does not restore the UI row or its notification");
        var second = model.Inbox.Files.Single(x => x.Model.Id == secondOffer.Result.Id);
        var downloading = model.Inbox.ReceiveOfferedFileAsync(second.Model);
        Check(!second.CanDelete && !second.DeleteCommand.CanExecute(null), "the record delete cross is disabled throughout an active download");
        Pump(downloading); Layout();
        var third = model.Inbox.Files.Single(x => x.Model.Id == thirdOffer.Result.Id);
        ClickDelete(third);
        Check(!model.Inbox.Files.Contains(third) && model.Incoming.Current?.Id == secondOffer.Result.Id && !model.Incoming.QueueHint.Contains("等待确认", StringComparison.Ordinal),
            "deleting an unreceived record removes that pending offer from the confirmation queue");
        ClickDelete(second);
        Check(model.Inbox.IsEmpty && !model.Incoming.IsOpen && File.Exists(second.Model.LocalPath),
            "deleting the final matching record shows an empty result and dismisses its completed prompt without deleting its file");
        Pump(model.Inbox.InitializeAsync()); Layout();
        var reopened = new IndexedFileStore(store.DataDirectory);
        var persisted = reopened.LoadFileHistoryAsync("可删除记录"); Pump(persisted);
        Check(model.Inbox.IsEmpty && persisted.Result.Messages.Count == 0, "deleted rows stay absent after history reload and repository reopen");
        window.Width = 420;
        model.Inbox.Search = ""; Pump(model.Inbox.SearchTask); Layout();

        Button DeleteButton(FileHistoryItemViewModel item) => items.ContainerFromItem(item)!.GetVisualDescendants().OfType<Button>().Single(x => x.Classes.Contains("deleteRecord"));
        void ClickDelete(FileHistoryItemViewModel item)
        {
            Layout(); var button = DeleteButton(item);
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            if (item.DeleteCommand.ExecutionTask is { } deleting) Pump(deleting);
            Layout();
        }
        void Layout()
        {
            window.UpdateLayout(); Pump(Task.Delay(30)); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); Pump(Task.Delay(20));
        }
    }
}
