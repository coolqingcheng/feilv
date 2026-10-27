using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanChat.Core.Models;
using LanChat.Core.Services;
using LanChat.Desktop.Services;

namespace LanChat.Desktop.ViewModels;

public partial class AppUpdatesViewModel(IAppUpdateService service, UserProfile profile, Func<bool> hasActiveTransfers) : ViewModelBase, IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private PreparedAppUpdate? _prepared;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CheckCommand)), NotifyCanExecuteChangedFor(nameof(InstallCommand))] private bool _isBusy;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(InstallCommand))] private bool _isReady;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(InstallCommand)), NotifyCanExecuteChangedFor(nameof(CheckCommand))] private bool _isInstalling;
    [ObservableProperty] private string _status = "检查新版本，下载并校验后提示更新。";
    [ObservableProperty] private string _readyVersion = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ReleaseNotesTitle))] private string _availableVersion = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasReleaseNotes))] private string _releaseNotes = "";
    [ObservableProperty] private double _progress;
    public string ReadyMessage => $"飞驴 {ReadyVersion} 已下载并通过校验";
    public string ReleaseNotesTitle => $"v{AvailableVersion} 更新内容";
    public bool HasReleaseNotes => ReleaseNotes.Length > 0;
    public event Action? UpdateReady;
    public event Func<PreparedAppUpdate, Task>? InstallRequested;
    private bool CanCheck => !IsBusy && !IsInstalling;
    private bool CanInstall => IsReady && !IsInstalling && !IsBusy;

    public async Task RunBackgroundAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), _stop.Token);
            while (!_stop.IsCancellationRequested)
            {
                if (profile.AutoDownloadUpdates && CanCheck) await CheckAsync();
                await Task.Delay(TimeSpan.FromMinutes(30), _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        if (!CanCheck) return;
        IsBusy = true; Progress = 0;
        try
        {
            Status = "正在检查更新…";
            var release = await service.CheckAsync(typeof(AppUpdatesViewModel).Assembly.GetName().Version!, _stop.Token);
            if (release is null) { Status = "当前已是最新正式版。"; return; }
            AvailableVersion = release.Version;
            ReleaseNotes = string.IsNullOrWhiteSpace(release.ReleaseNotes) ? "此版本未提供更新说明。" : release.ReleaseNotes;
            if (_prepared is not null && AppReleases.SamePackage(_prepared.Release, release))
            {
                _prepared = _prepared with { Release = release };
                Status = ReadyMessage; return;
            }
            _prepared = null; IsReady = false; ReadyVersion = "";
            OnPropertyChanged(nameof(ReadyMessage));
            Status = "发现新版本，正在后台下载并校验…";
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                if (_stop.IsCancellationRequested || !IsBusy) return;
                Progress = value.Total == 0 ? 0 : value.Received * 100d / value.Total;
                Status = $"正在下载 {release.Version} · {Progress:F0}% · {ImageLoader.TransferSpeed(value.BytesPerSecond)}";
            });
            _prepared = await service.DownloadAsync(release, progress, _stop.Token);
            ReadyVersion = release.Version; OnPropertyChanged(nameof(ReadyMessage));
            Progress = 100; IsReady = true; Status = ReadyMessage + "，可以立即更新或稍后处理。";
            UpdateReady?.Invoke();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Status = "更新未就绪：" + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (_prepared is null || !CanInstall) return;
        if (hasActiveTransfers()) { Status = "文件正在传输，请完成后再更新。"; return; }
        if (InstallRequested is null) { Status = "请在正式客户端中安装更新。"; return; }
        IsInstalling = true;
        try { Status = "正在准备更新，飞驴即将重启…"; await InstallRequested(_prepared); }
        catch (Exception ex) { Status = "无法开始更新：" + ex.Message; }
        finally { IsInstalling = false; }
    }

    public void Dispose() { _stop.Cancel(); service.Dispose(); }
}
