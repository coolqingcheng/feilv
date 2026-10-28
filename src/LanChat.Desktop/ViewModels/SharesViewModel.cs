using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanChat.Core.Models;
using LanChat.Core.Localization;
using LanChat.Core.Services;
using LanChat.Desktop.Services;

namespace LanChat.Desktop.ViewModels;

public sealed class SharedEntryViewModel(SharedEntry model) : ViewModelBase
{
    public SharedEntry Model { get; } = model;
    public string Name => Model.Name;
    public string Icon => Model.IsDirectory ? "folder" : "file";
    public string Description => Model.IsDirectory ? "文件夹 · 点击浏览" : ImageLoader.FileSize(Model.Size);
    public string Action => Model.IsDirectory ? "打开" : "下载";
}

public partial class SharesViewModel(ILanService lan, IUiServices ui, Action<string> status) : ViewModelBase, IDisposable
{
    private CancellationTokenSource? _downloadCancellation;
    private readonly CancellationTokenSource _stop = new();
    private bool _disposed;
    private readonly Stack<(string? RootId, string Path, string Label)> _navigation = new();
    private string? _rootId;
    private string _path = "";
    private int _requestVersion;
    [ObservableProperty] private PeerViewModel? _peer;
    [ObservableProperty] private string _location = "所有共享文件夹";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private string _feedback = "";
    public ObservableCollection<SharedFolder> LocalFolders { get; } = [];
    public ObservableCollection<PeerViewModel> Devices { get; } = [];
    public ObservableCollection<SharedEntryViewModel> RemoteEntries { get; } = [];
    public bool CanGoUp => _navigation.Count > 0;
    public bool IsEmpty => RemoteEntries.Count == 0;
    public bool HasPeer => Peer is not null;
    public string LocationLabel => _rootId is null ? Localizer.Text("所有共享文件夹") : Location;
    partial void OnLocationChanged(string value) => OnPropertyChanged(nameof(LocationLabel));
    private void Report(string text) { Feedback = text; status(text); }

    public void UpdatePeer(PeerViewModel peer)
    {
        if (peer.IsOnline) { if (!Devices.Contains(peer)) Devices.Add(peer); }
        else { Devices.Remove(peer); if (Peer == peer) Peer = null; }
    }

    public void RefreshLocal()
    {
        LocalFolders.Clear();
        foreach (var folder in lan.SharedFolders) LocalFolders.Add(folder);
    }

    partial void OnPeerChanged(PeerViewModel? value)
    {
        _requestVersion++;
        _rootId = null; _path = ""; _navigation.Clear();
        Location = "所有共享文件夹";
        RemoteEntries.Clear();
        OnPropertyChanged(nameof(HasPeer));
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(IsEmpty));
        if (value is not null) _ = ReadEntriesAsync();
        else IsBusy = false;
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var path = await ui.PickFolderAsync();
        if (path is null) return;
        try
        {
            var name = await ui.EditShareNameAsync(path, new DirectoryInfo(path).Name);
            if (name is null) return;
            await lan.AddSharedFolderAsync(path, displayName: name); RefreshLocal(); Report("文件夹已共享，局域网客户端可浏览和下载");
        }
        catch (Exception ex) { Report("共享失败：" + ex.Message); }
    }

    [RelayCommand]
    private async Task RenameFolderAsync(SharedFolder folder)
    {
        try
        {
            var name = await ui.EditShareNameAsync(folder.LocalPath, folder.Name);
            if (name is null) return;
            await lan.RenameSharedFolderAsync(folder.Id, name); RefreshLocal(); Report("共享名称已更新");
        }
        catch (Exception ex) { Report("共享名称更新失败：" + ex.Message); }
    }

    [RelayCommand]
    private async Task RemoveFolderAsync(SharedFolder folder)
    {
        try { await lan.RemoveSharedFolderAsync(folder.Id); RefreshLocal(); Report("已停止共享"); }
        catch (Exception ex) { Report("移除失败：" + ex.Message); }
    }

    [RelayCommand]
    private Task RefreshAsync() => ReadEntriesAsync();

    private async Task ReadEntriesAsync()
    {
        if (Peer is null) return;
        var peer = Peer;
        var version = ++_requestVersion;
        IsBusy = true;
        try
        {
            var entries = await lan.BrowseAsync(peer.Model, _rootId, _path, _stop.Token);
            if (_disposed || Peer != peer || version != _requestVersion) return;
            RemoteEntries.Clear();
            foreach (var entry in entries) RemoteEntries.Add(new SharedEntryViewModel(entry));
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed && version == _requestVersion) Report("读取共享目录失败：" + ex.Message); }
        finally { if (version == _requestVersion) IsBusy = false; }
    }

    [RelayCommand]
    private async Task OpenEntryAsync(SharedEntryViewModel entry)
    {
        if (Peer is null || IsBusy) return;
        if (entry.Model.IsDirectory)
        {
            _navigation.Push((_rootId, _path, Location));
            _rootId = entry.Model.RootId; _path = entry.Model.RelativePath;
            Location = entry.Model.Name;
            OnPropertyChanged(nameof(CanGoUp));
            await RefreshAsync();
        }
        else
        {
            IsBusy = true;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _downloadCancellation = cancellation;
            try
            {
                var peer = Peer;
                var directory = await ui.PickDownloadFolderAsync();
                if (directory is null || _disposed || cancellation.IsCancellationRequested || Peer != peer) return;
                IsDownloading = true;
                var path = await lan.DownloadSharedFileAsync(peer.Model, entry.Model, cancellation.Token, directory);
                Report("下载完成：" + path);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { if (!_disposed) Report("下载已取消。"); }
            catch (Exception ex) { Report("下载失败：" + ex.Message); }
            finally { _downloadCancellation = null; IsDownloading = false; IsBusy = false; }
        }
    }

    [RelayCommand] private void CancelDownload() => _downloadCancellation?.Cancel();
    public void Dispose() { if (_disposed) return; _disposed = true; _requestVersion++; _stop.Cancel(); _downloadCancellation?.Cancel(); _stop.Dispose(); }

    [RelayCommand]
    private async Task GoUpAsync()
    {
        if (IsBusy || !_navigation.TryPop(out var parent)) return;
        _rootId = parent.RootId; _path = parent.Path; Location = parent.Label;
        OnPropertyChanged(nameof(CanGoUp));
        await RefreshAsync();
    }
}
