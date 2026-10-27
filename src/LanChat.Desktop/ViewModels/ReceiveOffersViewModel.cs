using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanChat.Core.Models;
using LanChat.Desktop.Services;

namespace LanChat.Desktop.ViewModels;

/// <summary>Receives queued file offers according to the saved automatic/manual preference.</summary>
public partial class ReceiveOffersViewModel(FileInboxViewModel inbox, IUiServices ui, Func<bool>? autoReceive = null) : ViewModelBase, IDisposable
{
    private readonly List<ChatMessage> _pending = [];
    private bool _disposed;
    private bool _rejectionFailed;
    private bool AutomaticEnabled => !_disposed && autoReceive?.Invoke() == true;
    public Task AutomaticReceiveTask { get; private set; } = Task.CompletedTask;
    [ObservableProperty] private ChatMessage? _current;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRespond)), NotifyPropertyChangedFor(nameof(AcceptLabel)), NotifyPropertyChangedFor(nameof(Header)), NotifyPropertyChangedFor(nameof(QueueHint)), NotifyPropertyChangedFor(nameof(ShowDecisionActions)), NotifyPropertyChangedFor(nameof(ShowTransferProgress)), NotifyPropertyChangedFor(nameof(IsIndeterminate))] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIndeterminate))] private double _progress;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Header)), NotifyPropertyChangedFor(nameof(ShowDecisionActions))] private bool _isAutomatic;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Header)), NotifyPropertyChangedFor(nameof(QueueHint)), NotifyPropertyChangedFor(nameof(ShowTransferProgress))] private bool _isRejecting;
    [ObservableProperty] private string _speedText = "正在连接…";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError)), NotifyPropertyChangedFor(nameof(Header)), NotifyPropertyChangedFor(nameof(AcceptLabel)), NotifyPropertyChangedFor(nameof(ShowDecisionActions))] private string _error = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCompleted)), NotifyPropertyChangedFor(nameof(CanRespond)), NotifyPropertyChangedFor(nameof(Header)), NotifyPropertyChangedFor(nameof(QueueHint)), NotifyPropertyChangedFor(nameof(ShowDecisionActions)), NotifyPropertyChangedFor(nameof(ShowTransferProgress))]
    private string? _completedPath;
    public bool IsOpen => Current is not null;
    public bool IsCompleted => CompletedPath is not null;
    public string Header => IsCompleted ? (Current?.IsFolder == true ? "文件夹接收完成" : "文件接收完成")
        : IsRejecting ? "正在拒绝接收…"
        : IsBusy ? (Current?.IsFolder == true ? "正在接收文件夹…" : "正在接收文件…")
        : IsAutomatic && !HasError ? "正在准备接收…" : (Current?.IsFolder == true ? "是否接收这个文件夹？" : "是否接收这个文件？");
    public bool ShowDecisionActions => !IsCompleted && !IsBusy && (!IsAutomatic || HasError);
    public bool ShowTransferProgress => IsBusy && !IsRejecting || IsCompleted;
    public bool IsIndeterminate => IsBusy && Progress <= 0;
    public bool CanRespond => IsOpen && !IsBusy && !IsCompleted;
    public bool HasError => Error.Length > 0;
    public string FileName => Current?.FileName ?? "附件";
    public string Size => (Current?.IsFolder == true ? "文件夹 · " : "") + ImageLoader.FileSize(Current?.FileSize ?? 0);
    public string Source => "来自 " + (string.IsNullOrEmpty(Current?.PeerNickname) ? "未知电脑" : Current.PeerNickname);
    public string Address => "IP " + Current?.PeerAddress;
    public string QueueHint => IsRejecting ? "正在通知发送方…" : _pending.Count > 0 ? $"还有 {_pending.Count} 个文件等待" + (AutomaticEnabled ? "接收" : "确认")
        : IsCompleted ? "文件已保存，可打开所在文件夹查看。" : IsBusy || IsAutomatic ? "正在接收，文件按日期保存" : "确认后开始接收，文件按日期保存";
    public string AcceptLabel => IsBusy ? "正在接收…" : HasError && !_rejectionFailed ? "重试接收" : "接收";

    public void Enqueue(ChatMessage message)
    {
        if (_disposed || message.LocalPath is not null || message.Kind == MessageKind.Text || message.Delivery == DeliveryState.Rejected) return;
        if (Same(Current, message) || _pending.Any(x => Same(x, message))) return;
        if (Current is null) Current = message;
        else _pending.Add(message);
        OnPropertyChanged(nameof(QueueHint));
        ApplyReceivePreference();
    }

    public void ApplyReceivePreference()
    {
        if (_disposed) return;
        IsAutomatic = AutomaticEnabled;
        OnPropertyChanged(nameof(QueueHint));
        if (AutomaticEnabled && IsCompleted && !IsBusy && _pending.Count > 0) { MoveNext(); return; }
        if (!AutomaticEnabled || !CanRespond || !AutomaticReceiveTask.IsCompleted) return;
        AutomaticReceiveTask = ReceiveAutomaticallyAsync();
    }

    private async Task ReceiveAutomaticallyAsync()
    {
        // Yield once so enqueue/property notifications finish before the serial receive loop starts.
        await Task.Yield();
        while (AutomaticEnabled && Current is not null && CanRespond)
        {
            await AcceptAsync();
            if (!IsCompleted || !AutomaticEnabled || _pending.Count == 0) return;
            MoveNext();
        }
    }

    public void MarkReceived(ChatMessage message)
    {
        _pending.RemoveAll(x => Same(x, message));
        if (Same(Current, message) && !IsBusy) Complete(message);
        OnPropertyChanged(nameof(QueueHint));
    }

    public void Remove(ChatMessage message)
    {
        _pending.RemoveAll(x => Same(x, message));
        if (Same(Current, message) && !IsBusy) MoveNext();
        OnPropertyChanged(nameof(QueueHint));
    }

    private static bool Same(ChatMessage? left, ChatMessage right) => left?.Id == right.Id && left.PeerId == right.PeerId;
    partial void OnCurrentChanged(ChatMessage? value)
    {
        IsAutomatic = AutomaticEnabled;
        _rejectionFailed = false;
        CompletedPath = null; Error = ""; Progress = 0; SpeedText = "正在连接…";
        foreach (var name in new[] { nameof(IsOpen), nameof(CanRespond), nameof(Header), nameof(FileName), nameof(Size), nameof(Source), nameof(Address), nameof(QueueHint) }) OnPropertyChanged(name);
    }

    private void Complete(ChatMessage message)
    {
        if (message.LocalPath is null || !(message.IsFolder ? Directory.Exists(message.LocalPath) : File.Exists(message.LocalPath))) { Error = "接收的文件或文件夹已被移动或删除，请重新接收。"; return; }
        CompletedPath = message.LocalPath; Progress = 100; Error = "";
    }

    private void MoveNext()
    {
        var next = _pending.FirstOrDefault();
        if (next is not null) _pending.RemoveAt(0);
        Current = next;
        ApplyReceivePreference();
    }

    [RelayCommand]
    private async Task AcceptAsync()
    {
        if (_disposed || Current is not { } offer || !CanRespond) return;
        IsBusy = true;
        _rejectionFailed = false;
        Error = "";
        try
        {
            await inbox.ReceiveOfferedFileAsync(offer);
            if (Same(Current, offer)) Complete(offer);
        }
        catch (OperationCanceledException) { Error = "接收已取消，可以稍后重试。"; }
        catch (Exception ex) { Error = "接收失败：" + ex.Message; }
        finally
        {
            IsBusy = false;
            if (IsCompleted && AutomaticEnabled && AutomaticReceiveTask.IsCompleted && _pending.Count > 0) MoveNext();
        }
    }

    [RelayCommand]
    public void Defer() { if (!IsBusy) MoveNext(); }

    [RelayCommand]
    private async Task RejectAsync()
    {
        if (_disposed || Current is not { } offer || !CanRespond) return;
        var rejected = false;
        IsRejecting = true; IsBusy = true; Error = "";
        try
        {
            await inbox.RejectOfferedFileAsync(offer);
            rejected = true;
        }
        catch (OperationCanceledException) { _rejectionFailed = true; Error = "拒绝通知已取消，可稍后重试拒绝。"; }
        catch (Exception ex) { _rejectionFailed = true; Error = "拒绝通知失败，可重试拒绝：" + ex.Message; }
        finally { IsRejecting = false; IsBusy = false; }
        if (rejected && Same(Current, offer)) MoveNext();
    }

    [RelayCommand]
    private void OpenReceivedFolder()
    {
        if (!IsCompleted || CompletedPath is not { } path || IsBusy) return;
        try { ui.RevealFile(path); Error = ""; }
        catch (Exception ex) { Error = "打开所在文件夹失败：" + ex.Message; }
    }

    public void UpdateTransfer(TransferUpdate update)
    {
        if (Current?.Id == update.Id && !update.IsUpload)
        {
            Progress = update.TotalBytes == 0 ? 0 : Math.Clamp(update.CompletedBytes * 100.0 / update.TotalBytes, 0, 100);
            SpeedText = ImageLoader.TransferSpeed(update.BytesPerSecond);
        }
    }

    public void Dispose() { _disposed = true; _pending.Clear(); }
}
