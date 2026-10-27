using Avalonia;
using Avalonia.Styling;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LanChat.Core.Models;
using LanChat.Core.Services;
using LanChat.Desktop.ViewModels;
using LanChat.Desktop.Views;
using LanChat.Infrastructure.Networking;
using LanChat.Infrastructure.Storage;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static void RunGitHubUpdateUiTests(MainWindow main, LanService node, IndexedFileStore store, TestUiServices ui)
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
        Check(model.ReleaseNotes == service.Notes && model.ReleaseNotesTitle.Contains("9.8.7", StringComparison.Ordinal), "update details become available while the verified package is downloading");
        service.CompleteDownload(); Pump(check);
        Check(model.IsReady && notifications == 1 && popup.IsVisible && !main.IsVisible && !popup.ShowActivated && !popup.ShowInTaskbar,
            "only a verified download produces an update prompt, without restoring the hidden main window or requesting focus");
        Check(popup.FindControl<TextBlock>("UpdateReleaseNotes")!.Text == service.Notes, "the verified update prompt displays the release description");
        service.Notes += "\n• 补充的更新说明。";
        Pump(model.CheckCommand.ExecuteAsync(null));
        Check(model.ReleaseNotes == service.Notes && notifications == 1 && model.IsReady, "refreshing release notes for the same prepared package preserves readiness without another prompt");
        Pump(model.InstallCommand.ExecuteAsync(null));
        Check(!installed && model.Status.Contains("传输", StringComparison.Ordinal), "installing waits for active file transfers instead of interrupting them");
        transferring = false;
        Pump(model.InstallCommand.ExecuteAsync(null));
        Check(installed, "the explicit update action invokes installation only after the user confirms");
        using var settingsModel = new SettingsViewModel(node, store, ui, _ => { }, model);
        var settingsView = new SettingsView { DataContext = settingsModel };
        var settingsWindow = new Window { Width = 360, Height = 660, Content = settingsView };
        settingsWindow.Show();
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Application.Current!.RequestedThemeVariant = theme;
            Render(popup, "update-ready-" + (theme == ThemeVariant.Dark ? "dark" : "light") + ".png");
            var notesScroll = popup.FindControl<ScrollViewer>("ReleaseNotesScroll")!;
            Check(notesScroll.Extent.Height > notesScroll.Viewport.Height && notesScroll.Extent.Width <= notesScroll.Viewport.Width + 1,
                theme + " update notes scroll vertically and wrap without pushing installation actions out of view");
            settingsWindow.UpdateLayout(); Pump(Task.Delay(30)); settingsWindow.UpdateLayout();
            var settingsScroll = settingsView.FindControl<ScrollViewer>("SettingsScroll")!;
            settingsScroll.Offset = new Vector(0, settingsScroll.Extent.Height);
            Render(settingsWindow, "update-settings-" + (theme == ThemeVariant.Dark ? "dark" : "light") + ".png");
            Check(settingsView.FindControl<TextBlock>("SettingsReleaseNotes")!.Text == service.Notes
                && settingsView.GetVisualDescendants().OfType<ScrollViewer>().All(x => x.Extent.Width <= x.Viewport.Width + 1),
                theme + " settings display the same update details in a 360 px window without horizontal overflow");
        }
        settingsWindow.Close();
        popup.Close();
        var progressModel = new UpdateProgressViewModel();
        var progressWindow = new UpdateProgressWindow { DataContext = progressModel }; progressWindow.Show();
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Application.Current!.RequestedThemeVariant = theme;
            progressModel.Status = "正在安装更新，请稍候…";
            Render(progressWindow, "update-installing-" + (theme == ThemeVariant.Dark ? "dark" : "light") + ".png");
            Check(progressWindow.FindControl<TextBlock>("UpdateProgressStatus")!.Text == progressModel.Status
                && !progressWindow.ShowActivated && !progressWindow.ShowInTaskbar && !main.IsVisible,
                theme + " independent installer progress stays visible without restoring the hidden main client");
        }
        progressWindow.Close();
        var failedService = new DeferredUpdateService { Notes = "" };
        using var failed = new AppUpdatesViewModel(failedService, new UserProfile(), () => false);
        failed.UpdateReady += () => notifications++;
        var failure = failed.CheckCommand.ExecuteAsync(null);
        Check(failed.HasReleaseNotes && failed.ReleaseNotes == "此版本未提供更新说明。", "releases without descriptions show a clear fallback without blocking download");
        failedService.FailDownload(); Pump(failure);
        Check(!failed.IsReady && notifications == 1 && !failed.InstallCommand.CanExecute(null), "a failed digest check never prompts or enables application replacement");
        main.Restore();
    }

    private sealed class DeferredUpdateService : IAppUpdateService
    {
        private readonly TaskCompletionSource<PreparedAppUpdate> _download = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AppRelease _release = new("9.8.7", "win-x64", AppReleases.AssetName("9.8.7", "win-x64"), new string('a', 64), 100);
        public string Notes { get; set; } = "• 在线设备显示版本号，便于核对各台电脑的版本。\n• 设置和更新提示中可查看本次更新内容。\n• 较长的更新说明可以滚动浏览。\n• 下载校验完成后再确认安装。\n\n" + string.Join("\n", Enumerable.Range(1, 12).Select(i => $"• 改进 {i}：优化文件传输和界面交互体验。"));
        public Task<AppRelease?> CheckAsync(Version version, CancellationToken ct = default) => Task.FromResult<AppRelease?>(_release with { ReleaseNotes = Notes });
        public Task<PreparedAppUpdate> DownloadAsync(AppRelease release, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken ct = default) => _download.Task.WaitAsync(ct);
        public void CompleteDownload() => _download.SetResult(new(_release, "unused", new("9.8.7", "win-x64", "feilv.exe", [])));
        public void FailDownload() => _download.SetException(new InvalidDataException("SHA-256 mismatch"));
        public void Dispose() { }
    }
}
