using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanChat.Core.Models;
using LanChat.Core.Services;
using LanChat.Desktop.Services;

namespace LanChat.Desktop.ViewModels;

/// <summary>File history, paged search and transfer actions. The view owns only scrolling and native drag/drop.</summary>
public partial class FileInboxViewModel(ILanService lan, ILocalStore store, IUiServices ui, Action<string> status) : ViewModelBase, IDisposable
{
    private readonly Dictionary<string, FileHistoryItemViewModel> _loaded = [];
    private readonly Dictionary<string, Task> _downloads = [];
    private readonly HashSet<string> _deletedWhileLoading = [];
    private readonly CancellationTokenSource _transfers = new();
    private CancellationTokenSource? _searchCancellation;
    private FileHistoryCursor? _cursor;
    private string _activeSearch = "";
    private IReadOnlyList<string> _pendingFiles = [];
    private int _queryVersion;
    private bool _disposed;
    public const int PageSize = 50;
    public ObservableCollection<FileHistoryItemViewModel> Files { get; } = [];
    public ObservableCollection<PeerViewModel> Recipients { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDevices)), NotifyPropertyChangedFor(nameof(IsHistory)), NotifyPropertyChangedFor(nameof(ShowTransferSummary))] private int _selectedTab;
    [ObservableProperty] private string _search = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty)), NotifyPropertyChangedFor(nameof(HistoryHint))] private bool _isLoading;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HistoryHint))] private bool _hasMore;
    [ObservableProperty] private bool _isPickerOpen;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanConfirm))] private bool _isSending;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanConfirm))] private PeerViewModel? _selectedRecipient;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFeedback))] private string _feedback = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasTransferSummary)), NotifyPropertyChangedFor(nameof(ShowTransferSummary))] private string _transferSummary = "";
    public bool HasTransferSummary => TransferSummary.Length > 0;
    public bool HasActiveTransfers => IsSending || _downloads.Count != 0 || Files.Any(x => x.IsBusy);
    public bool ShowTransferSummary => HasTransferSummary && IsHistory;
    public bool HasFeedback => Feedback.Length > 0;
    public bool IsDevices => SelectedTab == 0;
    public bool IsHistory => SelectedTab == 1;
    public string OnlineHint => $"{Recipients.Count} 台电脑在线 · 拖文件到设备即可发送";
    public bool HasSearch => Search.Length > 0;
    public bool CanConfirm => Recipients.Any(x => x.IsOnline && x.IsRecipientSelected) && !IsSending;
    public string PickerSelectionHint => $"已选 {Recipients.Count(x => x.IsOnline && x.IsRecipientSelected)} 台电脑 · 按顺序发送";
    public bool HasPendingItems => _pendingFiles.Count > 0;
    public bool IsEmpty => !IsLoading && Files.Count == 0;
    public bool NoRecipients => Recipients.Count == 0;
    public string EmptyText => string.IsNullOrWhiteSpace(Search) ? "还没有收到文件" : "没有找到匹配的文件";
    public string EmptyHint => string.IsNullOrWhiteSpace(Search) ? "局域网内的文件会显示在这里" : "试试其他文件名";
    public string HistoryHint => IsLoading ? "正在加载记录…" : Files.Count == 0 ? "" : HasMore ? "向上滚动，加载更早记录" : "已显示全部记录";
    public string PickerAction => _pendingFiles.Count == 0 ? "选择文件" : $"发送 {_pendingFiles.Count} 项";
    public Task SearchTask { get; private set; } = Task.CompletedTask;
    public event Action<bool>? ScrollRequested;
    public event Action<ChatMessage>? HistoryDeleted;

    public Task InitializeAsync() => ResetHistoryAsync(false);
    partial void OnSearchChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearch));
        OnPropertyChanged(nameof(EmptyText)); OnPropertyChanged(nameof(EmptyHint));
        SearchTask = ResetHistoryAsync(true);
    }

    private async Task ResetHistoryAsync(bool debounce)
    {
        var version = ++_queryVersion;
        _searchCancellation?.Cancel(); _searchCancellation?.Dispose();
        _searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_transfers.Token);
        var token = _searchCancellation.Token;
        _activeSearch = Search.Trim(); _cursor = null; HasMore = false;
        _deletedWhileLoading.Clear();
        Files.Clear(); _loaded.Clear(); IsLoading = true; NotifyList();
        try
        {
            if (debounce) await Task.Delay(220, token);
            var page = await store.LoadFileHistoryAsync(_activeSearch, pageSize: PageSize, cancellationToken: token);
            if (version != _queryVersion || _disposed) return;
            foreach (var message in page.Messages) AddOrUpdate(message, false);
            _cursor = page.OlderCursor; HasMore = page.HasMore;
            ScrollRequested?.Invoke(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (version == _queryVersion) Report("读取文件记录失败：" + ex.Message); }
        finally { if (version == _queryVersion) { IsLoading = false; _deletedWhileLoading.Clear(); NotifyList(); } }
    }

    public async Task<bool> LoadOlderAsync()
    {
        if (IsLoading || !HasMore || _disposed) return false;
        var version = _queryVersion;
        var token = _searchCancellation?.Token ?? _transfers.Token;
        IsLoading = true;
        try
        {
            var page = await store.LoadFileHistoryAsync(_activeSearch, _cursor, PageSize, token);
            if (version != _queryVersion || _disposed) return false;
            foreach (var message in page.Messages) AddOrUpdate(message, false);
            _cursor = page.OlderCursor; HasMore = page.HasMore;
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception ex) { if (version == _queryVersion) Report("读取历史记录失败：" + ex.Message); return false; }
        finally { if (version == _queryVersion) { IsLoading = false; _deletedWhileLoading.Clear(); NotifyList(); } }
    }

    public void Ingest(ChatMessage message)
    {
        if (message.Kind == MessageKind.Text || !Matches(message)) return;
        var key = message.PeerId + ":" + message.Id;
        if (_loaded.TryGetValue(key, out var existing)) { existing.Update(message); return; }
        // A late download/retry for an older, unloaded record must not fill the gap between pages.
        if (HasMore && Files.Count > 0 && Compare(message, Files[0].Model) < 0) return;
        AddOrUpdate(message, true);
    }

    private bool Matches(ChatMessage message) => _activeSearch.Length == 0 || (message.FileName?.Contains(_activeSearch, StringComparison.OrdinalIgnoreCase) ?? false);
    private void AddOrUpdate(ChatMessage message, bool live)
    {
        var key = message.PeerId + ":" + message.Id;
        if (_loaded.ContainsKey(key) || _deletedWhileLoading.Contains(key)) return; // Preserve changes made while a history page was being read.
        var item = new FileHistoryItemViewModel(message, lan.Profile.Id, this);
        var index = Files.Count;
        while (index > 0 && Compare(message, Files[index - 1].Model) < 0) index--;
        Files.Insert(index, item); _loaded.Add(key, item); NotifyList();
        if (live) ScrollRequested?.Invoke(item.IsMine);
    }

    private static int Compare(ChatMessage left, ChatMessage right)
    {
        var time = (left.ReceivedAt ?? left.Timestamp).ToUnixTimeMilliseconds().CompareTo((right.ReceivedAt ?? right.Timestamp).ToUnixTimeMilliseconds());
        if (time != 0) return time;
        var id = string.CompareOrdinal(left.Id, right.Id);
        return id != 0 ? id : string.CompareOrdinal(left.PeerId, right.PeerId);
    }
    private void NotifyList() { OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(HistoryHint)); }
    private void Report(string text) { Feedback = text; status(text); }

    public bool IsDownloading(string key) => _downloads.ContainsKey(key);

    public async Task DeleteAsync(FileHistoryItemViewModel item)
    {
        if (_disposed || !item.CanDelete) return;
        item.IsDeleting = true;
        try
        {
            await store.DeleteMessageAsync(item.Model.PeerId, item.Model.Id, _transfers.Token);
            if (_disposed) return;
            if (IsLoading) _deletedWhileLoading.Add(item.Key);
            if (_loaded.Remove(item.Key, out var current)) Files.Remove(current);
            HistoryDeleted?.Invoke(item.Model);
            NotifyList(); Report("记录已删除，本地文件保留。");
            if (Files.Count == 0 && HasMore && !IsLoading) await LoadOlderAsync();
        }
        catch (OperationCanceledException) when (_transfers.IsCancellationRequested) { }
        catch (Exception ex) { Report("删除记录失败：" + ex.Message); }
        finally { item.IsDeleting = false; }
    }

    public void UpdatePeer(PeerViewModel peer)
    {
        if (peer.IsOnline)
        {
            if (!Recipients.Contains(peer))
            {
                peer.SendFilesCommand = SendToDeviceCommand; peer.SendFolderCommand = SendFolderToDeviceCommand;
                peer.PropertyChanged += RecipientChanged; Recipients.Add(peer);
            }
        }
        else
        {
            peer.IsRecipientSelected = false; peer.PropertyChanged -= RecipientChanged;
            Recipients.Remove(peer); if (SelectedRecipient == peer) SelectedRecipient = null;
        }
        OnPropertyChanged(nameof(NoRecipients));
        OnPropertyChanged(nameof(OnlineHint));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(PickerSelectionHint));
    }

    partial void OnSelectedRecipientChanged(PeerViewModel? value)
    {
        if (value is not null) foreach (var peer in Recipients) peer.IsRecipientSelected = peer == value;
    }

    private void RecipientChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(PeerViewModel.IsRecipientSelected)) return;
        OnPropertyChanged(nameof(CanConfirm)); OnPropertyChanged(nameof(PickerSelectionHint));
    }

    [RelayCommand] private void BeginSend() => BeginSendFiles([]);
    public void BeginSendFiles(IReadOnlyList<string> paths)
    {
        if (IsSending) { Report("上一批文件正在发送，请稍候。"); return; }
        _pendingFiles = paths;
        if (!Recipients.Any(x => x.IsRecipientSelected) && Recipients.FirstOrDefault() is { } first) first.IsRecipientSelected = true;
        OnPropertyChanged(nameof(PickerAction)); OnPropertyChanged(nameof(HasPendingItems)); OnPropertyChanged(nameof(PickerSelectionHint)); IsPickerOpen = true;
    }
    [RelayCommand] public void CancelPicker() { IsPickerOpen = false; _pendingFiles = []; OnPropertyChanged(nameof(HasPendingItems)); OnPropertyChanged(nameof(PickerAction)); }
    [RelayCommand] private void ClearSearch() => Search = "";
    public void RevealLatest()
    {
        SelectedTab = 1;
        if (HasSearch) Search = "";
        else ScrollRequested?.Invoke(true);
    }

    [RelayCommand]
    private Task ConfirmSendAsync()
    {
        var selected = Recipients.Where(x => x.IsOnline && x.IsRecipientSelected).ToArray();
        if (selected.Length == 0) { Report("请先勾选接收电脑。"); return Task.CompletedTask; }
        return SendBatchAsync(selected, _pendingFiles, false);
    }

    [RelayCommand]
    private Task ConfirmFoldersAsync()
    {
        var selected = Recipients.Where(x => x.IsOnline && x.IsRecipientSelected).ToArray();
        if (selected.Length == 0) { Report("请先勾选接收电脑。"); return Task.CompletedTask; }
        return SendBatchAsync(selected, [], true);
    }

    [RelayCommand]
    private Task SendToDeviceAsync(PeerViewModel? peer)
    {
        if (peer is not { IsOnline: true }) { Report("这台电脑已离线，请等待上线后发送。"); return Task.CompletedTask; }
        return SendBatchAsync([peer], [], false);
    }

    [RelayCommand]
    private Task SendFolderToDeviceAsync(PeerViewModel? peer)
    {
        if (peer is not { IsOnline: true }) { Report("这台电脑已离线，请等待上线后发送。"); return Task.CompletedTask; }
        return SendBatchAsync([peer], [], true);
    }

    public Task SendDroppedFilesToDeviceAsync(PeerViewModel peer, IReadOnlyList<string> paths)
    {
        if (!peer.IsOnline) { Report("这台电脑已离线，文件未发送。"); return Task.CompletedTask; }
        return paths.Count == 0 ? Task.CompletedTask : SendBatchAsync([peer], paths, false);
    }

    private async Task SendBatchAsync(IReadOnlyList<PeerViewModel> recipients, IReadOnlyList<string> paths, bool pickFolders)
    {
        if (IsSending) { Report("上一批文件正在发送，请稍候。"); return; }
        // Snapshot all recipients before the native picker; later checkbox changes cannot redirect a batch.
        var targets = recipients.Select(x => (Row: x, Peer: x.Model)).ToArray();
        IsSending = true;
        try
        {
            if (paths.Count == 0) paths = pickFolders ? await ui.PickFoldersAsync() : await ui.PickFilesAsync();
            if (paths.Count == 0) return;
            CancelPicker();
            var sent = 0; var failed = 0; string? lastError = null;
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var items = paths.Select(Path.GetFullPath).Distinct(comparer).ToArray();
            foreach (var target in targets)
            {
                foreach (var path in items)
                {
                    _transfers.Token.ThrowIfCancellationRequested();
                    try
                    {
                        if (!target.Row.IsOnline) throw new IOException("设备已离线");
                        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                        target.Row.PrepareTransfer(name);
                        Report("正在向 " + target.Peer.Nickname + " 发送：" + name);
                        if (Directory.Exists(path)) await lan.SendFolderAsync(target.Peer, path, _transfers.Token);
                        else
                        {
                            var file = new FileInfo(path);
                            var image = file.Length <= 20 * 1024 * 1024 && Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif";
                            await lan.SendFileAsync(target.Peer, path, image, _transfers.Token);
                        }
                        sent++;
                    }
                    catch (OperationCanceledException) when (_transfers.IsCancellationRequested) { throw; }
                    catch (Exception ex) { failed++; lastError = ex.Message; target.Row.FailTransfer(ex.Message); }
                }
            }
            Report($"已向 {targets.Length} 台电脑发送 {sent} 项接收请求。" + (failed > 0 ? $" {failed} 项失败：{lastError}" : ""));
        }
        catch (OperationCanceledException) when (_transfers.IsCancellationRequested) { }
        catch (Exception ex) { Report("发送失败：" + ex.Message); }
        finally { IsSending = false; }
    }

    public async Task ActAsync(FileHistoryItemViewModel item)
    {
        if (!item.CanAct) return;
        item.Update(item.Model);
        if (!item.CanAct) { Report("本地文件已移除，当前记录无法定位。"); return; }
        if (item.HasLocalFile && !(item.IsMine && item.Model.Delivery == DeliveryState.Failed))
        {
            try { ui.RevealFile(item.Model.LocalPath!); }
            catch (Exception ex) { Report("打开文件夹失败：" + ex.Message); }
            return;
        }
        var peer = lan.Peers.FirstOrDefault(x => x.Id == item.Model.PeerId);
        if (peer is null) { Report("未找到来源设备，请等待对方上线。"); return; }
        item.IsBusy = true;
        try
        {
            if (item.IsMine) await lan.RetryMessageAsync(peer, item.Model, _transfers.Token);
            else await ReceiveOfferedFileAsync(item.Model);
            item.Update(item.Model); Report(item.IsMine ? "文件已重新发送。" : "文件已接收，可打开文件夹定位。");
        }
        catch (OperationCanceledException) when (_transfers.IsCancellationRequested) { }
        catch (Exception ex) { Report("传输失败：" + ex.Message); }
        finally { item.IsBusy = false; item.Update(item.Model); }
    }

    public Task ReceiveOfferedFileAsync(ChatMessage message)
    {
        if (message.Kind == MessageKind.Text || message.SenderId == lan.Profile.Id || message.AttachmentId is null || message.Delivery == DeliveryState.Rejected)
            return Task.FromException(new InvalidOperationException("这条记录不是可接收的文件。"));
        if (message.IsFolder ? Directory.Exists(message.LocalPath) : File.Exists(message.LocalPath)) return Task.CompletedTask;
        var key = message.PeerId + ":" + message.Id;
        if (_loaded.TryGetValue(key, out var row) && row.IsDeleting) return Task.FromException(new InvalidOperationException("记录正在删除，请稍候。"));
        if (_downloads.TryGetValue(key, out var existing)) return existing;
        var task = DownloadOfferedFileAsync(message, key);
        _downloads.Add(key, task);
        _ = task.ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _downloads.Remove(key);
            if (_loaded.TryGetValue(key, out var item)) item.Update(item.Model);
        }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    public async Task RejectOfferedFileAsync(ChatMessage message)
    {
        var key = message.PeerId + ":" + message.Id;
        if (_downloads.ContainsKey(key)) throw new InvalidOperationException("文件正在接收，无法拒绝。");
        var peer = lan.Peers.FirstOrDefault(x => x.Id == message.PeerId) ?? throw new IOException("未找到来源电脑，请等待对方上线。");
        await lan.RejectAttachmentAsync(peer, message, _transfers.Token);
        if (_loaded.TryGetValue(key, out var row)) row.Update(message);
        Report("已拒绝接收，已通知发送方。");
    }

    private async Task DownloadOfferedFileAsync(ChatMessage message, string key)
    {
        var peer = lan.Peers.FirstOrDefault(x => x.Id == message.PeerId) ?? throw new IOException("未找到来源电脑，请等待对方上线。");
        if (_loaded.TryGetValue(key, out var item)) item.IsBusy = true;
        try
        {
            await lan.DownloadAttachmentAsync(peer, message, _transfers.Token);
            Report("文件已接收，可打开文件夹定位。");
        }
        finally
        {
            if (_loaded.TryGetValue(key, out item)) { item.IsBusy = false; item.Update(message); }
        }
    }

    public void UpdateTransfer(TransferUpdate update)
    {
        var direction = update.IsUpload ? "发送" : "接收";
        var speed = ImageLoader.TransferSpeed(update.BytesPerSecond);
        var state = update.Status == "已完成" ? direction + "完成" : update.Status;
        TransferSummary = $"{state} · {speed}\n{update.Name}";
        foreach (var item in Files.Where(x => x.Model.Id == update.Id || (update.IsUpload && update.AttachmentId is not null && x.IsMine && x.Model.AttachmentId == update.AttachmentId)))
        {
            item.Progress = update.TotalBytes == 0 ? 0 : update.CompletedBytes * 100.0 / update.TotalBytes;
            item.TransferText = update.Status + " · " + speed;
            item.IsBusy = update.Status is "连接中" or "下载中" or "准备发送" or "发送中";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _transfers.Cancel(); _searchCancellation?.Cancel();
        foreach (var peer in Recipients) peer.PropertyChanged -= RecipientChanged;
        _searchCancellation?.Dispose(); _transfers.Dispose();
    }
}
