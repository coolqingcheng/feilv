using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using LanChat.Core.Services;
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
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var store = new IndexedFileStore(Program.Options.DataDirectory);
            var profile = store.InitialProfile;
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
            var open = new NativeMenuItem("打开飞驴"); open.Click += (_, _) => window.Restore(); menu.Items.Add(open);
            menu.Items.Add(new NativeMenuItemSeparator());
            var exit = new NativeMenuItem("退出"); exit.Click += async (_, _) => await ShutdownAsync();
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
            _tray = new TrayIcon { Icon = window.Icon, ToolTipText = "飞驴 · 局域网文件传输", Menu = menu, IsVisible = true };
            _tray.Clicked += (_, _) => window.Restore();
            TrayIcon.SetIcons(this, new TrayIcons { _tray });
            window.Opened += InitializeOnceAsync;
            async void InitializeOnceAsync(object? sender, EventArgs args)
            {
                window.Opened -= InitializeOnceAsync;
                await model.InitializeAsync();
                UpdateInstaller.SignalReady(Program.UpdateReadyPath);
                _ = model.Updates.RunBackgroundAsync();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
