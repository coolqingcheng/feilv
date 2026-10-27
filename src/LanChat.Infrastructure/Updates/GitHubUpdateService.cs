using System.Buffers;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LanChat.Core.Models;
using LanChat.Core.Services;

namespace LanChat.Infrastructure.Updates;

public sealed class GitHubUpdateService : IAppUpdateService
{
    private const long MaximumArchiveSize = 512L * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly string _updatesDirectory;
    private readonly string _runtime;

    public GitHubUpdateService(string dataDirectory, HttpClient? http = null, string? runtime = null)
    {
        _updatesDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "Data", "Updates");
        _runtime = runtime ?? AppReleases.Runtime;
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<AppRelease?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{AppReleases.Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("Feilu/" + currentVersion.ToString(3));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new IOException("更新服务暂时受限，请稍后重试。");
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var json = new MemoryStream();
        await CopyLimitedAsync(content, json, 1024 * 1024, deadline.Token).ConfigureAwait(false);
        json.Position = 0;
        using var document = await JsonDocument.ParseAsync(json, cancellationToken: deadline.Token).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) || version.Build < 0 || version.Revision >= 0)
            throw new InvalidDataException("发行版本号无效。");
        if (version <= new Version(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build))) return null;
        var assetNames = new[] { AppReleases.AssetName(version.ToString(3), _runtime), AppReleases.ArchiveAssetName(version.ToString(3), _runtime) }.Distinct();
        foreach (var assetName in assetNames)
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != assetName) continue;
            var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !IsSha256(digest[7..]))
                throw new InvalidDataException("更新包缺少校验信息，已停止更新。");
            var size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaximumArchiveSize) throw new InvalidDataException("发行包大小无效。");
            var notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "";
            // Release descriptions are display-only; cap retained text and never interpret HTML or scripts.
            if (notes.Length > 16384) notes = notes[..16384] + "\n…";
            return new AppRelease(version.ToString(3), _runtime, assetName, digest[7..].ToLowerInvariant(), size) { ReleaseNotes = notes.Trim() };
        }
        return null;
    }

    public async Task<PreparedAppUpdate> DownloadAsync(AppRelease release, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (release.Runtime != _runtime || (release.AssetName != AppReleases.AssetName(release.Version, _runtime) && release.AssetName != AppReleases.ArchiveAssetName(release.Version, _runtime))
            || !Version.TryParse(release.Version, out _) || !IsSha256(release.Sha256) || release.Size is <= 0 or > MaximumArchiveSize)
            throw new InvalidDataException("更新信息无效。");
        Directory.CreateDirectory(_updatesDirectory);
        var cached = Path.Combine(_updatesDirectory, "ready.json");
        if (File.Exists(cached) && new FileInfo(cached).Length <= 1024 * 1024)
        {
            try
            {
                await using var stream = File.OpenRead(cached);
                var ready = await JsonSerializer.DeserializeAsync(stream, UpdateJsonContext.Default.PreparedAppUpdate, cancellationToken).ConfigureAwait(false);
                if (ready is not null && AppReleases.SamePackage(ready.Release, release) && Path.GetDirectoryName(ready.PayloadDirectory) is { } parent
                    && Path.GetDirectoryName(parent) == _updatesDirectory)
                {
                    await UpdatePackage.ValidateAsync(ready, cancellationToken).ConfigureAwait(false);
                    return ready with { Release = release };
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { }
        }
        var staging = Path.Combine(_updatesDirectory, Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(staging, "payload");
        Directory.CreateDirectory(payload);
        var succeeded = false;
        try
        {
            var archive = Path.Combine(staging, "package.download");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(30));
            using (var response = await GetDownloadAsync(release, deadline.Token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } length && length != release.Size) throw new InvalidDataException("更新包大小与发行信息不一致。");
                await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                await using var destination = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(65536);
                long received = 0;
                var clock = Stopwatch.StartNew(); var lastProgress = TimeSpan.Zero;
                try
                {
                    int count;
                    while ((count = await source.ReadAsync(buffer.AsMemory(0, 65536), deadline.Token).ConfigureAwait(false)) != 0)
                    {
                        received += count;
                        if (received > release.Size) throw new InvalidDataException("更新下载超过预期大小。");
                        hash.AppendData(buffer, 0, count);
                        await destination.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                        if (clock.Elapsed - lastProgress >= TimeSpan.FromMilliseconds(250))
                        {
                            progress?.Report(new(received, release.Size, received / Math.Max(.001, clock.Elapsed.TotalSeconds)));
                            lastProgress = clock.Elapsed;
                        }
                    }
                    if (received != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("更新包 SHA-256 或大小校验失败，未更改当前程序。");
                    progress?.Report(new(received, release.Size, received / Math.Max(.001, clock.Elapsed.TotalSeconds)));
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            if (AppReleases.IsInstaller(release))
            {
                File.Move(archive, Path.Combine(payload, release.AssetName));
                var installerManifest = new PackageManifest(release.Version, release.Runtime, release.AssetName,
                    [new PackageFile(release.AssetName, release.Size, release.Sha256)]);
                await using var output = new FileStream(Path.Combine(payload, "app-manifest.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
                await JsonSerializer.SerializeAsync(output, installerManifest, UpdateJsonContext.Default.PackageManifest, cancellationToken).ConfigureAwait(false);
            }
            else await ExtractAsync(archive, payload, release.Runtime.StartsWith("win-", StringComparison.Ordinal), cancellationToken).ConfigureAwait(false);
            var manifestPath = Path.Combine(payload, "app-manifest.json");
            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 512 * 1024) throw new InvalidDataException("更新包缺少有效清单。");
            PackageManifest manifest;
            await using (var stream = File.OpenRead(manifestPath))
                manifest = await JsonSerializer.DeserializeAsync(stream, UpdateJsonContext.Default.PackageManifest, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("更新清单为空。");
            var prepared = new PreparedAppUpdate(release, payload, manifest);
            await UpdatePackage.ValidateAsync(prepared, cancellationToken).ConfigureAwait(false);
            File.Delete(archive);
            await using (var stream = new FileStream(cached + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, prepared, UpdateJsonContext.Default.PreparedAppUpdate, cancellationToken).ConfigureAwait(false);
            File.Move(cached + ".tmp", cached, true);
            succeeded = true;
            return prepared;
        }
        finally { if (!succeeded && Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private async Task<HttpResponseMessage> GetDownloadAsync(AppRelease release, CancellationToken ct)
    {
        var uri = new Uri($"{AppReleases.HomeUrl}/releases/download/v{release.Version}/{release.AssetName}");
        for (var i = 0; i < 6; i++)
        {
            if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || !(uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com"))
                throw new InvalidDataException("更新下载地址未通过验证。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Feilu-Updater");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new IOException("更新下载重定向无效。");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new IOException("更新下载重定向次数过多。");
    }

    private static async Task ExtractAsync(string archive, string destination, bool zip, CancellationToken ct)
    {
        long total = 0; var count = 0;
        async Task ExtractFile(string name, long size, Stream source)
        {
            if (++count > 256 || size < 0 || (total += size) > 1024L * 1024 * 1024) throw new InvalidDataException("更新包解压大小超出限制。");
            var path = UpdatePackage.ResolvePath(destination, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            await CopyLimitedAsync(source, output, size, ct).ConfigureAwait(false);
            if (output.Length != size) throw new InvalidDataException("更新文件大小错误。");
        }
        if (zip)
        {
            using var input = ZipFile.OpenRead(archive);
            foreach (var entry in input.Entries)
            {
                if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("更新包不能含符号链接。");
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(UpdatePackage.ResolvePath(destination, entry.FullName.TrimEnd('/'))); continue; }
                await using var source = entry.Open();
                await ExtractFile(entry.FullName, entry.Length, source).ConfigureAwait(false);
            }
        }
        else
        {
            await using var file = File.OpenRead(archive);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            await using var tar = new TarReader(gzip);
            TarEntry? entry;
            while ((entry = await tar.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false)) is not null)
            {
                var name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
                if (name.Length == 0 && entry.EntryType == TarEntryType.Directory) continue;
                if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(UpdatePackage.ResolvePath(destination, name.TrimEnd('/'))); continue; }
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) throw new InvalidDataException("更新包只能包含普通文件。");
                await ExtractFile(name, entry.Length, entry.DataStream ?? Stream.Null).ConfigureAwait(false);
            }
        }
    }

    private static async Task CopyLimitedAsync(Stream source, Stream destination, long maximum, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            long copied = 0; int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, 65536), ct).ConfigureAwait(false)) > 0)
            {
                if ((copied += read) > maximum) throw new InvalidDataException("更新响应超过大小限制。");
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    internal static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    public void Dispose() => _http.Dispose();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PackageManifest))]
[JsonSerializable(typeof(PreparedAppUpdate))]
[JsonSerializable(typeof(UpdateInstallPlan))]
internal partial class UpdateJsonContext : JsonSerializerContext;
