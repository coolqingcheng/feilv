using System.Diagnostics;
using LanChat.Desktop.Services;

namespace LanChat.IntegrationTests;

internal static class SingleInstanceTests
{
    public static int RunProbe(string[] args)
    {
        using var instance = new SingleInstanceService(args[1]);
        if (!instance.TryStart()) return instance.ActivateExistingAsync().GetAwaiter().GetResult() ? 10 : 11;
        File.WriteAllText(args[2], Environment.ProcessId.ToString());
        Thread.Sleep(int.Parse(args[5]));
        instance.SetActivationHandler(() => File.AppendAllText(args[3], "activate\n"));
        var timer = Stopwatch.StartNew();
        while (!File.Exists(args[4]) && timer.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(10);
        return File.Exists(args[4]) ? 0 : 12;
    }

    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "single-instance");
        Directory.CreateDirectory(directory);
        var key = Guid.NewGuid().ToString("N");
        var ready = Path.Combine(directory, "ready");
        var activation = Path.Combine(directory, "activation");
        var stop = Path.Combine(directory, "stop");
        using var primary = StartProbe(key, ready, activation, stop, 1500);
        try
        {
            await WaitUntilAsync(() => File.Exists(ready));
            using var second = StartProbe(key, ready, activation, stop);
            await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            check(second.ExitCode == 10 && !primary.HasExited, "second process notifies primary and exits");
            await WaitUntilAsync(() => File.Exists(activation));
            check(File.ReadAllLines(activation).Length == 1, "activation queued before UI is ready");

            var duplicates = Enumerable.Range(0, 4).Select(_ => StartProbe(key, ready, activation, stop)).ToArray();
            try
            {
                await Task.WhenAll(duplicates.Select(x => x.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(12));
                check(duplicates.All(x => x.ExitCode == 10) && !primary.HasExited, "concurrent launches retain one primary process");
                await WaitUntilAsync(() => File.ReadAllLines(activation).Length == 5);
                check(true, "each duplicate launch requests window activation");
            }
            finally { foreach (var duplicate in duplicates) { Stop(duplicate); duplicate.Dispose(); } }

            File.WriteAllText(stop, "stop");
            await primary.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            check(primary.ExitCode == 0, "single-instance listener stops cleanly");

            var crashReady = Path.Combine(directory, "crash-ready");
            var crashStop = Path.Combine(directory, "crash-stop");
            using var crash = StartProbe(key, crashReady, activation, crashStop);
            try
            {
                await WaitUntilAsync(() => File.Exists(crashReady));
                check(!crash.HasExited, "client can reopen after normal shutdown");
                crash.Kill();
                await crash.WaitForExitAsync();
            }
            finally { Stop(crash); }

            var recoveredReady = Path.Combine(directory, "recovered-ready");
            var recoveredStop = Path.Combine(directory, "recovered-stop");
            using var recovered = StartProbe(key, recoveredReady, activation, recoveredStop);
            try
            {
                await WaitUntilAsync(() => File.Exists(recoveredReady));
                check(!recovered.HasExited, "client can reopen after unexpected process exit");
                File.WriteAllText(recoveredStop, "stop");
                await recovered.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { Stop(recovered); }
        }
        finally { Stop(primary); }
    }

    private static Process StartProbe(string key, string ready, string activation, string stop, int registrationDelay = 0)
    {
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate test executable.");
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LanChat.IntegrationTests.dll"));
        foreach (var argument in new[] { "--instance-probe", key, ready, activation, stop, registrationDelay.ToString() }) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Cannot start instance test process.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private static void Stop(Process process)
    {
        if (process.HasExited) return;
        process.Kill(); process.WaitForExit();
    }
}
