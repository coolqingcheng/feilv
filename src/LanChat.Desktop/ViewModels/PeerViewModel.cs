using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using LanChat.Core.Models;
using LanChat.Desktop.Services;
using System.Windows.Input;

namespace LanChat.Desktop.ViewModels;

public partial class PeerViewModel : ViewModelBase, IDisposable
{
    public Peer Model { get; private set; }
    public string Id => Model.Id;
    [ObservableProperty] private string _nickname = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private Bitmap? _avatar;
    [ObservableProperty] private bool _isRecipientSelected;
    [ObservableProperty] private ICommand? _sendFilesCommand;
    [ObservableProperty] private ICommand? _sendFolderCommand;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasTransfer))] private string _transferStatus = "";
    [ObservableProperty] private string _transferName = "";
    [ObservableProperty] private string _transferSpeed = "";
    [ObservableProperty] private double _transferProgress;
    [ObservableProperty] private bool _isTransferring;
    [ObservableProperty] private bool _isPreparingTransfer;
    private string? _completedAttachmentId;
    public bool HasTransfer => TransferStatus.Length > 0;
    public string Initial => string.IsNullOrEmpty(Nickname) ? "?" : Nickname[..1].ToUpperInvariant();
    public string Presence => IsOnline ? "在线" : "离线";
    public string VersionLabel => string.IsNullOrEmpty(Model.AppVersion) ? "版本未知" : "v" + Model.AppVersion;

    public PeerViewModel(Peer model) { Model = model; Update(model); }

    public void Update(Peer model)
    {
        var previousPath = Model.AvatarPath;
        Model = model;
        Nickname = model.Nickname;
        Address = model.Address;
        IsOnline = model.IsOnline;
        if (previousPath != model.AvatarPath || Avatar is null)
        {
            var old = Avatar;
            Avatar = ImageLoader.Load(model.AvatarPath, 96);
            old?.Dispose();
        }
        OnPropertyChanged(nameof(Initial));
        OnPropertyChanged(nameof(Presence));
        OnPropertyChanged(nameof(VersionLabel));
    }

    public void PrepareTransfer(string name)
    {
        if (IsTransferring && !IsPreparingTransfer) return;
        TransferName = name; TransferStatus = "准备发送…"; TransferSpeed = "";
        TransferProgress = 0; IsPreparingTransfer = true; IsTransferring = true;
    }

    public void OfferChanged(ChatMessage message)
    {
        if (_completedAttachmentId == message.AttachmentId || (IsTransferring && !IsPreparingTransfer)) return;
        TransferName = message.FileName ?? "文件";
        TransferStatus = message.Delivery switch { DeliveryState.Sent => "等待对方接收", DeliveryState.Failed => "发送失败", _ => "发送接收请求…" };
        TransferSpeed = ""; IsTransferring = false; IsPreparingTransfer = false; TransferProgress = 0;
    }

    public void ApplyTransfer(TransferUpdate update)
    {
        TransferName = update.Name;
        TransferStatus = update.Status == "已完成" ? (update.IsUpload ? "发送完成" : "接收完成") : update.Status;
        TransferProgress = update.TotalBytes == 0 ? 0 : Math.Clamp(update.CompletedBytes * 100.0 / update.TotalBytes, 0, 100);
        IsPreparingTransfer = update.Status is "准备文件夹" or "准备发送" or "连接中";
        IsTransferring = IsPreparingTransfer || update.Status is "发送中" or "下载中";
        TransferSpeed = IsPreparingTransfer || update.Status == "排队等待" ? "" : ImageLoader.TransferSpeed(update.BytesPerSecond);
        if (update.Status == "已完成") _completedAttachmentId = update.AttachmentId;
    }

    public void FailTransfer(string reason)
    {
        TransferStatus = "发送失败：" + reason; TransferSpeed = ""; IsTransferring = false; IsPreparingTransfer = false;
    }

    public void Dispose() { Avatar?.Dispose(); Avatar = null; }
}
