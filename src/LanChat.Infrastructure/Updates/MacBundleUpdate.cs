using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using LanChat.Core.Models;

namespace LanChat.Infrastructure.Updates;

internal static class MacBundleUpdate
{
    internal sealed record Swap(string Destination, string Backup);

    internal static string BundlePath(string payload) => Path.Combine(Path.GetDirectoryName(payload)!, "bundle", "Feilv.app");
    private static string ImagePath(string payload) => Path.Combine(Path.GetDirectoryName(payload)!, "package.download");

    internal static async Task ExtractAsync(string image, string payload, AppRelease release, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var mount = Path.Combine(Path.GetDirectoryName(payload)!, "mount");
        Directory.CreateDirectory(mount);
        await RunAsync("/usr/bin/hdiutil", ["attach", "-readonly", "-nobrowse", "-mountpoint", mount, image], ct);
        try
        {
            var source = Path.Combine(mount, "Feilv.app");
            if (!Directory.Exists(source)) throw new InvalidDataException("更新镜像缺少应用程序。");
            var bundle = BundlePath(payload);
            await RunAsync("/usr/bin/ditto", [source, bundle], ct);
            await RunAsync("/usr/bin/ditto", [Path.Combine(bundle, "Contents", "MacOS"), payload], ct);
            File.Copy(Path.Combine(mount, ".app-manifest.json"), Path.Combine(payload, "app-manifest.json"));
            await VerifyBundleAsync(bundle, payload, release, ct);
        }
        finally { await RunAsync("/usr/bin/hdiutil", ["detach", mount, "-quiet"], CancellationToken.None); }
    }

    internal static async Task ValidateAsync(PreparedAppUpdate package, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var image = ImagePath(package.PayloadDirectory);
        var info = new FileInfo(image);
        if (!info.Exists || info.Length != package.Release.Size) throw new InvalidDataException("更新镜像大小不匹配。");
        await using var file = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(file, ct);
        if (!Convert.ToHexString(digest).Equals(package.Release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新镜像校验失败。");
        await VerifyBundleAsync(BundlePath(package.PayloadDirectory), package.PayloadDirectory, package.Release, ct);
    }

    private static async Task VerifyBundleAsync(string bundle, string payload, AppRelease release, CancellationToken ct)
    {
        await RunAsync("/usr/bin/codesign", ["--verify", "--deep", "--strict", bundle], ct);
        var plist = XDocument.Load(Path.Combine(bundle, "Contents", "Info.plist"));
        var entries = plist.Root?.Element("dict")?.Elements().ToArray() ?? [];
        string? Value(string key)
        {
            for (var i = 0; i + 1 < entries.Length; i++) if (entries[i].Name.LocalName == "key" && entries[i].Value == key) return entries[i + 1].Value;
            return null;
        }
        if (Value("CFBundleIdentifier") != "com.github.coolqingcheng.feilv" || Value("CFBundleExecutable") != "feilv" || Value("CFBundleShortVersionString") != release.Version)
            throw new InvalidDataException("更新应用程序信息不匹配。");
        var native = Path.Combine(bundle, "Contents", "MacOS");
        if (new FileInfo(Path.Combine(payload, "app-manifest.json")).Length > 512 * 1024) throw new InvalidDataException("更新清单无效。");
        await using var manifestStream = File.OpenRead(Path.Combine(payload, "app-manifest.json"));
        var manifest = await JsonSerializer.DeserializeAsync(manifestStream, UpdateJsonContext.Default.PackageManifest, ct)
            ?? throw new InvalidDataException("更新清单为空。");
        if (manifest.Executable != "feilv")
            throw new InvalidDataException("更新应用清单不匹配。");
        await UpdatePackage.ValidateAsync(new PreparedAppUpdate(release, native, manifest), ct);
    }

    internal static string GetInstalledBundle(string executable)
    {
        var path = Path.GetFullPath(executable);
        const string suffix = "/Feilv.app/Contents/MacOS/feilv";
        if (!path.EndsWith(suffix, StringComparison.Ordinal)) throw new IOException("请先将飞驴拖入应用程序文件夹，再更新。");
        return path[..^("/Contents/MacOS/feilv".Length)];
    }

    internal static void CheckWritable(string executable)
    {
        var bundle = GetInstalledBundle(executable);
        var parent = Path.GetDirectoryName(bundle)!;
        var probe = Path.Combine(parent, ".feilv-write-" + Guid.NewGuid().ToString("N"));
        try { using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new IOException("无权限更新应用，请手动更新。", ex); }
        finally { if (File.Exists(probe)) File.Delete(probe); }
    }

    internal static async Task<Swap> InstallAsync(PreparedAppUpdate package, string executable)
    {
        var destination = GetInstalledBundle(executable);
        var parent = Path.GetDirectoryName(destination)!;
        var suffix = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(parent, ".feilv-update-" + suffix + ".app");
        var backup = Path.Combine(parent, ".feilv-backup-" + suffix + ".app");
        try
        {
            await RunAsync("/usr/bin/ditto", [BundlePath(package.PayloadDirectory), temporary], CancellationToken.None);
            await RunAsync("/usr/bin/codesign", ["--verify", "--deep", "--strict", temporary], CancellationToken.None);
            Directory.Move(destination, backup);
            try { Directory.Move(temporary, destination); }
            catch { Directory.Move(backup, destination); throw; }
            return new Swap(destination, backup);
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    internal static void Rollback(Swap swap)
    {
        var failed = swap.Destination + ".failed-" + Guid.NewGuid().ToString("N");
        Directory.Move(swap.Destination, failed);
        try { Directory.Move(swap.Backup, swap.Destination); }
        catch { Directory.Move(failed, swap.Destination); throw; }
        Directory.Delete(failed, true);
    }

    internal static async Task RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动系统工具。");
        var stderr = process.StandardError.ReadToEndAsync(ct); var stdout = process.StandardOutput.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
        var error = await stderr; await stdout;
        if (process.ExitCode != 0) throw new IOException("系统工具执行失败：" + error.Trim());
    }
}
