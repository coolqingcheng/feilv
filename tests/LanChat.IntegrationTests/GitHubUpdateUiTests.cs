using Avalonia;
using Avalonia.Styling;
using Avalonia.Controls;
using LanChat.Core.Models;
using LanChat.Core.Services;
using LanChat.Desktop.ViewModels;
using LanChat.Desktop.Views;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static void RunGitHubUpdateUiTests(MainWindow main)
    {
        var service = new DeferredUpdateService();
        var transferring = true; var installed = false;
        using var model = new AppUpdatesViewModel(service, new UserProfile(), () => transferring);
        var notifications = 0;
        var popup = new UpdateReadyWindow { DataContext = model };
        model.UpdateReady += () => { notifications++; popup.Show(); };
        model.InstallRequested += _ => { installed = true; return Task.CompletedTask; };
        main.WindowState = WindowState.Minimized;
        var check = model.CheckCommand.ExecuteAsync(null);
        Check(model.IsBusy && !model.IsReady && notifications == 0 && !model.InstallCommand.CanExecute(null), "a discovered release starts downloading without displaying an install prompt or enabling installation");
        service.CompleteDownload(); Pump(check);
        Check(model.IsReady && notifications == 1 && popup.IsVisible && !main.IsVisible && !popup.ShowActivated && !popup.ShowInTaskbar,
            "only a verified download produces an update prompt, without restoring the hidden main window or requesting focus");
        Pump(model.InstallCommand.ExecuteAsync(null));
        Check(!installed && model.Status.Contains("传输", StringComparison.Ordinal), "installing waits for active file transfers instead of interrupting them");
        transferring = false;
        Pump(model.InstallCommand.ExecuteAsync(null));
        Check(installed, "the explicit update action invokes installation only after the user confirms");
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Application.Current!.RequestedThemeVariant = theme;
            Render(popup, "update-ready-" + (theme == ThemeVariant.Dark ? "dark" : "light") + ".png");
        }
        popup.Close();
        var failedService = new DeferredUpdateService();
        using var failed = new AppUpdatesViewModel(failedService, new UserProfile(), () => false);
        failed.UpdateReady += () => notifications++;
        var failure = failed.CheckCommand.ExecuteAsync(null);
        failedService.FailDownload(); Pump(failure);
        Check(!failed.IsReady && notifications == 1 && !failed.InstallCommand.CanExecute(null), "a failed digest check never prompts or enables application replacement");
        main.Restore();
    }

    private sealed class DeferredUpdateService : IAppUpdateService
    {
        private readonly TaskCompletionSource<PreparedAppUpdate> _download = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AppRelease _release = new("9.8.7", "win-x64", AppReleases.AssetName("9.8.7", "win-x64"), new string('a', 64), 100);
        public Task<AppRelease?> CheckAsync(Version version, CancellationToken ct = default) => Task.FromResult<AppRelease?>(_release);
        public Task<PreparedAppUpdate> DownloadAsync(AppRelease release, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken ct = default) => _download.Task.WaitAsync(ct);
        public void CompleteDownload() => _download.SetResult(new(_release, "unused", new("9.8.7", "win-x64", "feilv.exe", [])));
        public void FailDownload() => _download.SetException(new InvalidDataException("SHA-256 mismatch"));
        public void Dispose() { }
    }
}
