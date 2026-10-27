using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using LanChat.Core.Models;
using LanChat.Desktop;
using LanChat.Desktop.Services;
using LanChat.Desktop.ViewModels;
using LanChat.Desktop.Views;
using LanChat.Infrastructure.Files;
using LanChat.Infrastructure.Networking;
using LanChat.Infrastructure.Storage;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static int _checks;
    private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "LanChat-tests-" + Guid.NewGuid().ToString("N"));

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--instance-probe", ..]) return SingleInstanceTests.RunProbe(args);
        if (args is ["--presence-probe", ..]) return PresenceTests.RunProbeAsync(args).GetAwaiter().GetResult();
        if (args is ["--qr-http-preview", ..]) return RunQrHttpPreviewAsync(args).GetAwaiter().GetResult();
        try
        {
            Directory.CreateDirectory(TestRoot);
            SingleInstanceTests.RunAsync(TestRoot, Check).GetAwaiter().GetResult();
            RunFileHistoryStorageTestsAsync().GetAwaiter().GetResult();
            RunStartupConfigurationTests();
            RunQrHttpTestsAsync().GetAwaiter().GetResult();
            RunGitHubUpdateTestsAsync().GetAwaiter().GetResult();
            RunUpdateHelperTestsAsync().GetAwaiter().GetResult();
            PresenceTests.RunAsync(TestRoot, Check).GetAwaiter().GetResult();
            RunNetworkTestsAsync().GetAwaiter().GetResult();
            if (!args.Contains("--no-ui")) RunUiTests();
            Console.WriteLine($"PASS: {_checks} checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (Directory.Exists(TestRoot)) Directory.Delete(TestRoot, true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        _checks++; Console.WriteLine("PASS: " + message);
    }

    private static async Task RunNetworkTestsAsync()
    {
        var storeA = new IndexedFileStore(Path.Combine(TestRoot, "alice"));
        var storeB = new IndexedFileStore(Path.Combine(TestRoot, "bob"));
        var profileA = await storeA.LoadProfileAsync(); profileA.Nickname = "Alice 测试";
        var profileB = await storeB.LoadProfileAsync(); profileB.Nickname = "Bob 测试";


        await storeA.SaveProfileAsync(profileA); await storeB.SaveProfileAsync(profileB);
        Check((await new IndexedFileStore(storeA.DataDirectory).LoadProfileAsync()).Id == profileA.Id, "stable persisted identity");
        await storeA.LoadFileHistoryAsync();
        Check(System.Text.Encoding.ASCII.GetString((await File.ReadAllBytesAsync(storeA.StoragePath))[..16]) == "FEILUDAT00000001" && File.Exists(storeA.IndexPath), "history uses a metadata storage file and a separate index");
        Check(File.Exists(AppPaths.Configuration(storeA.DataDirectory)) && !File.Exists(Path.Combine(storeA.DataDirectory, "config.xml")), "single XML configuration lives under Data");
        using var portProbe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var discoveryPort = ((IPEndPoint)portProbe.Client.LocalEndPoint!).Port;
        portProbe.Close();
        await using var alice = new LanService(storeA, profileA, new NodeOptions { DiscoveryPort = discoveryPort });
        await using var bob = new LanService(storeB, profileB, new NodeOptions { DiscoveryPort = discoveryPort });
        alice.Error += text => Console.WriteLine("Alice: " + text);
        bob.Error += text => Console.WriteLine("Bob: " + text);
        var discovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        alice.PeerChanged += peer => { if (peer.Id == profileB.Id) discovered.TrySetResult(); };
        await alice.StartAsync(); await bob.StartAsync();
        await discovered.Task.WaitAsync(TimeSpan.FromSeconds(12));
        Check(alice.Peers.Any(x => x.Id == profileB.Id), "real UDP multicast/broadcast discovery");
        var peerB = await alice.ConnectAsync("127.0.0.1", bob.Port);
        var peerA = await bob.ConnectAsync("127.0.0.1", alice.Port);
        Check(peerB.Nickname == profileB.Nickname && peerB.Address == "127.0.0.1", "TLS handshake, nickname and IP");
        Check(!await storeA.TrustIdentityAsync(profileB.Id, new string('F', 64)), "reject changed identity certificate");
        var incoming = new List<ChatMessage>();
        bob.MessageReceived += message => { lock (incoming) incoming.Add(message); };
        var sent = await alice.SendTextAsync(peerB, "你好 Bob 👋\n第二行");
        Check(sent.Delivery == DeliveryState.Sent, "message delivery acknowledged");
        Check(incoming.Single().Text == sent.Text && incoming.Single().PeerId == profileA.Id, "Unicode multiline text received");
        await alice.RetryMessageAsync(peerB, sent);
        Check(incoming.Count == 1 && (await storeB.LoadMessagesAsync()).Count == 1, "message retry is idempotent");
        var source = Path.Combine(TestRoot, "大文件.bin");
        var bytes = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 17);
        await File.WriteAllBytesAsync(source, bytes);
        await alice.SendFileAsync(peerB, source, false);
        var attachment = incoming.Last();
        Check(attachment.LocalPath is null && attachment.FileSize == bytes.Length, "file offer does not download automatically");
        Check(attachment.PeerNickname == profileA.Nickname && attachment.PeerAddress == "127.0.0.1" && attachment.ReceivedAt is not null, "received file captures authenticated sender name, actual IP and local receipt time");
        var download = await bob.DownloadAttachmentAsync(peerA, attachment);
        Check((await File.ReadAllBytesAsync(download)).SequenceEqual(bytes), "streamed file download and SHA-256 verification");
        Check(download.StartsWith(AppPaths.DailyMedia(storeB.DataDirectory, false, "Received"), StringComparison.Ordinal), "received files archived by day");
        Check(sent.LocalPath is null && (await storeA.LoadAttachmentsAsync()).All(x => x.Path.StartsWith(AppPaths.DailyMedia(storeA.DataDirectory, false, "Sent"), StringComparison.Ordinal)), "sent files archived by day");
        Check((await storeB.LoadMessagesAsync()).Last().LocalPath == download, "download path persisted");
        var empty = Path.Combine(TestRoot, "empty.txt"); await File.WriteAllBytesAsync(empty, []);
        await alice.SendFileAsync(peerB, empty, false);
        var emptyDownload = await bob.DownloadAttachmentAsync(peerA, incoming.Last());
        Check(new FileInfo(emptyDownload).Length == 0, "zero-byte file transfer");
        var image = Path.Combine(TestRoot, "image.png");
        await File.WriteAllBytesAsync(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/a9sAAAAASUVORK5CYII="));
        await alice.SendFileAsync(peerB, image, true);
        Check(incoming.Last().Kind == MessageKind.Image, "image message kind");
        var imageDownload = await bob.DownloadAttachmentAsync(peerA, incoming.Last());
        Check(File.Exists(imageDownload) && imageDownload.StartsWith(AppPaths.DailyMedia(storeB.DataDirectory, true, "Received"), StringComparison.Ordinal), "image download stored in local cache");
        var share = Path.Combine(TestRoot, "shared");
        Directory.CreateDirectory(Path.Combine(share, "nested"));
        await File.WriteAllTextAsync(Path.Combine(share, "nested", "hello.txt"), "共享文件");
        await alice.AddSharedFolderAsync(share);
        var roots = await bob.BrowseAsync(peerA);
        Check(roots.Count == 1 && roots[0].IsDirectory, "shared root listing");
        var entries = await bob.BrowseAsync(peerA, roots[0].RootId);
        var nested = await bob.BrowseAsync(peerA, roots[0].RootId, entries[0].RelativePath);
        Check(nested.Single().Name == "hello.txt", "nested shared folder browsing");
        var sharedDownload = await bob.DownloadSharedFileAsync(peerA, nested.Single());
        Check(await File.ReadAllTextAsync(sharedDownload) == "共享文件", "shared file download");
        Check((await storeB.LoadFileHistoryAsync("hello.txt")).Messages.Single().LocalPath == sharedDownload, "shared downloads appear in the file history");
        await MustFailAsync(() => bob.BrowseAsync(peerA, roots[0].RootId, "../"), "reject network path traversal");
        await MustFailAsync(() => bob.BrowseAsync(peerA, roots[0].RootId, "C:\\Windows"), "reject absolute paths");
        await alice.RemoveSharedFolderAsync(roots[0].RootId);
        await MustFailAsync(() => bob.BrowseAsync(peerA, roots[0].RootId), "share revocation takes effect");
        Check((await storeA.LoadPeersAsync()).Any(x => x.Id == profileB.Id), "peer profile history persisted");
        var folder = new SharedFolder("test", "root", share);
        try { SharedFileAccess.Resolve(folder, "../outside"); throw new InvalidOperationException("Traversal accepted"); }
        catch (UnauthorizedAccessException) { Check(true, "local path boundary enforcement"); }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await MustFailAsync(() => bob.DownloadSharedFileAsync(peerA, nested.Single(), cancellation.Token), "download cancellation");
        profileA.Nickname = "Alice 已改名"; await alice.UpdateProfileAsync();
        Check((await bob.ConnectAsync("127.0.0.1", alice.Port)).Nickname == "Alice 已改名", "profile changes propagated");
        var reopened = new IndexedFileStore(storeB.DataDirectory);
        Check((await reopened.LoadMessagesAsync()).Count == (await storeB.LoadMessagesAsync()).Count, "file history survives repository restart");
        var escaped = "引号 ' ; DROP TABLE messages; -- <>&";
        await alice.SendTextAsync(peerB, escaped);
        Check((await reopened.LoadMessagesAsync()).Last().Text == escaped, "record serialization preserves special characters");
        await alice.DisposeAsync();
        await using var restarted = new LanService(storeA, profileA, new NodeOptions { EnableDiscovery = false });
        await restarted.StartAsync();
        var restartedPeer = await bob.ConnectAsync("127.0.0.1", restarted.Port);
        var fromIndex = await bob.DownloadAttachmentAsync(restartedPeer, attachment);
        Check((await File.ReadAllBytesAsync(fromIndex)).SequenceEqual(bytes), "sender restart serves existing attachments from disk without a resident index");
        var foreignStore = new IndexedFileStore(Path.Combine(TestRoot, "foreign-attachment-client"));
        await using var foreign = new LanService(foreignStore, await foreignStore.LoadProfileAsync(), new NodeOptions { EnableDiscovery = false });
        await foreign.StartAsync();
        var foreignPeer = await foreign.ConnectAsync("127.0.0.1", restarted.Port);
        await MustFailAsync(() => foreign.DownloadAttachmentAsync(foreignPeer, attachment), "lazy attachment lookup still rejects downloads by another device");
        var receivingPeer = await restarted.ConnectAsync("127.0.0.1", bob.Port);
        await RunStreamingTransferTestsAsync(restarted, bob, receivingPeer, restartedPeer);
        await RunFolderTransferTestsAsync(restarted, bob, receivingPeer, restartedPeer);
    }

    private static async Task MustFailAsync(Func<Task> action, string description)
    {
        var failed = false;
        try { await action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException) { failed = true; }
        Check(failed, description);
    }

    private static void RunUiTests()
    {
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var store = new IndexedFileStore(Path.Combine(TestRoot, "ui"));
        var profile = new UserProfile { Nickname = "林间" };
        var node = new LanService(store, profile, new NodeOptions { EnableDiscovery = false });
        var remoteStore = new IndexedFileStore(Path.Combine(TestRoot, "ui-remote"));
        var remote = new LanService(remoteStore, new UserProfile { Nickname = "小宇" }, new NodeOptions { EnableDiscovery = false });
        Pump(remote.StartAsync());
        var ui = new TestUiServices();
        var blockedStore = new IndexedFileStore(Path.Combine(TestRoot, "ui-blocked-port"));
        var blockedNode = new LanService(blockedStore, new UserProfile(), new NodeOptions { ListenPort = remote.Port, EnableDiscovery = false });
        var blockedUi = new TestUiServices();
        using (var blockedModel = new MainWindowViewModel(blockedNode, blockedStore, blockedUi))
        {
            Pump(blockedModel.InitializeAsync());
            Check(blockedUi.StartupChanges.SequenceEqual(new[] { true }) && blockedModel.Status.StartsWith("启动失败", StringComparison.Ordinal),
                "startup registration is applied even if the network listener cannot start");
        }
        Pump(blockedNode.DisposeAsync().AsTask());
        var model = new MainWindowViewModel(node, store, ui);
        Pump(model.InitializeAsync());
        Check(model.IsFiles && model.Inbox.Files.Count <= FileInboxViewModel.PageSize, "client starts on the file inbox with a bounded first page");
        Check(model.Settings.StartWithWindows && ui.StartupChanges.SequenceEqual(new[] { true }), "first launch applies enabled startup through the OS service");
        model.Settings.StartWithWindows = false;
        Pump(model.Settings.SaveCommand.ExecuteAsync(null));
        Check(!XmlConfiguration.Load(store.DataDirectory).Profile.StartWithWindows && ui.StartupChanges.Last() == false,
            "turning off startup removes it through the OS service and persists the choice in XML");
        var reloadedSettings = new SettingsViewModel(node, store, ui, _ => { });
        Check(!reloadedSettings.StartWithWindows && reloadedSettings.VersionLabel.EndsWith(typeof(SettingsViewModel).Assembly.GetName().Version!.ToString(3), StringComparison.Ordinal),
            "settings reload preserves startup opt-out and displays the assembly version");
        ui.StartupFailure = true; model.Settings.StartWithWindows = true;
        Pump(model.Settings.SaveCommand.ExecuteAsync(null));
        Check(!node.Profile.StartWithWindows && !XmlConfiguration.Load(store.DataDirectory).Profile.StartWithWindows,
            "OS startup failure never saves a successful enabled preference");
        ui.StartupFailure = false; model.Settings.StartWithWindows = false;
        Pump(node.ConnectAsync("127.0.0.1", remote.Port));
        Dispatcher.UIThread.RunJobs();
        var window = new MainWindow { DataContext = model };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var imagePath = Path.Combine(TestRoot, "ui-image.png");
        using (var bitmap = new WriteableBitmap(new PixelSize(32, 32), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque))
            bitmap.Save(imagePath);
        model.Settings.Nickname = "林间 新昵称";
        model.Settings.ShowNotifications = true;
        Pump(model.Settings.SaveCommand.ExecuteAsync(null));
        var savedProfile = store.LoadProfileAsync().GetAwaiter().GetResult();
        Check(savedProfile.Nickname == "林间 新昵称" && savedProfile.ShowNotifications, "device identity and incoming-file prompt preference persisted");
        ui.NextFile = imagePath;
        Pump(model.Settings.ChangeAvatarCommand.ExecuteAsync(null));
        var savedAvatar = XmlConfiguration.Load(store.DataDirectory).Profile.AvatarPath;
        Check(savedAvatar is not null && savedAvatar.StartsWith(AppPaths.DailyMedia(store.DataDirectory, true, "Avatars"), StringComparison.Ordinal) && File.Exists(savedAvatar), "avatar stored by day and referenced from XML");
        var avatarPeerTask = remote.ConnectAsync("127.0.0.1", node.Port);
        Pump(avatarPeerTask);
        Check(avatarPeerTask.Result.AvatarPath is { } remoteAvatar && File.Exists(remoteAvatar), "updated avatar transferred to peer");
        window.WindowState = WindowState.Minimized; Dispatcher.UIThread.RunJobs();
        Check(!window.IsVisible, "minimize hides main window for tray residency");
        window.Restore(); Dispatcher.UIThread.RunJobs();
        Check(window.IsVisible && window.WindowState == WindowState.Normal, "restore from tray");
        var notifications = new NotificationService(); notifications.Show("小宇", "发来文件：项目资料.zip", () => { });
        Dispatcher.UIThread.RunJobs(); notifications.Close();
        Check(true, "file notification window opens and closes");
        RunFileInboxTests(window, model, node, store, remote, remoteStore, ui);
        RunFileOnlyPageTests(window, model, node, remote, remoteStore, ui, imagePath);
        RunDeviceTabsAndReceiveTests(window, model, node, store, remote, remoteStore, ui);
        RunBackgroundReceivePromptTests(window, model, node, remote, ui);
        RunHistoryDeletionTests(window, model, node, store, remote);
        RunQrShareUiTests(window, model, store, ui);
        RunGitHubUpdateUiTests(window, ui);
        model.Dispose();
        window.AllowClose = true; window.Close();
        Pump(node.DisposeAsync().AsTask()); Pump(remote.DisposeAsync().AsTask());
    }

    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI test timed out");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs();
    }

    private static void Render(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Pump(Task.Delay(40));
        var output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "screenshots");
        Directory.CreateDirectory(output);
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame");
        frame.Save(Path.Combine(output, name));
        Check(frame.PixelSize.Width is >= 360 and < 600, "render " + name);
    }

    private sealed class TestUiServices : IUiServices
    {
        public string? NextFile { get; set; }
        public string? NextFolder { get; set; }
        public IReadOnlyList<string>? NextFiles { get; set; }
        public IReadOnlyList<string>? NextFolders { get; set; }
        public string? LastOpened { get; private set; }
        public string? LastRevealed { get; private set; }
        public bool RevealFailure { get; set; }
        public string? CopiedText { get; private set; }
        public Task CopyTextAsync(string text) { CopiedText = text; return Task.CompletedTask; }
        public List<bool> StartupChanges { get; } = [];
        public bool StartupFailure { get; set; }
        public Task<string?> PickFileAsync(bool imageOnly = false) { var result = NextFile; NextFile = null; return Task.FromResult(result); }
        public Task<string?> PickFolderAsync() { var result = NextFolder; NextFolder = null; return Task.FromResult(result); }
        public async Task<IReadOnlyList<string>> PickFilesAsync() { var result = NextFiles; NextFiles = null; return result ?? (await PickFileAsync() is { } file ? [file] : []); }
        public async Task<IReadOnlyList<string>> PickFoldersAsync() { var result = NextFolders; NextFolders = null; return result ?? (await PickFolderAsync() is { } folder ? [folder] : []); }
        public void OpenFile(string path) => LastOpened = path;
        public void RevealFile(string path)
        {
            if (RevealFailure) throw new IOException("test folder reveal failure");
            LastRevealed = path;
        }
        public void SetTheme(string theme) => Application.Current!.RequestedThemeVariant = theme switch { "Light" => ThemeVariant.Light, "Dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
        public void SetStartWithWindows(bool enabled)
        {
            if (StartupFailure) throw new IOException("test startup write failure");
            StartupChanges.Add(enabled);
        }
    }
}
