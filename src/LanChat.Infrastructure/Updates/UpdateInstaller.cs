using System.Diagnostics;
using System.Text.Json;
using LanChat.Core.Models;
using LanChat.Core.Localization;

namespace LanChat.Infrastructure.Updates;

public sealed record UpdateInstallPlan(PreparedAppUpdate Package, string TargetExecutable, int ParentProcessId, long ParentStartTime, string[] Arguments)
{
    public string? ParentExecutable { get; init; }
    public string? AppImageExtractAndRun { get; init; }
}

public static class UpdateInstaller
{
    public static async Task StartAsync(PreparedAppUpdate package, CancellationToken ct = default, bool startHidden = false, bool startMaximized = false)
    {
        var timing = new UpdateTiming(Path.GetDirectoryName(package.PayloadDirectory)!, "prepare-timing.log");
        await timing.RecordAsync("confirmation-received").ConfigureAwait(false);
        await UpdatePackage.ValidateAsync(package, ct).ConfigureAwait(false);
        if (AppReleases.IsDiskImage(package.Release)) await MacBundleUpdate.ValidateAsync(package, ct).ConfigureAwait(false);
        await timing.RecordAsync("package-revalidated").ConfigureAwait(false);
        var executable = Environment.ProcessPath ?? throw new IOException("无法定位当前程序。");
        if (Path.GetFileNameWithoutExtension(executable) is "dotnet" or "LanChat.IntegrationTests" || File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "LanChat.Desktop.dll")))
            throw new IOException("开发环境不能替换程序，请使用正式发行版。");
        var isAppImage = AppReleases.IsAppImage(package.Release);
        var target = isAppImage ? Environment.GetEnvironmentVariable("APPIMAGE") : executable;
        if (isAppImage && (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target) || !target.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(target) || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0))
            throw new IOException("请先运行本地 AppImage 文件，再更新。");
        if (isAppImage) CheckAppImageWritable(target!);
        if (AppReleases.IsDiskImage(package.Release)) MacBundleUpdate.CheckWritable(executable);
        using var current = Process.GetCurrentProcess();
        var arguments = RestartArguments(Environment.GetCommandLineArgs()[1..]);
        if (startHidden) arguments = [.. arguments, "--update-hidden"];
        else if (startMaximized) arguments = [.. arguments, "--update-maximized"];
        var plan = new UpdateInstallPlan(package, target!, current.Id, current.StartTime.ToUniversalTime().Ticks, arguments)
        { ParentExecutable = executable, AppImageExtractAndRun = Environment.GetEnvironmentVariable("APPIMAGE_EXTRACT_AND_RUN") };
        var planPath = Path.Combine(Path.GetDirectoryName(package.PayloadDirectory)!, "install.json");
        await using (var stream = new FileStream(planPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            await JsonSerializer.SerializeAsync(stream, plan, UpdateJsonContext.Default.UpdateInstallPlan, ct).ConfigureAwait(false);
        var helperExecutable = AppReleases.IsInstaller(package.Release)
            ? await WindowsSetupUpdate.PrepareHelperAsync(package, target!, ct).ConfigureAwait(false)
            : UpdatePackage.ResolvePath(package.PayloadDirectory, package.Manifest.Executable);
        await timing.RecordAsync("helper-prepared").ConfigureAwait(false);
        var start = new ProcessStartInfo(helperExecutable)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(helperExecutable)! };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(planPath);
        start.Environment["FEILV_UPDATE_LANGUAGE"] = Localizer.Language;
        if (isAppImage) start.Environment["APPIMAGE_EXTRACT_AND_RUN"] = "1";
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
        await timing.RecordAsync("helper-ready-parent-can-exit").ConfigureAwait(false);
    }

    public static async Task<int> RunHelperAsync(string planPath, IProgress<string>? progress = null)
    {
        string? directory = null;
        try
        {
            planPath = Path.GetFullPath(planPath);
            if (Path.GetFileName(planPath) != "install.json" || new FileInfo(planPath).Length > 1024 * 1024) throw new InvalidDataException("更新指令无效。");
            directory = Path.GetDirectoryName(planPath)!;
            var timing = new UpdateTiming(directory);
            await timing.RecordAsync("helper-started").ConfigureAwait(false);
            progress?.Report("正在准备更新…");
            await using var stream = File.OpenRead(planPath);
            var plan = await JsonSerializer.DeserializeAsync(stream, UpdateJsonContext.Default.UpdateInstallPlan).ConfigureAwait(false) ?? throw new InvalidDataException("更新指令为空。");
            var package = plan.Package;
            var isSetup = AppReleases.IsInstaller(package.Release);
            var isAppImage = AppReleases.IsAppImage(package.Release);
            var isDiskImage = AppReleases.IsDiskImage(package.Release);
            var expectedHelper = isSetup ? Path.Combine(directory, "helper", "飞驴.exe") : UpdatePackage.ResolvePath(package.PayloadDirectory, package.Manifest.Executable);
            var runningHelper = isAppImage ? Environment.GetEnvironmentVariable("APPIMAGE") : Environment.ProcessPath;
            if (Path.GetDirectoryName(package.PayloadDirectory) != directory || Path.GetFileName(package.PayloadDirectory) != "payload"
                || Path.GetFileName(Path.GetDirectoryName(directory)) != "Updates"
                || Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(directory))) != "Data"
                || string.IsNullOrEmpty(runningHelper) || Path.GetFullPath(runningHelper) != expectedHelper)
                throw new InvalidDataException("更新助手目录无效。");
            UpdatePackage.EnsureNoLinks(Path.GetDirectoryName(Path.GetDirectoryName(directory))!, package.PayloadDirectory);
            await UpdatePackage.ValidateAsync(package).ConfigureAwait(false);
            if (isDiskImage) await MacBundleUpdate.ValidateAsync(package).ConfigureAwait(false);
            await timing.RecordAsync("package-revalidated").ConfigureAwait(false);
            using (var parent = Process.GetProcessById(plan.ParentProcessId))
            {
                // Linux derives process start time from boot time, which can vary slightly between reads.
                var tolerance = OperatingSystem.IsLinux() ? TimeSpan.FromSeconds(2).Ticks : 0;
                var startDifference = Math.Abs((decimal)parent.StartTime.ToUniversalTime().Ticks - plan.ParentStartTime);
                if (plan.ParentStartTime <= 0 || startDifference > tolerance)
                    throw new InvalidDataException($"等待退出的程序启动时间不匹配（差 {startDifference} ticks）。");
                if (!Path.GetFullPath(parent.MainModule!.FileName).Equals(Path.GetFullPath(plan.ParentExecutable ?? plan.TargetExecutable), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidDataException("等待退出的程序路径不匹配。");
                await File.WriteAllTextAsync(Path.Combine(directory, "helper-ready"), "ready").ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await parent.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            await timing.RecordAsync("parent-exited").ConfigureAwait(false);
            if (!File.Exists(Path.Combine(directory, "apply-approved"))) throw new IOException("更新请求未确认，保留当前版本。");
            var backup = Path.Combine(directory, "backup");
            List<ReplacedFile> transaction;
            MacBundleUpdate.Swap? macSwap = null;
            progress?.Report("正在安装更新，请稍候…");
            try
            {
                if (isDiskImage) { macSwap = await MacBundleUpdate.InstallAsync(package, plan.TargetExecutable).ConfigureAwait(false); transaction = []; }
                else if (isAppImage) transaction = ApplyAppImage(package, plan.TargetExecutable, backup);
                else transaction = isSetup ? await WindowsSetupUpdate.InstallAsync(plan, backup, timing).ConfigureAwait(false) : ApplyFiles(package, plan.TargetExecutable, backup);
            }
            catch { using var restored = Restart(plan, null); throw; }
            await timing.RecordAsync("files-installed-and-verified").ConfigureAwait(false);
            var ready = Path.Combine(directory, "app-ready");
            File.Delete(ready);
            try
            {
                progress?.Report("正在重新启动飞驴…");
                using var child = Restart(plan, ready);
                await timing.RecordAsync("application-process-started").ConfigureAwait(false);
                var clock = Stopwatch.StartNew();
                while (!File.Exists(ready) && !child.HasExited && clock.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(150).ConfigureAwait(false);
                if (!File.Exists(ready) && child.HasExited) throw new IOException("新版启动失败，已恢复原程序。");
                await timing.RecordAsync(File.Exists(ready) ? "application-ready" : "application-readiness-timeout").ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), File.Exists(ready) ? "更新成功" : "新版已启动，保留备份等待确认").ConfigureAwait(false);
                if (File.Exists(ready) && macSwap is not null)
                    try { Directory.Delete(macSwap.Backup, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            catch
            {
                if (macSwap is not null) MacBundleUpdate.Rollback(macSwap); else Rollback(transaction);
                if (isSetup) WindowsSetupUpdate.RestoreRegistration();
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

    internal static void CheckAppImageWritable(string target)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(target))!;
        var probe = Path.Combine(directory, ".feilv-write-" + Guid.NewGuid().ToString("N"));
        try { using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new IOException("无权限更新应用，请手动更新。", ex); }
        finally { if (File.Exists(probe)) File.Delete(probe); }
    }

    internal static List<ReplacedFile> ApplyAppImage(PreparedAppUpdate package, string targetExecutable, string backupDirectory)
    {
        var target = Path.GetFullPath(targetExecutable);
        if (!target.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)
            || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new IOException("AppImage 更新目标无效。");
        Directory.CreateDirectory(backupDirectory);
        var backup = Path.Combine(backupDirectory, "previous.AppImage");
        File.Copy(target, backup, true);
        var temporary = target + ".feilu-update";
        try
        {
            File.Copy(UpdatePackage.ResolvePath(package.PayloadDirectory, package.Manifest.Executable), temporary, true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(target));
            File.Move(temporary, target, true);
        }
        finally { File.Delete(temporary); }
        return [new ReplacedFile(target, backup)];
    }

    internal static string[] RestartArguments(string[] arguments)
    {
        var result = new List<string>();
        for (var i = 0; i < arguments.Length; i++)
        {
            if (arguments[i] == "--update-ready") { i++; continue; }
            if (arguments[i] is "--update-hidden" or "--update-maximized") continue;
            result.Add(arguments[i]);
        }
        return result.ToArray();
    }

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
            replaced.Add(new ReplacedFile(manifest, manifestBackup));
            try
            {
                File.Copy(Path.Combine(package.PayloadDirectory, "app-manifest.json"), manifest + ".feilu-update", true);
                File.Move(manifest + ".feilu-update", manifest, true);
            }
            finally { File.Delete(manifest + ".feilu-update"); }
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
        if (AppReleases.IsAppImage(plan.Package.Release))
        {
            start.Environment.Remove("APPIMAGE"); start.Environment.Remove("APPDIR"); start.Environment.Remove("ARGV0");
            if (plan.AppImageExtractAndRun is null) start.Environment.Remove("APPIMAGE_EXTRACT_AND_RUN");
            else start.Environment["APPIMAGE_EXTRACT_AND_RUN"] = plan.AppImageExtractAndRun;
        }
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
