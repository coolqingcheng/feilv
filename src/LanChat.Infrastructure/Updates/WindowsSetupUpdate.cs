using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using LanChat.Core.Models;

namespace LanChat.Infrastructure.Updates;

internal static class WindowsSetupUpdate
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AFBE1865-4656-49BA-9B10-BCFD5767B137}_is1";

    internal static async Task<string> PrepareHelperAsync(PreparedAppUpdate package, string target, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!Path.GetFileName(target).Equals("飞驴.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("请退出客户端后手动运行安装包，安装版主程序名称应为飞驴.exe。");
        var root = Path.GetDirectoryName(target)!;
        var manifest = await ReadManifestAsync(root, ct).ConfigureAwait(false);
        var helper = Path.Combine(Path.GetDirectoryName(package.PayloadDirectory)!, "helper");
        Directory.CreateDirectory(helper);
        foreach (var file in manifest.Files)
        {
            var source = file.Path == manifest.Executable ? target : UpdatePackage.ResolvePath(root, file.Path);
            var destination = UpdatePackage.ResolvePath(helper, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            await input.CopyToAsync(output, 65536, ct).ConfigureAwait(false);
        }
        await using (var output = File.Create(Path.Combine(helper, "app-manifest.json")))
            await JsonSerializer.SerializeAsync(output, manifest, UpdateJsonContext.Default.PackageManifest, ct).ConfigureAwait(false);
        var helperPackage = new PreparedAppUpdate(new(manifest.Version, manifest.Runtime, "distribution", "", 0), helper, manifest);
        await UpdatePackage.ValidateAsync(helperPackage, ct).ConfigureAwait(false);
        return UpdatePackage.ResolvePath(helper, manifest.Executable);
    }

    internal static async Task<List<UpdateInstaller.ReplacedFile>> InstallAsync(UpdateInstallPlan plan, string backupDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var root = Path.GetDirectoryName(plan.TargetExecutable)!;
        var old = await ReadManifestAsync(root, CancellationToken.None).ConfigureAwait(false);
        var previous = new List<UpdateInstaller.ReplacedFile>();
        foreach (var relative in old.Files.Select(x => x.Path).Append("app-manifest.json")
            .Concat(Directory.EnumerateFiles(root, "unins*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName)!))
        {
            if (relative is null) continue;
            var target = UpdatePackage.ResolvePath(root, relative);
            UpdatePackage.EnsureNoLinks(root, target);
            var backup = UpdatePackage.ResolvePath(backupDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(target, backup, true);
            previous.Add(new(target, backup));
        }
        try
        {
            var setup = new ProcessStartInfo(UpdatePackage.ResolvePath(plan.Package.PayloadDirectory, plan.Package.Manifest.Executable))
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = plan.Package.PayloadDirectory };
            foreach (var argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/NOICONS", "/DIR=" + root,
                "/LOG=" + Path.Combine(Path.GetDirectoryName(plan.Package.PayloadDirectory)!, "setup.log") }) setup.ArgumentList.Add(argument);
            using var process = Process.Start(setup) ?? throw new IOException("无法启动已校验的安装程序。");
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("安装程序未完成，退出代码：" + process.ExitCode);
            var installed = await ReadManifestAsync(root, CancellationToken.None).ConfigureAwait(false);
            if (installed.Version != plan.Package.Release.Version || installed.Runtime != plan.Package.Release.Runtime)
                throw new InvalidDataException("安装后的程序版本不匹配。");
            foreach (var file in installed.Files)
            {
                var target = UpdatePackage.ResolvePath(root, file.Path);
                if (!previous.Any(x => x.Target.Equals(target, StringComparison.OrdinalIgnoreCase))) previous.Add(new(target, null));
            }
            await UpdatePackage.VerifyDistributionAsync(root).ConfigureAwait(false);
            return previous;
        }
        catch { UpdateInstaller.Rollback(previous); RestoreRegistration(); throw; }
    }

    internal static void RestoreRegistration()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, true);
        key?.SetValue("DisplayVersion", typeof(UpdateInstaller).Assembly.GetName().Version!.ToString(3));
    }

    private static async Task<PackageManifest> ReadManifestAsync(string root, CancellationToken ct)
    {
        var path = Path.Combine(root, "app-manifest.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 512 * 1024) throw new InvalidDataException("安装目录缺少有效文件清单。");
        await using var input = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync(input, UpdateJsonContext.Default.PackageManifest, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("安装目录的文件清单为空。");
        if (manifest.Files.Count is < 1 or > 256 || manifest.Executable != "飞驴.exe") throw new InvalidDataException("安装目录的程序信息无效。");
        return manifest;
    }
}
