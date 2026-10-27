using System.Security.Cryptography;
using System.Text.Json;
using LanChat.Core.Models;

namespace LanChat.Infrastructure.Updates;

public static class UpdatePackage
{
    public static async Task VerifyDistributionAsync(string root)
    {
        await using var stream = File.OpenRead(Path.Combine(root, "app-manifest.json"));
        var manifest = await JsonSerializer.DeserializeAsync(stream, UpdateJsonContext.Default.PackageManifest).ConfigureAwait(false)
            ?? throw new InvalidDataException("发行清单为空。");
        var release = new AppRelease(manifest.Version, manifest.Runtime, AppReleases.AssetName(manifest.Version, manifest.Runtime), "", 0);
        if (manifest.Runtime != AppReleases.Runtime) throw new InvalidDataException("发行架构与运行架构不一致。");
        await ValidateAsync(new PreparedAppUpdate(release, root, manifest)).ConfigureAwait(false);
    }

    public static async Task ValidateAsync(PreparedAppUpdate package, CancellationToken ct = default)
    {
        var manifest = package.Manifest;
        if (manifest.Version != package.Release.Version || manifest.Runtime != package.Release.Runtime || manifest.Files.Count is < 1 or > 256)
            throw new InvalidDataException("更新包的版本、平台或文件清单不匹配。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (!paths.Add(file.Path) || file.Size < 0 || !GitHubUpdateService.IsSha256(file.Sha256)) throw new InvalidDataException("更新文件清单无效。");
            var path = ResolvePath(package.PayloadDirectory, file.Path);
            EnsureNoLinks(package.PayloadDirectory, path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.Size) throw new InvalidDataException("更新文件缺失或大小不匹配：" + file.Path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var digest = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            if (!Convert.ToHexString(digest).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新文件校验失败：" + file.Path);
        }
        if (!paths.Contains(manifest.Executable)) throw new InvalidDataException("更新包缺少主程序。");
        var actualFiles = Directory.EnumerateFiles(package.PayloadDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(package.PayloadDirectory, path).Replace('\\', '/'));
        if (actualFiles.Any(path => path != "app-manifest.json" && !paths.Contains(path))) throw new InvalidDataException("更新包中有未列入清单的文件。");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(ResolvePath(package.PayloadDirectory, manifest.Executable), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    internal static string ResolvePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') || relative.Contains('\0')
            || Path.IsPathRooted(relative) || relative.Split('/').Any(x => x is "" or "." or "..")
            || relative.Split('/')[0].ToLowerInvariant() is "data" or "files" or "images" or "qr-http" or ".git")
            throw new InvalidDataException("更新包包含不安全路径。");
        var prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidDataException("更新路径超出目录。");
        return path;
    }

    internal static void EnsureNoLinks(string root, string path)
    {
        var current = path;
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新目录不能包含链接。");
            if (Path.GetFullPath(current) == Path.GetFullPath(root)) return;
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidDataException("更新目录无效。");
    }
}
