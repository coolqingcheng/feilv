using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanChat.Core.Models;
using LanChat.Core.Localization;
using LanChat.Core.Services;
using LanChat.Desktop.Services;
using LanChat.Infrastructure.QrSharing;
using LanChat.Infrastructure.Updates;

namespace LanChat.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ILanService _lan;
    private readonly ILocalStore _store;
    private readonly IQrHttpServer _qrServer;
    private readonly IUiServices _ui;
    private readonly Dictionary<string, PeerViewModel> _peers = [];
    private Dictionary<string, string> _remarks = new(StringComparer.Ordinal);
    private readonly HashSet<(string PeerId, string Id)> _notifiedOffers = [];
    private readonly Queue<(string PeerId, string Id)> _recentOffers = [];
    private bool _disposed;
    [ObservableProperty] private string _page = "Files";
    [ObservableProperty] private string _status = "正在启动局域网服务…";
    public FileInboxViewModel Inbox { get; }
    public ReceiveOffersViewModel Incoming { get; }
    public SharesViewModel Shares { get; }
    public SettingsViewModel Settings { get; }
    public QrShareViewModel QrShare { get; }
    public AppUpdatesViewModel Updates { get; }
    public bool IsFiles => Page == "Files";
    public bool IsShares => Page == "Shares";
    public bool IsSettings => Page == "Settings";
    public bool IsQr => Page == "Qr";
    public bool ShowPageStatus => IsShares || IsSettings;
    public string PageTitle => IsQr ? "二维码分享" : IsSettings ? "设置" : IsShares ? "共享文件夹" : "飞驴  v" + AppReleases.CurrentVersion;
    public string WindowTitle => Localizer.Text("飞驴 · 局域网文件传输") + " v" + AppReleases.CurrentVersion;
    public event Action<PeerViewModel, ChatMessage>? IncomingNotification;
    public event Action<PeerViewModel>? BrowsePeerSharesRequested;

    public MainWindowViewModel(ILanService lan, ILocalStore store, IUiServices ui)
    {
        _lan = lan;
        _store = store;
        _ui = ui;
        Inbox = new FileInboxViewModel(lan, store, ui, SetStatus, FormatPeerName);
        Incoming = new ReceiveOffersViewModel(Inbox, ui, () => lan.Profile.AutoReceive);
        Inbox.HistoryDeleted += Incoming.Remove;
        Shares = new SharesViewModel(lan, ui, SetStatus);
        Updates = new AppUpdatesViewModel(new GitHubUpdateService(store.DataDirectory), lan.Profile,
            () => Inbox.HasActiveTransfers || _peers.Values.Any(x => x.IsTransferring));
        Settings = new SettingsViewModel(lan, store, ui, SetStatus, Updates);
        Settings.ReceivePreferencesSaved += Incoming.ApplyReceivePreference;
        _qrServer = new QrHttpServer();
        _qrServer.UploadCompleted += OnQrUploadCompletedAsync;
        QrShare = new QrShareViewModel(_qrServer, lan.Profile, store, ui);
        lan.PeerChanged += OnPeerChanged;
        lan.MessageReceived += OnMessageReceived;
        lan.TransferChanged += OnTransferChanged;
        lan.Error += OnError;
        Localizer.Changed += OnLanguageChanged;
    }

    private Task OnQrUploadCompletedAsync(QrUpload upload) => Dispatcher.UIThread.InvokeAsync(() => RecordQrUploadAsync(upload));

    private async Task RecordQrUploadAsync(QrUpload upload)
    {
        if (_disposed) return;
        var message = new ChatMessage
        {
            Id = upload.Id, PeerId = QrUpload.SourceId, SenderId = QrUpload.SourceId,
            Kind = Path.GetExtension(upload.FileName).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" ? MessageKind.Image : MessageKind.File,
            FileName = upload.FileName, FileSize = upload.Size, LocalPath = upload.FilePath, Delivery = DeliveryState.Received,
            PeerNickname = "二维码上传", PeerAddress = upload.SenderAddress, Timestamp = upload.ReceivedAt, ReceivedAt = upload.ReceivedAt
        };
        try { if (!await _store.TrySaveReceivedMessageAsync(message)) return; }
        catch (Exception ex) { if (!_disposed) { Status = "二维码文件已保存，但记录写入失败：" + ex.Message; Inbox.Feedback = Status; } }
        if (_disposed) return;
        Inbox.Ingest(message);
        Incoming.EnqueueCompleted(message, upload.BytesPerSecond);
    }

    private void OnLanguageChanged()
    {
        if (_disposed) return;
        RefreshLanguage(); Inbox.RefreshLanguage(); Incoming.RefreshLanguage(); Shares.RefreshLanguage();
        Settings.RefreshLanguage(); QrShare.RefreshLanguage(); Updates.RefreshLanguage();
        foreach (var peer in _peers.Values.Concat(Inbox.Recipients).Distinct()) peer.RefreshLanguage();
        foreach (var item in Inbox.Files) item.RefreshLanguage();
        foreach (var entry in Shares.RemoteEntries) entry.RefreshLanguage();
    }

    private void OnPeerChanged(Peer peer) => Dispatcher.UIThread.Post(() => { if (!_disposed) UpdatePeer(peer); });
    private void OnMessageReceived(ChatMessage message) => Dispatcher.UIThread.Post(() => { if (!_disposed) ReceiveFile(message); });
    private void OnTransferChanged(TransferUpdate update) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        Inbox.UpdateTransfer(update); Incoming.UpdateTransfer(update);
        if (update.PeerId is { } peerId && _peers.TryGetValue(peerId, out var peer)) peer.ApplyTransfer(update);
        if (IsShares) Status = Inbox.TransferSummary;
    });
    private void OnError(string text) => Dispatcher.UIThread.Post(() => { if (!_disposed) { Status = text; Inbox.Feedback = text; } });

    public async Task InitializeAsync()
    {
        string? startupError = null;
        try { _ui.SetStartWithWindows(_lan.Profile.StartWithWindows); }
        catch (Exception ex) { startupError = "开机启动设置未生效：" + ex.Message; }
        try
        {
            _remarks = await _store.LoadPeerRemarksAsync();
            await Inbox.InitializeAsync();
            await _lan.StartAsync();
            Shares.RefreshLocal();
            Settings.RefreshEndpoint();
            Status = startupError ?? "局域网服务已启动 · 自动发现附近设备";
        }
        catch (Exception ex) { Status = "启动失败：" + ex.Message; Inbox.Feedback = Status; }
    }

    private void SetStatus(string text) => Dispatcher.UIThread.Post(() => Status = text);
    private void UpdatePeer(Peer peer)
    {
        if (_peers.TryGetValue(peer.Id, out var item)) item.Update(peer);
        else { item = new PeerViewModel(peer) { EditRemarkCommand = EditRemarkCommand, BrowseSharesCommand = BrowsePeerSharesCommand }; _peers.Add(peer.Id, item); }
        item.Remark = _remarks.GetValueOrDefault(peer.Id, "");
        Inbox.UpdatePeer(item);
        Shares.UpdatePeer(item);
    }

    private string FormatPeerName(string id, string name) => _remarks.TryGetValue(id, out var remark) && remark.Length > 0 ? name + "(" + remark + ")" : name;

    [RelayCommand]
    private async Task EditRemarkAsync(PeerViewModel? peer)
    {
        if (peer is null) return;
        try
        {
            var remark = await _ui.EditPeerRemarkAsync(peer.Nickname, peer.Remark);
            if (remark is null) return;
            remark = remark.Trim();
            if (remark.Length > 40) throw new ArgumentException("设备备注不能超过 40 个字符。");
            await _store.SavePeerRemarkAsync(peer.Id, remark);
            if (remark.Length == 0) _remarks.Remove(peer.Id); else _remarks[peer.Id] = remark;
            peer.Remark = remark; Inbox.RefreshRemarks(peer.Id); Incoming.RefreshLanguage();
            Inbox.Feedback = Status = "备注已保存";
        }
        catch (Exception ex) { Inbox.Feedback = Status = "保存备注失败：" + ex.Message; }
    }

    [RelayCommand]
    private void BrowsePeerShares(PeerViewModel? peer) { if (peer is { IsOnline: true }) BrowsePeerSharesRequested?.Invoke(peer); }

    private void ReceiveFile(ChatMessage message)
    {
        // Legacy peers can still deliver text packets; the file-only desktop never displays or notifies them.
        if (message.Kind == MessageKind.Text) return;
        Inbox.Ingest(message);
        if (message.SenderId == _lan.Profile.Id)
        {
            if (_peers.TryGetValue(message.PeerId, out var recipient)) recipient.OfferChanged(message);
            return;
        }
        if (message.Delivery == DeliveryState.Rejected) { Incoming.Remove(message); return; }
        if (message.LocalPath is not null) { Incoming.MarkReceived(message); return; }
        if (_peers.TryGetValue(message.PeerId, out var peer) && _notifiedOffers.Add((message.PeerId, message.Id)))
        {
            _recentOffers.Enqueue((message.PeerId, message.Id));
            if (_recentOffers.Count > 256) _notifiedOffers.Remove(_recentOffers.Dequeue());
            Incoming.Enqueue(message);
            IncomingNotification?.Invoke(peer, message);
        }
    }

    partial void OnPageChanged(string value)
    {
        Inbox.CancelPicker();
        OnPropertyChanged(nameof(IsFiles)); OnPropertyChanged(nameof(IsShares)); OnPropertyChanged(nameof(IsSettings)); OnPropertyChanged(nameof(IsQr)); OnPropertyChanged(nameof(ShowPageStatus)); OnPropertyChanged(nameof(PageTitle));
    }

    [RelayCommand] private async Task OpenQrShareAsync() { Page = "Qr"; await QrShare.StartAsync(); }

    public void ShowIncoming(PeerViewModel peer, ChatMessage message)
    {
        if (message.Kind == MessageKind.Text) return;
        Page = "Files"; Inbox.RevealLatest();
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        if (page is "Files" or "Shares" or "Settings") Page = page;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Localizer.Changed -= OnLanguageChanged;
        _qrServer.UploadCompleted -= OnQrUploadCompletedAsync;
        _lan.PeerChanged -= OnPeerChanged; _lan.MessageReceived -= OnMessageReceived;
        _lan.TransferChanged -= OnTransferChanged; _lan.Error -= OnError;
        Inbox.HistoryDeleted -= Incoming.Remove;
        Settings.ReceivePreferencesSaved -= Incoming.ApplyReceivePreference;
        Incoming.Dispose(); Inbox.Dispose(); Shares.Dispose(); Settings.Dispose(); QrShare.Dispose(); Updates.Dispose();
        foreach (var peer in _peers.Values) peer.Dispose();
        _peers.Clear(); _notifiedOffers.Clear(); _recentOffers.Clear();
    }
}
