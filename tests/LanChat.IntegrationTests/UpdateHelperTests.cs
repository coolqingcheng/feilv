using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LanChat.Core.Models;
using LanChat.Infrastructure.Updates;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static async Task RunUpdateHelperTestsAsync()
    {
        Check(UpdateInstaller.RestartArguments(["--data-dir", "有空格的目录", "--update-ready", "previous-marker", "--update-hidden", "--update-maximized", "--port", "45832"])
            .SequenceEqual(new[] { "--data-dir", "有空格的目录", "--port", "45832" }), "a later update preserves user arguments while removing the previous update readiness marker");
        var root = Path.Combine(TestRoot, "helper transaction 有空格"); Directory.CreateDirectory(root);
        var fixture = Path.Combine(AppContext.BaseDirectory, "UpdateFixture");
        var staging = Path.Combine(root, "Data", "Updates", "fixture");
        var payload = Path.Combine(staging, "payload"); Directory.CreateDirectory(payload);
        var files = new List<PackageFile>();
        foreach (var path in Directory.EnumerateFiles(fixture))
        {
            var name = Path.GetFileName(path);
            File.Copy(path, Path.Combine(root, name)); File.Copy(path, Path.Combine(payload, name));
            await using var stream = File.OpenRead(path);
            files.Add(new(name, stream.Length, Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant()));
        }
        var executable = OperatingSystem.IsWindows() ? "LanChat.UpdateFixture.exe" : "LanChat.UpdateFixture";
        var sentinel = Path.Combine(payload, "new-library.txt");
        await File.WriteAllTextAsync(sentinel, "new version");
        files.Add(new("new-library.txt", new FileInfo(sentinel).Length, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sentinel))).ToLowerInvariant()));
        var manifest = new PackageManifest("9.8.7", AppReleases.Runtime, executable, files);
        await File.WriteAllTextAsync(Path.Combine(payload, "app-manifest.json"), JsonSerializer.Serialize(manifest, UpdateJsonContext.Default.PackageManifest));
        var package = new PreparedAppUpdate(new("9.8.7", AppReleases.Runtime, "fixture", "", 0), payload, manifest);
        await UpdatePackage.ValidateAsync(package);
        var parentStop = Path.Combine(root, "stop-parent");
        var target = Path.Combine(root, executable);
        var parentInfo = new ProcessStartInfo(target) { UseShellExecute = false, CreateNoWindow = true };
        parentInfo.ArgumentList.Add("--parent"); parentInfo.ArgumentList.Add(parentStop);
        using var parent = Process.Start(parentInfo)!;
        await WaitForFileAsync(parentStop + ".started");
        var plan = new UpdateInstallPlan(package, target, parent.Id, parent.StartTime.ToUniversalTime().Ticks, ["--parent", parentStop]);
        var planPath = Path.Combine(staging, "install.json");
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan, UpdateJsonContext.Default.UpdateInstallPlan));
        var helperInfo = new ProcessStartInfo(Path.Combine(payload, executable)) { UseShellExecute = false, CreateNoWindow = true };
        helperInfo.ArgumentList.Add("--apply-update"); helperInfo.ArgumentList.Add(planPath);
        using var helper = Process.Start(helperInfo)!;
        await WaitForFileAsync(Path.Combine(staging, "helper-ready"));
        Check(!File.Exists(Path.Combine(root, "new-library.txt")) && !parent.HasExited, "the real updater helper acknowledges readiness and waits for the original process without modifying its files");
        await File.WriteAllTextAsync(Path.Combine(staging, "apply-approved"), "approved");
        await File.WriteAllTextAsync(parentStop, "stop");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await helper.WaitForExitAsync(deadline.Token);
        Check(helper.ExitCode == 0 && File.Exists(Path.Combine(staging, "app-ready")) && await File.ReadAllTextAsync(Path.Combine(root, "new-library.txt")) == "new version",
            "the real helper replaces a stopped application, restarts with preserved Unicode arguments and receives the new process readiness signal");
        Check(File.Exists(Path.Combine(staging, "backup", executable)) && await File.ReadAllTextAsync(Path.Combine(staging, "result.txt")) == "更新成功", "a successful installation retains its previous executable and writes a completion result");
        var timing = await File.ReadAllTextAsync(Path.Combine(staging, "update-timing.log"));
        Check(timing.Contains("parent-exited", StringComparison.Ordinal) && timing.Contains("files-installed-and-verified", StringComparison.Ordinal)
            && timing.Contains("application-ready", StringComparison.Ordinal), "update timing logs distinguish parent exit, installation and application readiness");
    }

    private static async Task WaitForFileAsync(string path)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Timed out waiting for " + path);
            await Task.Delay(50);
        }
    }
}
