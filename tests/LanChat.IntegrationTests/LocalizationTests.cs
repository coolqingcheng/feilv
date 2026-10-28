using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LanChat.Core.Localization;
using LanChat.Core.Models;
using LanChat.Desktop.ViewModels;
using LanChat.Desktop.Views;
using LanChat.Infrastructure.Networking;
using LanChat.Infrastructure.Storage;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static void RunLocalizationTests()
    {
        Check(Localizer.ResolveLanguage("System", CultureInfo.GetCultureInfo("zh-TW")) == "zh-CN"
            && Localizer.ResolveLanguage("System", CultureInfo.GetCultureInfo("en-GB")) == "en-US"
            && Localizer.ResolveLanguage("System", CultureInfo.GetCultureInfo("fr-FR")) == "en-US",
            "system language selects Chinese for Chinese locales and English otherwise");
        Check(Localizer.ResolveLanguage("en-US", CultureInfo.GetCultureInfo("zh-CN")) == "en-US"
            && Localizer.ResolveLanguage("zh-CN", CultureInfo.GetCultureInfo("en-US")) == "zh-CN", "explicit language overrides the OS locale");
        var root = Path.Combine(TestRoot, "language-config");
        var config = XmlConfiguration.Load(root);
        Check(config.Profile.Language == "System", "first launch follows the system language");
        config.Profile.Language = "en-US"; config.Save(root);
        Check(XmlConfiguration.Load(root).Profile.Language == "en-US", "English preference persists in Data/config.xml");
        var xml = XDocument.Load(AppPaths.Configuration(root));
        xml.Root!.Element("Profile")!.Element("Language")!.Remove(); xml.Save(AppPaths.Configuration(root));
        Check(XmlConfiguration.Load(root).Profile.Language == "System", "older XML without a language preference follows the system");
        xml.Root.Element("Profile")!.Add(new XElement("Language", "unsupported")); xml.Save(AppPaths.Configuration(root));
        Check(XmlConfiguration.Load(root).Profile.Language == "System", "unknown language values safely fall back to the system");

        Localizer.SetPreference("en-US");
        Check(Localizer.Translate("接收失败：文件 SHA-256 校验失败。") == "Receive failed: File SHA-256 verification failed.",
            "nested backend errors are localized at the presentation boundary");
        Check(Localizer.Translate("来自 设置 {name}") == "From 设置 {name}"
            && Localizer.Translate("发给 English 发送中") == "To English 发送中", "device names stay opaque even when they contain translated words or braces");
        Check(Localizer.Translate("发送中 · 12 MB/s\n设置 {state} · 文件\n附件.txt") == "Sending · 12 MB/s\n设置 {state} · 文件\n附件.txt",
            "transfer states translate without modifying Unicode filenames, newlines or template-shaped names");
        Check(Localizer.Translate("已向 2 台电脑发送 3 项接收请求。 1 项失败：设备已离线")
            == "Sent 3 file offers to 2 computers. 1 failed: Device is offline", "batch status translates counts and nested errors without dropping failed items");
        Check(Localizer.Translate("正在下载 1.4.8 · 42% · 5.1 MB/s") == "Downloading 1.4.8 · 42% · 5.1 MB/s",
            "update version, percentage and transfer speed remain intact");
        Localizer.SetPreference("System");
    }

    private static void RunLocalizationUiTests()
    {
        var store = new IndexedFileStore(Path.Combine(TestRoot, "localized-ui"));
        var profile = new UserProfile { Nickname = "我的电脑", AutoReceive = false };
        var lan = new LanService(store, profile, new NodeOptions { EnableDiscovery = false });
        var ui = new TestUiServices();
        using var model = new MainWindowViewModel(lan, store, ui);
        var peer = new PeerViewModel(new Peer { Id = Guid.NewGuid().ToString("N"), Nickname = "设置 {name}", Address = "192.168.1.20", Port = 45832, Fingerprint = new string('A', 64) });
        model.Inbox.UpdatePeer(peer);
        peer.ApplyTransfer(new TransferUpdate("translation", "发送中 {file}.txt", 42, 100, "发送中") { IsUpload = true, BytesPerSecond = 1024 });
        var message = new ChatMessage { PeerId = peer.Id, SenderId = peer.Id, PeerNickname = peer.Nickname, PeerAddress = peer.Address, Kind = MessageKind.File,
            FileName = "文件 {name}.txt", FileSize = 1024, AttachmentId = Guid.NewGuid().ToString("N"), Delivery = DeliveryState.Received };
        model.Inbox.Ingest(message); model.Incoming.Enqueue(message);
        model.Shares.RemoteEntries.Add(new SharedEntryViewModel(new SharedEntry("root", "文件夹", "文件夹", true, 0)));
        var main = new MainWindow { DataContext = model };
        var prompt = new ReceiveFileWindow { DataContext = model.Incoming };
        main.Show(); prompt.Show();
        try
        {
            Pump(model.Settings.SetLanguageCommand.ExecuteAsync("en-US"));
            Check(Localizer.Language == "en-US" && XmlConfiguration.Load(store.DataDirectory).Profile.Language == "en-US",
                "language command applies immediately and saves the preference without restarting");
            var inbox = main.GetVisualDescendants().OfType<FileInboxView>().Single();
            Check(main.Title == "Feilv · LAN file transfer v" + AppReleases.CurrentVersion && inbox.FindControl<TabControl>("HomeTabs")!.Items.OfType<TabItem>().Select(x => x.Header?.ToString()).SequenceEqual(new[] { "Devices", "History" }),
                "existing window titles and tab resources switch to English live");
            Check(inbox.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "Sending") && peer.TransferStatus == "发送中",
                "device transfer status translates while the underlying protocol state stays invariant");
            Check(prompt.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "Receive this file?")
                && prompt.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "From 设置 {name}")
                && prompt.FindControl<Button>("AcceptReceiveButton")!.Content?.ToString() == "Receive"
                && prompt.FindControl<Button>("RejectReceiveButton")!.Content?.ToString() == "Decline",
                "an already-open receive prompt translates actions and preserves the sender's actual name");
            Check(model.Inbox.Files.Single().FileName == message.FileName && model.Settings.Nickname == "我的电脑", "switching language preserves filenames and the local nickname");
            model.Page = "Settings";
            main.UpdateLayout(); Pump(Task.Delay(30)); main.UpdateLayout();
            var settings = main.GetVisualDescendants().OfType<SettingsView>().Single();
            Check(settings.FindControl<TextBlock>("LanguageDescription")!.Text == "Current language: English"
                && settings.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "Software updates"), "settings and its language description switch together");
            var notesService = new DeferredUpdateService { Notes = "发送中" };
            using var update = new AppUpdatesViewModel(notesService, profile, () => false);
            var updateCheck = update.CheckCommand.ExecuteAsync(null);
            Check(update.ReleaseNotesText == "发送中", "authored release notes stay opaque even when they match a translated status");
            notesService.CompleteDownload(); Pump(updateCheck);
            notesService.Notes = ""; Pump(update.CheckCommand.ExecuteAsync(null));
            Check(update.ReleaseNotesText == "No release notes provided.", "missing release notes show an English fallback");
            notesService.Notes = "此版本未提供更新说明。"; Pump(update.CheckCommand.ExecuteAsync(null));
            Check(update.ReleaseNotesText == notesService.Notes, "authored notes matching the fallback text are still preserved");
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (var width in new[] { 360d, 420d })
            {
                Application.Current!.RequestedThemeVariant = theme; main.Width = width;
                foreach (var page in new[] { "Files", "Settings", "Shares", "Qr" })
                {
                    model.Page = page; main.UpdateLayout(); Pump(Task.Delay(30)); main.UpdateLayout();
                    Check(main.GetVisualDescendants().OfType<ScrollViewer>().Where(x => x.IsEffectivelyVisible)
                        .All(x => x.Extent.Width <= x.Viewport.Width + 1), $"English {page} fits {width}px in {theme} without horizontal scrolling");
                }
            }
            model.Page = "Files"; model.Inbox.SelectedTab = 1; main.UpdateLayout(); Pump(Task.Delay(30));
            Check(inbox.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "From 设置 {name}")
                && inbox.GetVisualDescendants().OfType<Button>().Any(x => x.Content?.ToString() == "Receive"), "virtualized history rows display translated source labels and actions");
            var progressModel = new UpdateProgressViewModel { Status = "正在安装更新，请稍候…" };
            var progressWindow = new UpdateProgressWindow { DataContext = progressModel }; progressWindow.Show();
            Check(progressWindow.Title == "Feilv · Updating" && progressWindow.FindControl<TextBlock>("UpdateProgressStatus")!.Text == "Installing update, please wait…",
                "independent update helper uses the selected language from its first frame");
            progressWindow.Close();
            Pump(model.Settings.SetLanguageCommand.ExecuteAsync("zh-CN"));
            Check(main.Title == "飞驴 · 局域网文件传输 v" + AppReleases.CurrentVersion && prompt.FindControl<Button>("AcceptReceiveButton")!.Content?.ToString() == "接收"
                && prompt.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "是否接收这个文件？"), "switching back restores Chinese static and computed labels in existing windows");
            Pump(model.Settings.SetLanguageCommand.ExecuteAsync("System"));
            Check(Localizer.Language == "zh-CN" && XmlConfiguration.Load(store.DataDirectory).Profile.Language == "System", "system option restores and persists the original OS language");
            var notifications = 0; model.PropertyChanged += (_, _) => notifications++;
            model.Dispose(); var afterDispose = notifications;
            Localizer.SetPreference("en-US");
            Check(notifications == afterDispose, "disposed view models unsubscribe from language changes");
        }
        finally
        {
            Localizer.SetPreference("System");
            prompt.AllowClose = true; prompt.Close(); main.AllowClose = true; main.Close();
            peer.Dispose(); Pump(lan.DisposeAsync().AsTask());
        }
    }
}
