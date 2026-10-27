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
            var ui = new DesktopUiServices(() => window);
            var model = new MainWindowViewModel(_lan, store, ui);
            window.DataContext = model;
            using (var icon = AssetLoader.Open(new Uri("avares://LanChat.Desktop/Assets/lanchat.ico"))) window.Icon = new WindowIcon(icon);
            desktop.MainWindow = window;
            var receivePrompt = new ReceiveFileWindow { DataContext = model.Incoming };
            var receiveNotifications = new ReceivePromptController(window, receivePrompt, model.Incoming, profile);
            var updatePrompt = new UpdateReadyWindow { DataContext = model.Updates };
            var shuttingDown = false;
            model.Updates.UpdateReady += () => updatePrompt.Show();
            model.Updates.InstallRequested += async package =>
            {
                await UpdateInstaller.StartAsync(package);
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
                updatePrompt.Close();
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
            window.Opened += InitializeOnceAsync;
            async void InitializeOnceAsync(object? sender, EventArgs args)
            {
                window.Opened -= InitializeOnceAsync;
                await model.InitializeAsync();
                UpdateInstaller.SignalReady(Program.UpdateReadyPath);
                if (Program.IsSmokeTest)
                {
                    if (model.Status.StartsWith("启动失败", StringComparison.Ordinal)) Environment.ExitCode = 1;
                    await Task.Delay(500);
                    await ShutdownAsync();
                    return;
                }
                _ = model.Updates.RunBackgroundAsync();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
