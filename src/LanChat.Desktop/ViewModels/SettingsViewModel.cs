using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanChat.Core.Services;
using LanChat.Core.Models;
using LanChat.Desktop.Services;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanChat.Desktop.ViewModels;

public partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly ILanService _lan;
    private readonly ILocalStore _store;
    private readonly IUiServices _ui;
    private readonly Action<string> _status;
    [ObservableProperty] private string _nickname;
    [ObservableProperty] private Bitmap? _avatar;
    [ObservableProperty] private string _theme;
    [ObservableProperty] private bool _showNotifications;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _autoDownloadUpdates;
    public AppUpdatesViewModel? Updates { get; }
    [ObservableProperty] private string _downloadDirectory;
    [ObservableProperty] private string _manualAddress = "";
    [ObservableProperty] private string _manualPort = "45832";
    [ObservableProperty] private bool _isConnecting;
    public string Initial => string.IsNullOrWhiteSpace(Nickname) ? "我" : Nickname[..1].ToUpperInvariant();
    public string DeviceId => _lan.Profile.Id[..12];
    public string VersionLabel => $"飞驴  /  {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)}";
    public string DataDirectory => _store.DataDirectory;
    public bool SupportsWindowsStartup => OperatingSystem.IsWindows();
    public string ThemeDescription => Theme switch { "Dark" => "当前主题：Dark", "Light" => "当前主题：Light", _ => "当前主题：跟随系统" };
    public string LocalEndpoint => $"{LocalAddress}:{_lan.Port}";
    private static string LocalAddress => NetworkInterface.GetAllNetworkInterfaces()
        .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(x => x.GetIPProperties().UnicastAddresses)
        .FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "127.0.0.1";
    public void RefreshEndpoint() => OnPropertyChanged(nameof(LocalEndpoint));

    public SettingsViewModel(ILanService lan, ILocalStore store, IUiServices ui, Action<string> status, AppUpdatesViewModel? updates = null)
    {
        _lan = lan; _store = store; _ui = ui; _status = status;
        _nickname = lan.Profile.Nickname; _theme = lan.Profile.Theme;
        _downloadDirectory = Path.Combine(store.DataDirectory, "Files"); _showNotifications = lan.Profile.ShowNotifications;
        _startWithWindows = lan.Profile.StartWithWindows;
        _autoDownloadUpdates = lan.Profile.AutoDownloadUpdates;
        Updates = updates;
        _avatar = ImageLoader.Load(lan.Profile.AvatarPath, 192);
    }

    partial void OnNicknameChanged(string value) => OnPropertyChanged(nameof(Initial));
    partial void OnThemeChanged(string value) => OnPropertyChanged(nameof(ThemeDescription));

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Nickname) || Nickname.Trim().Length > 40) { _status("设备名称应为 1–40 个字符"); return; }
        try
        {
            _ui.SetStartWithWindows(StartWithWindows);
            _lan.Profile.Nickname = Nickname.Trim();
            _lan.Profile.ShowNotifications = ShowNotifications;
            _lan.Profile.StartWithWindows = StartWithWindows;
            _lan.Profile.AutoDownloadUpdates = AutoDownloadUpdates;
            await _lan.UpdateProfileAsync();
            _status("设置已保存");
        }
        catch (Exception ex) { _status("保存失败：" + ex.Message); }
    }

    [RelayCommand]
    private async Task ChangeAvatarAsync()
    {
        var path = await _ui.PickFileAsync(true);
        if (path is null) return;
        try
        {
            using var image = ImageLoader.Load(path, 192) ?? throw new IOException("无法读取这张图片");
            var folder = AppPaths.DailyMedia(_store.DataDirectory, true, "Avatars");
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, "self-" + Guid.NewGuid().ToString("N") + ".png");
            image.Save(target);
            if (new FileInfo(target).Length > 131072) throw new IOException("头像过大，请选择较小的图片");
            _lan.Profile.AvatarPath = target;
            Avatar?.Dispose();
            Avatar = ImageLoader.Load(target, 192);
            await _lan.UpdateProfileAsync();
            _status("头像已更新");
        }
        catch (Exception ex) { _status("头像更新失败：" + ex.Message); }
    }

    [RelayCommand]
    private async Task SetThemeAsync(string theme)
    {
        Theme = theme;
        _lan.Profile.Theme = theme;
        _ui.SetTheme(theme);
        try { await _lan.UpdateProfileAsync(); }
        catch (Exception ex) { _status("主题保存失败：" + ex.Message); }
    }

    [RelayCommand] private void OpenDownloads() { Directory.CreateDirectory(DownloadDirectory); _ui.OpenFile(DownloadDirectory); }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(ManualAddress) || !int.TryParse(ManualPort, out var port) || port is < 1 or > 65535)
        { _status("请输入正确的 IP 地址和端口。"); return; }
        IsConnecting = true;
        try
        {
            var peer = await _lan.ConnectAsync(ManualAddress.Trim(), port);
            _status("已连接到 " + peer.Nickname + "，发送文件时即可选择这台电脑。");
        }
        catch (Exception ex) { _status("连接失败：" + ex.Message); }
        finally { IsConnecting = false; }
    }

    public void Dispose() { Avatar?.Dispose(); Avatar = null; }
}
