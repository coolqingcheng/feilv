using Avalonia.Controls;
using Avalonia.Threading;
using LanChat.Core.Models;
using LanChat.Desktop.ViewModels;
using LanChat.Desktop.Views;
using LanChat.Infrastructure.Networking;
using LanChat.Infrastructure.Storage;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static void RunDeviceRemarksAndSharesTests(MainWindow window, MainWindowViewModel model, LanService node,
        IndexedFileStore store, LanService remote, IndexedFileStore remoteStore, TestUiServices ui)
    {
        var connect = node.ConnectAsync("127.0.0.1", remote.Port); Pump(connect);
        Dispatcher.UIThread.RunJobs();
        var peer = model.Inbox.Recipients.First(x => x.Id == remote.Profile.Id);
        var originalName = peer.Nickname;
        ui.NextRemark = "同事";
        Pump(model.EditRemarkCommand.ExecuteAsync(peer));
        Check(peer.DisplayName == originalName + "(同事)" && peer.Model.Nickname == originalName, "device remark changes the local display without changing the peer identity");
        Check(XmlConfiguration.Load(store.DataDirectory).PeerRemarks.GetValueOrDefault(peer.Id) == "同事"
            && XmlConfiguration.Load(remoteStore.DataDirectory).PeerRemarks.Count == 0, "device remark persists only in the local XML configuration");
        model.Inbox.DeviceSearch = "同事";
        Check(model.Inbox.VisibleDevices.Contains(peer), "online device search matches the local remark");
        model.Inbox.DeviceSearch = "";
        var history = new FileHistoryItemViewModel(new ChatMessage { PeerId = peer.Id, SenderId = peer.Id, Kind = MessageKind.File, PeerNickname = originalName }, node.Profile.Id, model.Inbox);
        Check(history.Source.Contains("(同事)", StringComparison.Ordinal), "file history displays the local remark beside the recorded nickname");
        ui.NextRemark = ""; Pump(model.EditRemarkCommand.ExecuteAsync(peer));
        Check(peer.DisplayName == originalName && !XmlConfiguration.Load(store.DataDirectory).PeerRemarks.ContainsKey(peer.Id), "an empty remark clears the local alias");

        var shared = Path.Combine(TestRoot, "named-share"); Directory.CreateDirectory(shared);
        ui.NextFolder = shared; ui.NextShareName = "文档";
        Pump(model.Shares.AddFolderCommand.ExecuteAsync(null));
        var folder = model.Shares.LocalFolders.Single(x => x.LocalPath == shared);
        Check(folder.Name == "文档" && XmlConfiguration.Load(store.DataDirectory).Shares.Any(x => x.Id == folder.Id && x.Name == "文档"), "named local share persists its display name and actual path");
        var remotePeer = remote.ConnectAsync("127.0.0.1", node.Port); Pump(remotePeer);
        var roots = remote.BrowseAsync(remotePeer.Result); Pump(roots);
        Check(roots.Result.Any(x => x.RootId == folder.Id && x.Name == "文档"), "other devices see the chosen share name");
        ui.NextShareName = "项目"; Pump(model.Shares.RenameFolderCommand.ExecuteAsync(folder));
        roots = remote.BrowseAsync(remotePeer.Result); Pump(roots);
        Check(roots.Result.Any(x => x.RootId == folder.Id && x.Name == "项目") && model.Shares.LocalFolders.Single(x => x.Id == folder.Id).LocalPath == shared,
            "renaming a share preserves its ID and directory while updating remote browsing");

        var remoteFolder = Path.Combine(TestRoot, "remote-popup-share"); Directory.CreateDirectory(remoteFolder);
        File.WriteAllText(Path.Combine(remoteFolder, "shared.txt"), "shared window content");
        Pump(remote.AddSharedFolderAsync(remoteFolder, displayName: "图片"));
        PeerViewModel? requested = null; model.BrowsePeerSharesRequested += value => requested = value;
        model.BrowsePeerSharesCommand.Execute(peer);
        Check(requested == peer, "device share button targets the selected online computer");
        using var shareModel = new SharesViewModel(node, ui, _ => { });
        var shareWindow = new RemoteSharesWindow { DataContext = shareModel };
        shareWindow.Show(window); shareModel.Peer = peer;
        Pump(shareModel.RefreshCommand.ExecuteAsync(null));
        var root = shareModel.RemoteEntries.Single(x => x.Name == "图片");
        Pump(shareModel.OpenEntryCommand.ExecuteAsync(root));
        var file = shareModel.RemoteEntries.Single(x => x.Name == "shared.txt");
        var destination = Path.Combine(TestRoot, "share-window-save"); Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "shared.txt"), "existing file");
        Pump(shareModel.OpenEntryCommand.ExecuteAsync(file));
        Check(!File.Exists(Path.Combine(destination, "shared (1).txt")) && !shareModel.IsBusy, "cancelling the destination picker does not start a download");
        ui.NextFolder = destination;
        Pump(shareModel.OpenEntryCommand.ExecuteAsync(file));
        Check(File.ReadAllText(Path.Combine(destination, "shared.txt")) == "existing file"
            && File.ReadAllText(Path.Combine(destination, "shared (1).txt")) == "shared window content"
            && shareModel.Feedback.EndsWith(Path.Combine(destination, "shared (1).txt"), StringComparison.Ordinal),
            "device share window downloads into the chosen folder without overwriting an existing file");
        var savedHistory = store.LoadFileHistoryAsync(); Pump(savedHistory);
        Check(savedHistory.Result.Messages.Any(x => x.LocalPath == Path.Combine(destination, "shared (1).txt")), "shared download history points to the chosen file");
        Render(shareWindow, "device-share-window.png");
        shareWindow.Close();
        Check(model.WindowTitle.Contains(AppReleases.CurrentVersion, StringComparison.Ordinal)
            && model.Settings.SourceAddress == AppReleases.HomeUrl && model.Settings.BuildFramework.StartsWith(".NET", StringComparison.Ordinal),
            "application title and about settings expose version, repository and .NET framework");
    }
}
