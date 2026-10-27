using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanChat.Core.Models;
using LanChat.Desktop.Services;

namespace LanChat.Desktop.ViewModels;

/// <summary>Queues incoming file offers. Bytes are downloaded only after an explicit accept action.</summary>
public partial class ReceiveOffersViewModel(FileInboxViewModel inbox, IUiServices ui) : ViewModelBase
{
    private readonly List<ChatMessage> _pending = [];
    [ObservableProperty] private ChatMessage? _current;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRespond)), NotifyPropertyChangedFor(nameof(AcceptLabel))] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _speedText = "正在连接…";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCompleted)), NotifyPropertyChangedFor(nameof(CanRespond)), NotifyPropertyChangedFor(nameof(Header)), NotifyPropertyChangedFor(nameof(QueueHint))]
    private string? _completedPath;
    public bool IsOpen => Current is not null;
    public bool IsCompleted => CompletedPath is not null;
    public string Header => IsCompleted ? (Current?.IsFolder == true ? "文件夹接收完成" : "文件接收完成") : (Current?.IsFolder == true ? "是否接收这个文件夹？" : "是否接收这个文件？");
    public bool CanRespond => IsOpen && !IsBusy && !IsCompleted;
    public bool HasError => Error.Length > 0;
    public string FileName => Current?.FileName ?? "附件";
    public string Size => (Current?.IsFolder == true ? "文件夹 · " : "") + ImageLoader.FileSize(Current?.FileSize ?? 0);
    public string Source => "来自 " + (string.IsNullOrEmpty(Current?.PeerNickname) ? "未知电脑" : Current.PeerNickname);
    public string Address => "IP " + Current?.PeerAddress;
    public string QueueHint => _pending.Count > 0 ? $"还有 {_pending.Count} 个文件等待确认" : IsCompleted ? "文件已保存，可打开所在文件夹查看。" : "确认后开始接收，文件按日期保存";
    public string AcceptLabel => IsBusy ? "正在接收…" : "确定接收";

    public void Enqueue(ChatMessage message)
    {
        if (message.LocalPath is not null || message.Kind == MessageKind.Text) return;
        if (Same(Current, message) || _pending.Any(x => Same(x, message))) return;
        if (Current is null) Current = message;
        else _pending.Add(message);
        OnPropertyChanged(nameof(QueueHint));
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
    }

    [RelayCommand]
    private async Task AcceptAsync()
    {
        if (Current is not { } offer || !CanRespond) return;
        IsBusy = true;
        try
        {
            await inbox.ReceiveOfferedFileAsync(offer);
            if (Same(Current, offer)) Complete(offer);
        }
        catch (OperationCanceledException) { Error = "接收已取消，可以稍后重试。"; }
        catch (Exception ex) { Error = "接收失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public void Defer() { if (!IsBusy) MoveNext(); }

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
            Progress = update.TotalBytes == 0 ? 0 : update.CompletedBytes * 100.0 / update.TotalBytes;
            SpeedText = ImageLoader.TransferSpeed(update.BytesPerSecond);
        }
    }
}
