using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanChat.Core.Models;
using LanChat.Core.Localization;
using LanChat.Desktop.Services;

namespace LanChat.Desktop.ViewModels;

public partial class FileHistoryItemViewModel : ViewModelBase
{
    private readonly FileInboxViewModel _owner;
    private readonly string _localId;
    public ChatMessage Model { get; private set; }
    public string Key => Model.PeerId + ":" + Model.Id;
    public string FileName => Model.FileName ?? Localizer.Text("附件");
    public string Size => ImageLoader.FileSize(Model.FileSize);
    public string Source => Model.PeerId == QrUpload.SourceId ? Localizer.Text("来自二维码上传")
        : (IsMine ? "发给 " : "来自 ") + (string.IsNullOrEmpty(Model.PeerNickname) ? Localizer.Text("未知设备") : Model.PeerNickname);
    public string Address => string.IsNullOrEmpty(Model.PeerAddress) ? "IP 未记录" : "IP " + Model.PeerAddress;
    public string Time => (Model.ReceivedAt ?? Model.Timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string TimeDescription => (IsMine ? "发送时间：" : "接收时间：") + Time;
    public string Icon => Model.IsFolder ? "folder" : Model.Kind == MessageKind.Image ? "image" : "file";
    public bool IsMine => Model.SenderId == _localId;
    public bool HasLocalFile { get; private set; }
    public bool IsReceiveAction => !IsMine && !HasLocalFile && Model.AttachmentId is not null && !IsRejected;
    public bool IsRejected => Model.Delivery == DeliveryState.Rejected;
    public string RejectionLabel => IsMine ? "对方已拒绝" : "已拒绝";
    public bool CanAct => !IsDeleting && !IsBusy && Model.Delivery != DeliveryState.Sending && (HasLocalFile || (!IsRejected && (IsMine ? Model.Delivery == DeliveryState.Failed : Model.AttachmentId is not null)));
    public bool CanDelete => !IsDeleting && !IsBusy && Model.Delivery != DeliveryState.Sending && !_owner.IsDownloading(Key);
    public string ActionLabel => IsBusy ? IsMine ? "发送中" : "接收中" : Model.Delivery == DeliveryState.Sending ? "发送中" : IsMine && Model.Delivery == DeliveryState.Failed ? "重试" : HasLocalFile ? "打开文件夹" : IsRejected ? "已拒绝" : IsMine || Model.AttachmentId is null ? "不可用" : "接收";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanAct)), NotifyPropertyChangedFor(nameof(ActionLabel)), NotifyPropertyChangedFor(nameof(CanDelete)), NotifyCanExecuteChangedFor(nameof(DeleteCommand))] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanAct)), NotifyPropertyChangedFor(nameof(CanDelete)), NotifyCanExecuteChangedFor(nameof(DeleteCommand))] private bool _isDeleting;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _transferText = "正在连接…";

    public FileHistoryItemViewModel(ChatMessage message, string localId, FileInboxViewModel owner)
    {
        Model = message; _localId = localId; _owner = owner; Update(message);
    }

    public void Update(ChatMessage message)
    {
        Model = message;
        HasLocalFile = message.IsFolder ? Directory.Exists(message.LocalPath) : File.Exists(message.LocalPath);
        OnPropertyChanged(nameof(HasLocalFile));
        OnPropertyChanged(nameof(IsReceiveAction));
        OnPropertyChanged(nameof(IsRejected));
        OnPropertyChanged(nameof(RejectionLabel));
        OnPropertyChanged(nameof(CanAct));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(CanDelete));
        DeleteCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand] private Task ActAsync() => _owner.ActAsync(this);
    [RelayCommand(CanExecute = nameof(CanDelete))] private Task DeleteAsync() => _owner.DeleteAsync(this);
}
