using LanChat.Infrastructure.Updates;

if (args is ["--apply-update", var plan]) return await UpdateInstaller.RunHelperAsync(plan);
var readyIndex = Array.IndexOf(args, "--update-ready");
if (readyIndex >= 0) { UpdateInstaller.SignalReady(args[readyIndex + 1]); return 0; }
if (args is ["--parent", var stop])
{
    await File.WriteAllTextAsync(stop + ".started", "started");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    while (!File.Exists(stop)) await Task.Delay(50, timeout.Token);
    return 0;
}
return 2;
