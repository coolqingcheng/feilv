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
        var release = new AppRelease("9.8.7", "win-x64", AppReleases.AssetName("9.8.7", "win-x64"), Convert.ToHexString(digest).ToLowerInvariant(), new FileInfo(archive).Length);
        var handler = new UpdateHttpHandler(archive, release);
        using var service = new GitHubUpdateService(root, new HttpClient(handler), "win-x64");
        var discovered = await service.CheckAsync(new Version(1, 4, 0));
        Check(discovered == release && handler.LastApi == $"https://api.github.com/repos/{AppReleases.Repository}/releases/latest", "updater reads only the fixed GitHub repository and matching platform asset");
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
    }

    private sealed class UpdateHttpHandler(string archive, AppRelease release) : HttpMessageHandler
    {
        public bool Prerelease { get; set; }
        public bool MissingDigest { get; set; }
        public bool RedirectToUntrusted { get; set; }
        public string? LastApi { get; private set; }
        public int Downloads { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                LastApi = request.RequestUri.ToString();
                var json = $$"""{"tag_name":"v{{release.Version}}","draft":false,"prerelease":{{Prerelease.ToString().ToLowerInvariant()}},"assets":[{"name":"{{release.AssetName}}","size":{{release.Size}},"digest":{{(MissingDigest ? "null" : "\"sha256:" + release.Sha256 + "\"")}}}]}""";
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
