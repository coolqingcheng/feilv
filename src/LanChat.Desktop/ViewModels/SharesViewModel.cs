using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanChat.Core.Models;
using LanChat.Core.Services;
using LanChat.Desktop.Services;

namespace LanChat.Desktop.ViewModels;

public sealed class SharedEntryViewModel(SharedEntry model)
{
    public SharedEntry Model { get; } = model;
    public string Name => Model.Name;
    public string Icon => Model.IsDirectory ? "folder" : "file";
    public string Description => Model.IsDirectory ? "文件夹 · 点击浏览" : ImageLoader.FileSize(Model.Size);
    public string Action => Model.IsDirectory ? "打开" : "下载";
}

public partial class SharesViewModel(ILanService lan, IUiServices ui, Action<string> status) : ViewModelBase
{
    private readonly Stack<(string? RootId, string Path, string Label)> _navigation = new();
    private string? _rootId;
    private string _path = "";
    private int _requestVersion;
    [ObservableProperty] private PeerViewModel? _peer;
    [ObservableProperty] private string _location = "所有共享文件夹";
    [ObservableProperty] private bool _isBusy;
    public ObservableCollection<SharedFolder> LocalFolders { get; } = [];
    public ObservableCollection<PeerViewModel> Devices { get; } = [];
    public ObservableCollection<SharedEntryViewModel> RemoteEntries { get; } = [];
    public bool CanGoUp => _navigation.Count > 0;
    public bool IsEmpty => RemoteEntries.Count == 0;
    public bool HasPeer => Peer is not null;

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
        try { await lan.AddSharedFolderAsync(path); RefreshLocal(); status("文件夹已共享，局域网客户端可浏览和下载"); }
        catch (Exception ex) { status("共享失败：" + ex.Message); }
    }

    [RelayCommand]
    private async Task RemoveFolderAsync(SharedFolder folder)
    {
        try { await lan.RemoveSharedFolderAsync(folder.Id); RefreshLocal(); status("已停止共享"); }
        catch (Exception ex) { status("移除失败：" + ex.Message); }
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
            var entries = await lan.BrowseAsync(peer.Model, _rootId, _path);
            if (Peer != peer || version != _requestVersion) return;
            RemoteEntries.Clear();
            foreach (var entry in entries) RemoteEntries.Add(new SharedEntryViewModel(entry));
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex) { if (version == _requestVersion) status("读取共享目录失败：" + ex.Message); }
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
            try { var path = await lan.DownloadSharedFileAsync(Peer.Model, entry.Model); status("下载完成：" + path); }
            catch (Exception ex) { status("下载失败：" + ex.Message); }
            finally { IsBusy = false; }
        }
    }

    [RelayCommand]
    private async Task GoUpAsync()
    {
        if (IsBusy || !_navigation.TryPop(out var parent)) return;
        _rootId = parent.RootId; _path = parent.Path; Location = parent.Label;
        OnPropertyChanged(nameof(CanGoUp));
        await RefreshAsync();
    }
}
