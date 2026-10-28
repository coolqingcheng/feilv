using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using LanChat.Core.Services;
using LanChat.Core.Localization;
using LanChat.Desktop.Services;
using LanChat.Desktop.ViewModels;
using LanChat.Desktop.Views;
using LanChat.Infrastructure.Networking;
using LanChat.Infrastructure.Storage;
using LanChat.Infrastructure.Updates;

namespace LanChat.Desktop;

public partial class App : Application
{
    private ILanService? _lan;
    private TrayIcon? _tray;
    private NativeMenuItem? _openMenuItem;
    private NativeMenuItem? _exitMenuItem;
    private readonly ResourceDictionary _translations = new();
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Resources.MergedDictionaries.Add(_translations);
        Resources["Translate"] = new TranslationConverter();
        RefreshTranslations();
        Localizer.Changed += RefreshTranslations;
    }

    private void RefreshTranslations()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(RefreshTranslations); return; }
        foreach (var (key, value) in Localizer.Catalog) _translations["Loc." + key] = Localizer.Text(value[0]);
        if (_openMenuItem is not null) _openMenuItem.Header = Localizer.Text("打开飞驴");
        if (_exitMenuItem is not null) _exitMenuItem.Header = Localizer.Text("退出");
        if (_tray is not null) _tray.ToolTipText = Localizer.Text("飞驴 · 局域网文件传输");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (Program.UpdatePlanPath is { } plan)
            {
                var progress = new UpdateProgressViewModel();
                var progressWindow = new UpdateProgressWindow { DataContext = progress };
                desktop.MainWindow = progressWindow;
                progressWindow.Opened += async (_, _) =>
                {
                    Environment.ExitCode = await UpdateInstaller.RunHelperAsync(plan, new Progress<string>(value => progress.Status = value));
                    desktop.Shutdown(Environment.ExitCode);
                };
                base.OnFrameworkInitializationCompleted();
                return;
            }
            var store = new IndexedFileStore(Program.Options.DataDirectory);
            var profile = store.InitialProfile;
            Localizer.SetPreference(profile.Language);
            RequestedThemeVariant = profile.Theme switch { "Light" => ThemeVariant.Light, "Dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
            _lan = new LanService(store, profile, Program.Options);
            var window = new MainWindow();
            if (Program.UpdateStartMaximized) window.WindowState = WindowState.Maximized;
            var ui = new DesktopUiServices(() => window);
            var model = new MainWindowViewModel(_lan, store, ui);
            window.DataContext = model;
            using (var icon = AssetLoader.Open(new Uri("avares://LanChat.Desktop/Assets/lanchat.ico"))) window.Icon = new WindowIcon(icon);
            if (!Program.UpdateStartHidden) desktop.MainWindow = window;
            var receivePrompt = new ReceiveFileWindow { DataContext = model.Incoming };
            var receiveNotifications = new ReceivePromptController(window, receivePrompt, model.Incoming, profile);
            var updatePrompt = new UpdateReadyWindow { DataContext = model.Updates };
            var updateNotification = new NotificationService();
            var peerShareWindows = new Dictionary<string, RemoteSharesWindow>();
            model.BrowsePeerSharesRequested += peer =>
            {
                if (peerShareWindows.TryGetValue(peer.Id, out var existing)) { existing.Activate(); return; }
                var shareWindow = new RemoteSharesWindow();
                var shareModel = new SharesViewModel(_lan!, new DesktopUiServices(() => shareWindow), _ => { });
                shareWindow.DataContext = shareModel;
                peerShareWindows.Add(peer.Id, shareWindow);
                shareWindow.Closed += (_, _) => { shareModel.Dispose(); peerShareWindows.Remove(peer.Id); };
                shareWindow.Show(window);
                shareModel.Peer = peer;
            };
            var shuttingDown = false;
            var initialized = false;
            var reminderOpen = false;
            ProfileReminderWindow? reminderWindow = null;
            var reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            reminderTimer.Tick += (_, _) => _ = ShowProfileReminderAsync();
            window.PropertyChanged += (_, e) => { if (e.Property == Window.IsVisibleProperty && window.IsVisible) _ = ShowProfileReminderAsync(); };
            async Task ShowProfileReminderAsync()
            {
                if (!initialized || shuttingDown || reminderOpen || !window.IsVisible || Program.IsSmokeTest
                    || !ProfileReminderViewModel.IsDue(profile, DateTimeOffset.UtcNow, Environment.UserName)) return;
                reminderOpen = true;
                try
                {
                    reminderWindow = new ProfileReminderWindow();
                    using var reminder = new ProfileReminderViewModel(_lan!, store, new DesktopUiServices(() => reminderWindow));
                    reminderWindow.DataContext = reminder;
                    reminder.Saved += () => { model.Settings.RefreshProfile(); reminderWindow.Close(); };
                    var dialog = reminderWindow.ShowDialog(window);
                    try { await reminder.MarkShownAsync(DateTimeOffset.UtcNow); }
                    catch { reminderWindow.Close(); throw; }
                    await dialog;
                }
                catch (Exception ex) { model.Status = "设置提示失败：" + ex.Message; }
                finally { reminderWindow = null; reminderOpen = false; }
            }
            model.Updates.UpdateReady += () => updatePrompt.Show();
            model.Updates.InstallRequested += async package =>
            {
                await UpdateInstaller.StartAsync(package, startHidden: !window.IsVisible || window.WindowState == WindowState.Minimized,
                    startMaximized: window.WindowState == WindowState.Maximized);
                await ShutdownAsync();
            };
            Program.Instance?.SetActivationHandler(() => Dispatcher.UIThread.Post(window.Restore));
            var menu = new NativeMenu();
            var open = _openMenuItem = new NativeMenuItem(Localizer.Text("打开飞驴")); open.Click += (_, _) => window.Restore(); menu.Items.Add(open);
            menu.Items.Add(new NativeMenuItemSeparator());
            var exit = _exitMenuItem = new NativeMenuItem(Localizer.Text("退出")); exit.Click += async (_, _) => await ShutdownAsync();
            async Task ShutdownAsync()
            {
                if (shuttingDown) return;
                shuttingDown = true;
                reminderTimer.Stop();
                reminderWindow?.Close();
                updatePrompt.Close();
                updateNotification.Close();
                foreach (var shareWindow in peerShareWindows.Values.ToArray()) shareWindow.Close();
                receiveNotifications.Dispose();
                receivePrompt.AllowClose = true; receivePrompt.Close();
                await model.QrShare.DisposeAsync(); model.Dispose();
                _tray!.IsVisible = false;
                if (_lan is not null) await _lan.DisposeAsync();
                window.AllowClose = true; desktop.Shutdown();
            }
            menu.Items.Add(exit);
            _tray = new TrayIcon { Icon = window.Icon, ToolTipText = Localizer.Text("飞驴 · 局域网文件传输"), Menu = menu, IsVisible = true };
            _tray.Clicked += (_, _) => window.Restore();
            TrayIcon.SetIcons(this, new TrayIcons { _tray });
            if (Program.UpdateStartHidden) Dispatcher.UIThread.Post(() => _ = InitializeOnceAsync());
            else window.Opened += OnWindowOpened;
            async void OnWindowOpened(object? sender, EventArgs args)
            {
                window.Opened -= OnWindowOpened;
                await InitializeOnceAsync();
            }
            async Task InitializeOnceAsync()
            {
                await model.InitializeAsync();
                UpdateInstaller.SignalReady(Program.UpdateReadyPath);
                if (Program.IsSmokeTest)
                {
                    if (model.Status.StartsWith("启动失败", StringComparison.Ordinal)) Environment.ExitCode = 1;
                    await Task.Delay(500);
                    await ShutdownAsync();
                    return;
                }
                initialized = true;
                reminderTimer.Start();
                _ = ShowProfileReminderAsync();
                if (Program.UpdateReadyPath is not null)
                    updateNotification.Show(Localizer.Text("更新成功"), Localizer.Text("飞驴已更新并重新启动。"), () => { });
                _ = model.Updates.RunBackgroundAsync();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
