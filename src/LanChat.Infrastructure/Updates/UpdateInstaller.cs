using System.Diagnostics;
using System.Text.Json;
using LanChat.Core.Models;

namespace LanChat.Infrastructure.Updates;

public sealed record UpdateInstallPlan(PreparedAppUpdate Package, string TargetExecutable, int ParentProcessId, long ParentStartTime, string[] Arguments);

public static class UpdateInstaller
{
    public static async Task StartAsync(PreparedAppUpdate package, CancellationToken ct = default)
    {
        await UpdatePackage.ValidateAsync(package, ct).ConfigureAwait(false);
        var target = Environment.ProcessPath ?? throw new IOException("无法定位当前程序。");
        if (Path.GetFileNameWithoutExtension(target) is "dotnet" or "LanChat.IntegrationTests" || File.Exists(Path.Combine(Path.GetDirectoryName(target)!, "LanChat.Desktop.dll")))
            throw new IOException("开发环境不能替换程序，请使用正式发行版。");
        using var current = Process.GetCurrentProcess();
        var plan = new UpdateInstallPlan(package, target, current.Id, current.StartTime.ToUniversalTime().Ticks, Environment.GetCommandLineArgs()[1..]);
        var planPath = Path.Combine(Path.GetDirectoryName(package.PayloadDirectory)!, "install.json");
        await using (var stream = new FileStream(planPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            await JsonSerializer.SerializeAsync(stream, plan, UpdateJsonContext.Default.UpdateInstallPlan, ct).ConfigureAwait(false);
        var start = new ProcessStartInfo(UpdatePackage.ResolvePath(package.PayloadDirectory, package.Manifest.Executable))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = package.PayloadDirectory };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(planPath);
        var acknowledged = Path.Combine(Path.GetDirectoryName(planPath)!, "helper-ready");
        File.Delete(acknowledged);
        File.Delete(Path.Combine(Path.GetDirectoryName(planPath)!, "apply-approved"));
        using var helper = Process.Start(start) ?? throw new IOException("无法启动更新助手。");
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(acknowledged))
        {
            if (helper.HasExited || deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("更新助手未就绪，当前程序继续运行。");
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(planPath)!, "apply-approved"), "approved", ct).ConfigureAwait(false);
    }

    public static async Task<int> RunHelperAsync(string planPath)
    {
        string? directory = null;
        try
        {
            planPath = Path.GetFullPath(planPath);
            if (Path.GetFileName(planPath) != "install.json" || new FileInfo(planPath).Length > 1024 * 1024) throw new InvalidDataException("更新指令无效。");
            directory = Path.GetDirectoryName(planPath)!;
            await using var stream = File.OpenRead(planPath);
            var plan = await JsonSerializer.DeserializeAsync(stream, UpdateJsonContext.Default.UpdateInstallPlan).ConfigureAwait(false) ?? throw new InvalidDataException("更新指令为空。");
            var package = plan.Package;
            if (Path.GetDirectoryName(package.PayloadDirectory) != directory || Path.GetFileName(package.PayloadDirectory) != "payload"
                || Path.GetFileName(Path.GetDirectoryName(directory)) != "Updates"
                || Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(directory))) != "Data"
                || Path.GetFullPath(Environment.ProcessPath!) != UpdatePackage.ResolvePath(package.PayloadDirectory, package.Manifest.Executable))
                throw new InvalidDataException("更新助手目录无效。");
            UpdatePackage.EnsureNoLinks(Path.GetDirectoryName(Path.GetDirectoryName(directory))!, package.PayloadDirectory);
            await UpdatePackage.ValidateAsync(package).ConfigureAwait(false);
            using (var parent = Process.GetProcessById(plan.ParentProcessId))
            {
                if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartTime || !Path.GetFullPath(parent.MainModule!.FileName).Equals(Path.GetFullPath(plan.TargetExecutable), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidDataException("等待退出的程序身份不匹配。");
                await File.WriteAllTextAsync(Path.Combine(directory, "helper-ready"), "ready").ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await parent.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            if (!File.Exists(Path.Combine(directory, "apply-approved"))) throw new IOException("更新请求未确认，保留当前版本。");
            var backup = Path.Combine(directory, "backup");
            List<ReplacedFile> transaction;
            try { transaction = ApplyFiles(package, plan.TargetExecutable, backup); }
            catch { using var restored = Restart(plan, null); throw; }
            var ready = Path.Combine(directory, "app-ready");
            File.Delete(ready);
            try
            {
                using var child = Restart(plan, ready);
                var clock = Stopwatch.StartNew();
                while (!File.Exists(ready) && !child.HasExited && clock.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(150).ConfigureAwait(false);
                if (!File.Exists(ready) && child.HasExited) throw new IOException("新版启动失败，已恢复原程序。");
                await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), File.Exists(ready) ? "更新成功" : "新版已启动，保留备份等待确认").ConfigureAwait(false);
            }
            catch
            {
                Rollback(transaction);
                using var restored = Restart(plan, null);
                throw;
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (directory is not null) try { await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "更新失败：" + ex.Message).ConfigureAwait(false); } catch (IOException) { }
            return 1;
        }
    }

    internal sealed record ReplacedFile(string Target, string? Backup);

    internal static List<ReplacedFile> ApplyFiles(PreparedAppUpdate package, string targetExecutable, string backupDirectory, Action<string>? beforeReplace = null)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(targetExecutable))!;
        var replaced = new List<ReplacedFile>();
        try
        {
            foreach (var file in package.Manifest.Files.OrderBy(x => x.Path == package.Manifest.Executable))
            {
                var relative = file.Path == package.Manifest.Executable ? Path.GetFileName(targetExecutable) : file.Path;
                var target = UpdatePackage.ResolvePath(root, relative);
                UpdatePackage.EnsureNoLinks(root, target);
                beforeReplace?.Invoke(relative);
                var backup = File.Exists(target) ? UpdatePackage.ResolvePath(backupDirectory, relative) : null;
                if (backup is not null) { Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(target, backup, true); }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = target + ".feilu-update";
                try
                {
                    File.Copy(UpdatePackage.ResolvePath(package.PayloadDirectory, file.Path), temporary, true);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(UpdatePackage.ResolvePath(package.PayloadDirectory, file.Path)));
                    File.Move(temporary, target, true);
                }
                finally { File.Delete(temporary); }
                replaced.Add(new ReplacedFile(target, backup));
            }
            var manifest = Path.Combine(root, "app-manifest.json");
            var manifestBackup = File.Exists(manifest) ? Path.Combine(backupDirectory, "app-manifest.json") : null;
            if (manifestBackup is not null) File.Copy(manifest, manifestBackup, true);
            File.Copy(Path.Combine(package.PayloadDirectory, "app-manifest.json"), manifest, true);
            replaced.Add(new ReplacedFile(manifest, manifestBackup));
            return replaced;
        }
        catch { Rollback(replaced); throw; }
    }

    internal static void Rollback(List<ReplacedFile> replaced)
    {
        foreach (var file in replaced.AsEnumerable().Reverse())
        {
            if (file.Backup is null) File.Delete(file.Target);
            else File.Copy(file.Backup, file.Target, true);
        }
    }

    private static Process Restart(UpdateInstallPlan plan, string? ready)
    {
        var start = new ProcessStartInfo(plan.TargetExecutable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(plan.TargetExecutable)! };
        foreach (var argument in plan.Arguments) start.ArgumentList.Add(argument);
        if (ready is not null) { start.ArgumentList.Add("--update-ready"); start.ArgumentList.Add(ready); }
        return Process.Start(start) ?? throw new IOException("无法重新启动飞驴。");
    }

    public static void SignalReady(string? path)
    {
        if (path is null || Path.GetFileName(path) != "app-ready" || !File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "install.json"))) return;
        try { File.WriteAllText(path, "ready"); } catch (IOException) { }
    }
}
