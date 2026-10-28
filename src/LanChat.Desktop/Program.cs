using Avalonia;
using LanChat.Core.Models;
using LanChat.Core.Localization;
using LanChat.Desktop.Services;
using LanChat.Infrastructure.Storage;
using LanChat.Infrastructure.Updates;

namespace LanChat.Desktop;

internal static class Program
{
    internal static NodeOptions Options { get; private set; } = new();
    internal static SingleInstanceService? Instance { get; private set; }
    internal static string? UpdateReadyPath { get; private set; }
    internal static bool UpdateStartHidden { get; private set; }
    internal static bool UpdateStartMaximized { get; private set; }
    internal static bool IsSmokeTest { get; private set; }
    internal static string? UpdatePlanPath { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        if (args is ["--verify-package"])
        {
            try { UpdatePackage.VerifyDistributionAsync(AppContext.BaseDirectory).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "verify-error.txt"), ex.ToString());
                Environment.ExitCode = 1;
            }
            return;
        }
        if (args is ["--apply-update", var plan])
        {
            UpdatePlanPath = plan;
            Localizer.SetPreference(Environment.GetEnvironmentVariable("FEILV_UPDATE_LANGUAGE"));
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return;
        }
        using var instance = new SingleInstanceService();
        if (!instance.TryStart())
        {
            if (instance.ActivateExistingAsync().GetAwaiter().GetResult() || !instance.TryStart()) return;
        }
        Instance = instance;
        using var installerMutex = OperatingSystem.IsWindows() ? new Mutex(false, @"Local\Feilv.Application") : null;
        string? Value(string key) => Array.IndexOf(args, key) is var index && index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        IsSmokeTest = args.Contains("--smoke-test");
        var baseDirectory = Value("--data-dir") ?? AppPaths.DefaultBaseDirectory;
        UpdateReadyPath = Value("--update-ready");
        UpdateStartHidden = args.Contains("--update-hidden") && UpdateReadyPath is not null;
        UpdateStartMaximized = args.Contains("--update-maximized") && UpdateReadyPath is not null && !UpdateStartHidden;
        var config = XmlConfiguration.Load(baseDirectory);
        Localizer.SetPreference(config.Profile.Language);
        Options = new NodeOptions
        {
            DataDirectory = baseDirectory,
            ListenPort = int.TryParse(Value("--port"), out var port) ? port : config.ListenPort,
            DiscoveryPort = int.TryParse(Value("--discovery-port"), out var discoveryPort) ? discoveryPort : config.DiscoveryPort,
            EnableDiscovery = config.EnableDiscovery && !args.Contains("--no-discovery") && !IsSmokeTest
        };
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { Instance = null; }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
