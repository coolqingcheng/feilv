using Avalonia;
using LanChat.Core.Models;
using LanChat.Desktop.Services;
using LanChat.Infrastructure.Storage;

namespace LanChat.Desktop;

internal static class Program
{
    internal static NodeOptions Options { get; private set; } = new();
    internal static SingleInstanceService? Instance { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        using var instance = new SingleInstanceService();
        if (!instance.TryStart())
        {
            if (instance.ActivateExistingAsync().GetAwaiter().GetResult() || !instance.TryStart()) return;
        }
        Instance = instance;
        string? Value(string key) => Array.IndexOf(args, key) is var index && index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        var baseDirectory = Value("--data-dir") ?? AppPaths.DefaultBaseDirectory;
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
