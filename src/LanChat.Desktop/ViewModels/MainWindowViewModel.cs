using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanChat.Core.Models;
using LanChat.Core.Services;
using LanChat.Desktop.Services;
using LanChat.Infrastructure.QrSharing;
using LanChat.Infrastructure.Updates;

namespace LanChat.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ILanService _lan;
    private readonly IUiServices _ui;
    private readonly Dictionary<string, PeerViewModel> _peers = [];
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
    public string PageTitle => IsQr ? "二维码分享" : IsSettings ? "设置" : IsShares ? "共享文件夹" : "飞驴";
    public event Action<PeerViewModel, ChatMessage>? IncomingNotification;

    public MainWindowViewModel(ILanService lan, ILocalStore store, IUiServices ui)
    {
        _lan = lan;
        _ui = ui;
        Inbox = new FileInboxViewModel(lan, store, ui, SetStatus);
        Incoming = new ReceiveOffersViewModel(Inbox, ui, () => lan.Profile.AutoReceive);
        Inbox.HistoryDeleted += Incoming.Remove;
        Shares = new SharesViewModel(lan, ui, SetStatus);
        Updates = new AppUpdatesViewModel(new GitHubUpdateService(store.DataDirectory), lan.Profile,
            () => Inbox.HasActiveTransfers || _peers.Values.Any(x => x.IsTransferring));
        Settings = new SettingsViewModel(lan, store, ui, SetStatus, Updates);
        Settings.ReceivePreferencesSaved += Incoming.ApplyReceivePreference;
        QrShare = new QrShareViewModel(new QrHttpServer(), lan.Profile, store, ui);
        lan.PeerChanged += OnPeerChanged;
        lan.MessageReceived += OnMessageReceived;
        lan.TransferChanged += OnTransferChanged;
        lan.Error += OnError;
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
        else { item = new PeerViewModel(peer); _peers.Add(peer.Id, item); }
        Inbox.UpdatePeer(item);
        Shares.UpdatePeer(item);
    }

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
        _lan.PeerChanged -= OnPeerChanged; _lan.MessageReceived -= OnMessageReceived;
        _lan.TransferChanged -= OnTransferChanged; _lan.Error -= OnError;
        Inbox.HistoryDeleted -= Incoming.Remove;
        Settings.ReceivePreferencesSaved -= Incoming.ApplyReceivePreference;
        Incoming.Dispose(); Inbox.Dispose(); Settings.Dispose(); QrShare.Dispose(); Updates.Dispose();
        foreach (var peer in _peers.Values) peer.Dispose();
        _peers.Clear(); _notifiedOffers.Clear(); _recentOffers.Clear();
    }
}
