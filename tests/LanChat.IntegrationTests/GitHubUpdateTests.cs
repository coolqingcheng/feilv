using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanChat.Core.Models;
using LanChat.Infrastructure.Updates;
using LanChat.Infrastructure.Storage;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static async Task RunGitHubUpdateTestsAsync()
    {
        var root = Path.Combine(TestRoot, "github-updates"); Directory.CreateDirectory(root);
        var source = Path.Combine(root, "package"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "飞驴.exe"), "new executable");
        var library = Path.Combine(source, "native.dll");
        await using (var stream = File.Create(library))
        {
            var buffer = new byte[65536]; RandomNumberGenerator.Fill(buffer);
            for (var i = 0; i < 128; i++) await stream.WriteAsync(buffer);
        }
        var files = new List<PackageFile>();
        foreach (var path in Directory.EnumerateFiles(source))
            files.Add(new PackageFile(Path.GetFileName(path), new FileInfo(path).Length, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant()));
        var manifest = new PackageManifest("9.8.7", "win-x64", "飞驴.exe", files);
        await File.WriteAllTextAsync(Path.Combine(source, "app-manifest.json"), JsonSerializer.Serialize(manifest, UpdateJsonContext.Default.PackageManifest));
        var archive = Path.Combine(root, "package.zip");
        ZipFile.CreateFromDirectory(source, archive, CompressionLevel.NoCompression, false);
        byte[] digest;
        await using (var stream = File.OpenRead(archive)) digest = await SHA256.HashDataAsync(stream);
        var release = new AppRelease("9.8.7", "win-x64", AppReleases.ArchiveAssetName("9.8.7", "win-x64"), Convert.ToHexString(digest).ToLowerInvariant(), new FileInfo(archive).Length);
        var handler = new UpdateHttpHandler(archive, release);
        using var service = new GitHubUpdateService(root, new HttpClient(handler), "win-x64");
        var discovered = await service.CheckAsync(new Version(1, 4, 0));
        Check(discovered == release && handler.LastApi == $"https://api.github.com/repos/{AppReleases.Repository}/releases/latest", "updater reads only the fixed GitHub repository and matching platform asset");
        handler.ReleaseNotes = "新增设备版本号\n- 优化更新提示\n<script>literal text</script>";
        Check((await service.CheckAsync(new Version(1, 4, 0)))!.ReleaseNotes == handler.ReleaseNotes, "release descriptions preserve Unicode, newlines and literal markup for display");
        handler.ReleaseNotes = new string('文', 20000);
        Check((await service.CheckAsync(new Version(1, 4, 0)))!.ReleaseNotes.Length < 16400, "retained release descriptions have a bounded size");
        handler.ReleaseNotes = null;
        Check((await service.CheckAsync(new Version(1, 4, 0)))!.ReleaseNotes == "", "null release descriptions remain compatible with updates");
        Check(await service.CheckAsync(new Version(9, 8, 7)) is null && await service.CheckAsync(new Version(10, 0, 0)) is null, "equal and older GitHub releases cannot downgrade the client");
        handler.Prerelease = true;
        Check(await service.CheckAsync(new Version(1, 0, 0)) is null, "prereleases never enter the stable update channel");
        handler.Prerelease = false; handler.MissingDigest = true;
        await MustFailAsync(() => service.CheckAsync(new Version(1, 0, 0)), "an asset without GitHub SHA-256 metadata is rejected before downloading");
        handler.MissingDigest = false;
        var before = GC.GetTotalAllocatedBytes(true);
        var prepared = await service.DownloadAsync(release);
        await UpdatePackage.ValidateAsync(prepared with { PayloadDirectory = prepared.PayloadDirectory + Path.DirectorySeparatorChar });
        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        Check(allocated < 4 * 1024 * 1024 && File.Exists(Path.Combine(prepared.PayloadDirectory, "飞驴.exe")), "8 MiB update downloads, extraction and hashing use bounded buffers rather than loading the archive into memory");
        Check(handler.Downloads == 1 && !Directory.EnumerateFiles(Path.GetDirectoryName(prepared.PayloadDirectory)!, "*.download").Any(), "only a fully checked payload is cached and its downloaded archive is removed");
        var again = await service.DownloadAsync(release);
        Check(again.PayloadDirectory == prepared.PayloadDirectory && handler.Downloads == 1, "an already verified download is revalidated and reused without another download");
        var editedNotes = await service.DownloadAsync(release with { ReleaseNotes = "补充更新说明" });
        Check(editedNotes.Release.ReleaseNotes == "补充更新说明" && handler.Downloads == 1, "editing release notes refreshes the cached description without downloading the same package again");
        await File.AppendAllTextAsync(Path.Combine(prepared.PayloadDirectory, "飞驴.exe"), "tampered");
        await MustFailAsync(() => UpdatePackage.ValidateAsync(prepared), "changing the staged executable after download invalidates installation");
        prepared = await service.DownloadAsync(release);
        Check(handler.Downloads == 2, "a damaged cache is discarded and a complete verified package is downloaded again");
        await MustFailAsync(() => service.DownloadAsync(release with { Sha256 = new string('0', 64) }), "a mismatched downloaded archive digest never becomes a prepared update");
        handler.RedirectToUntrusted = true;
        await MustFailAsync(() => service.DownloadAsync(release with { Sha256 = new string('1', 64) }), "download redirects to a host outside GitHub are refused");
        handler.RedirectToUntrusted = false;
        foreach (var path in new[] { "../outside.exe", "/absolute.exe", "C:/outside.exe", "Data/config.xml", "native/../../escape", "folder\\escape" })
        {
            var rejected = false;
            try { UpdatePackage.ResolvePath(root, path); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "update paths reject " + path);
        }
        var installed = Path.Combine(root, "installed"); Directory.CreateDirectory(installed);
        var target = Path.Combine(installed, "飞驴-v1.3.0.exe");
        await File.WriteAllTextAsync(target, "old executable");
        await File.WriteAllTextAsync(Path.Combine(installed, "native.dll"), "old library");
        Directory.CreateDirectory(Path.Combine(installed, "Data"));
        await File.WriteAllTextAsync(Path.Combine(installed, "Data", "config.xml"), "keep identity and settings");
        var failed = false;
        try { UpdateInstaller.ApplyFiles(prepared, target, Path.Combine(root, "rollback"), path => { if (path.EndsWith(".exe", StringComparison.Ordinal)) throw new IOException("simulated file lock"); }); }
        catch (IOException) { failed = true; }
        Check(failed && await File.ReadAllTextAsync(target) == "old executable" && await File.ReadAllTextAsync(Path.Combine(installed, "native.dll")) == "old library", "a replacement failure restores every previously replaced file");
        var transaction = UpdateInstaller.ApplyFiles(prepared, target, Path.Combine(root, "backup"));
        Check(await File.ReadAllTextAsync(target) == "new executable" && await File.ReadAllTextAsync(Path.Combine(installed, "Data", "config.xml")) == "keep identity and settings", "installation keeps the existing executable path, configuration and user data");
        UpdateInstaller.Rollback(transaction);
        Check(await File.ReadAllTextAsync(target) == "old executable" && !File.Exists(Path.Combine(installed, "app-manifest.json")), "rollback restores the previous application and removes newly introduced package files");
        var configuration = XmlConfiguration.Load(Path.Combine(root, "configuration"));
        configuration.Profile.AutoDownloadUpdates = false; configuration.Save(Path.Combine(root, "configuration"));
        Check(!XmlConfiguration.Load(Path.Combine(root, "configuration")).Profile.AutoDownloadUpdates, "automatic update download opt-out persists in the existing XML configuration");
        var setupRelease = release with { AssetName = AppReleases.AssetName(release.Version, release.Runtime) };
        var setupHandler = new UpdateHttpHandler(archive, setupRelease) { IncludeLegacyArchive = true };
        using var setupService = new GitHubUpdateService(Path.Combine(root, "setup-download"), new HttpClient(setupHandler), "win-x64");
        Check(await setupService.CheckAsync(new Version(1, 0, 0)) == setupRelease, "Windows selects the wizard installer even when a legacy ZIP appears first in the release assets");
        var setupPackage = await setupService.DownloadAsync(setupRelease);
        Check(AppReleases.IsInstaller(setupPackage.Release) && setupPackage.Manifest.Files.Count == 1
            && File.Exists(Path.Combine(setupPackage.PayloadDirectory, setupRelease.AssetName)), "a Windows installer becomes ready after streaming SHA-256 validation without executing it or extracting a ZIP");
        var forged = setupPackage with { Manifest = setupPackage.Manifest with { Files = [new(setupRelease.AssetName, setupRelease.Size, new string('0', 64))] } };
        await MustFailAsync(() => UpdatePackage.ValidateAsync(forged), "installer cache metadata cannot replace the digest received from GitHub");
        await File.AppendAllTextAsync(Path.Combine(setupPackage.PayloadDirectory, setupRelease.AssetName), "modified");
        await MustFailAsync(() => UpdatePackage.ValidateAsync(setupPackage), "the staged setup EXE is rechecked before confirmed execution");
        Check(AppReleases.AssetName("9.8.7", "osx-arm64") == "feilv-v9.8.7-osx-arm64.dmg"
            && AppReleases.AssetName("9.8.7", "linux-x64") == "feilv-v9.8.7-linux-x64.AppImage"
            && AppReleases.AssetName("9.8.7", "linux-arm64") == "feilv-v9.8.7-linux-arm64.AppImage", "release asset names select macOS DMG and both Linux AppImages");
        var imageRelease = release with { Runtime = "linux-x64", AssetName = AppReleases.AssetName(release.Version, "linux-x64") };
        var imageHandler = new UpdateHttpHandler(archive, imageRelease);
        using var imageService = new GitHubUpdateService(Path.Combine(root, "appimage-download"), new HttpClient(imageHandler), "linux-x64");
        Check(await imageService.CheckAsync(new Version(1, 0, 0)) == imageRelease, "updater discovers the matching AppImage release and digest");
        var imagePackage = await imageService.DownloadAsync(imageRelease);
        Check(AppReleases.IsAppImage(imagePackage.Release) && imagePackage.Manifest.Files.Count == 1
            && File.Exists(Path.Combine(imagePackage.PayloadDirectory, imageRelease.AssetName)), "AppImage is streamed and verified as a single file without extraction");
        var imageTarget = Path.Combine(root, "current.AppImage");
        await File.WriteAllTextAsync(imageTarget, "old image");
        var imageSwap = UpdateInstaller.ApplyAppImage(imagePackage, imageTarget, Path.Combine(root, "image-backup"));
        Check(new FileInfo(imageTarget).Length == imageRelease.Size, "AppImage update replaces the original file while retaining its path");
        UpdateInstaller.Rollback(imageSwap);
        Check(await File.ReadAllTextAsync(imageTarget) == "old image", "a failed AppImage restart restores the previous executable");
        await File.AppendAllTextAsync(Path.Combine(imagePackage.PayloadDirectory, imageRelease.AssetName), "tampered");
        await MustFailAsync(() => UpdatePackage.ValidateAsync(imagePackage), "a changed AppImage cannot be installed after verification");
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Path.Combine(source, "Data"));
            await File.WriteAllTextAsync(Path.Combine(source, "Data", "config.xml"), "preserve user data");
            await File.WriteAllTextAsync(Path.Combine(source, "unins000.exe"), "uninstaller bookkeeping");
            await UpdatePackage.VerifyDistributionAsync(source);
            Check(true, "installed distribution checks hash program files without scanning or rejecting user data and uninstaller files");
            var helper = await WindowsSetupUpdate.PrepareHelperAsync(setupPackage, Path.Combine(source, "飞驴.exe"), CancellationToken.None);
            Check(File.Exists(helper) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(helper)!, "Data")), "installer updates copy only the declared executable and dependencies into their isolated helper");
        }
    }

    private sealed class UpdateHttpHandler(string archive, AppRelease release) : HttpMessageHandler
    {
        public bool Prerelease { get; set; }
        public bool MissingDigest { get; set; }
        public bool RedirectToUntrusted { get; set; }
        public string? LastApi { get; private set; }
        public int Downloads { get; private set; }
        public bool IncludeLegacyArchive { get; set; }
        public string? ReleaseNotes { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                LastApi = request.RequestUri.ToString();
                var legacy = IncludeLegacyArchive ? $$"""{"name":"{{AppReleases.ArchiveAssetName(release.Version, release.Runtime)}}","size":{{release.Size}},"digest":"sha256:{{release.Sha256}}"},""" : "";
                var notes = ReleaseNotes is null ? "null" : "\"" + JsonEncodedText.Encode(ReleaseNotes) + "\"";
                var json = $$"""{"tag_name":"v{{release.Version}}","body":{{notes}},"draft":false,"prerelease":{{Prerelease.ToString().ToLowerInvariant()}},"assets":[{{legacy}}{"name":"{{release.AssetName}}","size":{{release.Size}},"digest":{{(MissingDigest ? "null" : "\"sha256:" + release.Sha256 + "\"")}}}]}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
            Downloads++;
            if (RedirectToUntrusted)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found); redirect.Headers.Location = new Uri("https://example.com/update.exe");
                return Task.FromResult(redirect);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(archive)) });
        }
    }
}
