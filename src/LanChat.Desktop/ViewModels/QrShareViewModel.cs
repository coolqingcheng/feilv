using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanChat.Core.Models;
using LanChat.Core.Services;
using LanChat.Desktop.Services;

namespace LanChat.Desktop.ViewModels;

public partial class QrShareViewModel(IQrHttpServer server, UserProfile profile, ILocalStore store, IUiServices ui) : ViewModelBase, IDisposable, IAsyncDisposable
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit))] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit))] private bool _isRunning;
    [ObservableProperty] private string _status = "点击二维码按钮即可开始分享";
    [ObservableProperty] private string _uploadDirectory = profile.QrUploadDirectory;
    [ObservableProperty] private string _downloadDirectory = profile.QrDownloadDirectory;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Url))] private string _selectedAddress = "";
    public ObservableCollection<string> Addresses { get; } = [];
    public string DefaultDirectory => Path.Combine(store.DataDirectory, "qr-http");
    public bool CanEdit => !IsBusy;
    public string Url => IsRunning && SelectedAddress.Length > 0 ? $"http://{SelectedAddress}:{server.Port}{server.BasePath}" : "";
    public string EffectiveUploadDirectory => Resolve(UploadDirectory);
    public string EffectiveDownloadDirectory => Resolve(DownloadDirectory);
    private bool _disposed;

    private string Resolve(string path) => string.IsNullOrWhiteSpace(path) ? DefaultDirectory : Path.GetFullPath(Path.Combine(store.DataDirectory, path.Trim()));
    partial void OnUploadDirectoryChanged(string value) => OnPropertyChanged(nameof(EffectiveUploadDirectory));
    partial void OnDownloadDirectoryChanged(string value) => OnPropertyChanged(nameof(EffectiveDownloadDirectory));

    private void RefreshAddresses()
    {
        var previous = SelectedAddress;
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .OrderBy(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 ? 0 : 1)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Select(a => a.Address.ToString()).Distinct().ToList();
        Addresses.Clear(); foreach (var address in addresses) Addresses.Add(address);
        if (Addresses.Count == 0) Addresses.Add("127.0.0.1");
        SelectedAddress = Addresses.Contains(previous) ? previous : Addresses[0];
    }

    [RelayCommand]
    public async Task StartAsync()
    {
        if (_disposed || IsBusy || IsRunning) return;
        IsBusy = true;
        try
        {
            RefreshAddresses();
            await server.StartAsync(EffectiveUploadDirectory, EffectiveDownloadDirectory);
            IsRunning = true; OnPropertyChanged(nameof(Url));
            Status = SelectedAddress == "127.0.0.1" ? "服务已启动，连接局域网后重新启动分享。" : "服务运行中 · 手机扫码即可上传和下载";
        }
        catch (Exception ex) { Status = "启动分享失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await server.StopAsync(); IsRunning = false; OnPropertyChanged(nameof(Url)); Status = "已停止分享，二维码链接已失效。"; }
        catch (Exception ex) { Status = "停止分享失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ApplyDirectoriesAsync()
    {
        if (_disposed || IsBusy) return;
        IsBusy = true;
        try
        {
            var upload = EffectiveUploadDirectory; var download = EffectiveDownloadDirectory;
            await Task.Run(() => { Directory.CreateDirectory(upload); Directory.CreateDirectory(download); });
            var wasRunning = IsRunning;
            if (wasRunning) { await server.StopAsync(); IsRunning = false; OnPropertyChanged(nameof(Url)); }
            var previousUpload = profile.QrUploadDirectory; var previousDownload = profile.QrDownloadDirectory;
            profile.QrUploadDirectory = string.IsNullOrWhiteSpace(UploadDirectory) ? "" : upload;
            profile.QrDownloadDirectory = string.IsNullOrWhiteSpace(DownloadDirectory) ? "" : download;
            try { await store.SaveProfileAsync(profile); }
            catch { profile.QrUploadDirectory = previousUpload; profile.QrDownloadDirectory = previousDownload; throw; }
            if (wasRunning)
            {
                RefreshAddresses(); await server.StartAsync(upload, download); IsRunning = true; OnPropertyChanged(nameof(Url));
            }
            Status = wasRunning ? "目录已应用，分享链接已更新，请重新扫码。" : "目录设置已保存。";
        }
        catch (Exception ex) { Status = "保存目录失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand] private async Task ChooseUploadAsync() { if (await ui.PickFolderAsync() is { } path) UploadDirectory = path; }
    [RelayCommand] private async Task ChooseDownloadAsync() { if (await ui.PickFolderAsync() is { } path) DownloadDirectory = path; }
    [RelayCommand] private async Task CopyLinkAsync()
    {
        if (!IsRunning) return;
        try { await ui.CopyTextAsync(Url); Status = "访问地址已复制。"; } catch (Exception ex) { Status = "复制失败：" + ex.Message; }
    }
    [RelayCommand] private void OpenBrowser() { if (IsRunning) try { ui.OpenFile(Url); } catch (Exception ex) { Status = "打开失败：" + ex.Message; } }

    public void Dispose() { _disposed = true; server.Dispose(); }
    public async ValueTask DisposeAsync() { _disposed = true; await server.DisposeAsync(); }
}
