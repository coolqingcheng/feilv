using Avalonia;
using LanChat.Core.Models;
using LanChat.Desktop.Services;
using LanChat.Infrastructure.Storage;
using LanChat.Infrastructure.Updates;

namespace LanChat.Desktop;

internal static class Program
{
    internal static NodeOptions Options { get; private set; } = new();
    internal static SingleInstanceService? Instance { get; private set; }
    internal static string? UpdateReadyPath { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        if (args is ["--verify-package"])
        {
            try { UpdatePackage.VerifyDistributionAsync(AppContext.BaseDirectory).GetAwaiter().GetResult(); }
            catch { Environment.ExitCode = 1; }
            return;
        }
        if (args is ["--apply-update", var plan]) { Environment.ExitCode = UpdateInstaller.RunHelperAsync(plan).GetAwaiter().GetResult(); return; }
        using var instance = new SingleInstanceService();
        if (!instance.TryStart())
        {
            if (instance.ActivateExistingAsync().GetAwaiter().GetResult() || !instance.TryStart()) return;
        }
        Instance = instance;
        string? Value(string key) => Array.IndexOf(args, key) is var index && index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        var baseDirectory = Value("--data-dir") ?? AppPaths.DefaultBaseDirectory;
        UpdateReadyPath = Value("--update-ready");
        var config = XmlConfiguration.Load(baseDirectory);
        Options = new NodeOptions
        {
            DataDirectory = baseDirectory,
            ListenPort = int.TryParse(Value("--port"), out var port) ? port : config.ListenPort,
            DiscoveryPort = int.TryParse(Value("--discovery-port"), out var discoveryPort) ? discoveryPort : config.DiscoveryPort,
            EnableDiscovery = config.EnableDiscovery && !args.Contains("--no-discovery")
        };
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { Instance = null; }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
