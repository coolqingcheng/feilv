using System.Xml.Linq;
using LanChat.Core.Models;
using LanChat.Desktop.Services;
using LanChat.Infrastructure.Storage;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static void RunStartupConfigurationTests()
    {
        var directory = Path.Combine(TestRoot, "startup-config");
        var config = XmlConfiguration.Load(directory);
        Check(config.Profile.StartWithWindows && config.Profile.Theme == "System", "new configuration defaults to Windows startup and the system theme");
        config.Save(directory);
        config.Profile.StartWithWindows = false;
        config.Save(directory);
        Check(!XmlConfiguration.Load(directory).Profile.StartWithWindows, "explicit startup opt-out survives XML roundtrip");
        var legacy = XDocument.Load(AppPaths.Configuration(directory));
        legacy.Root!.Element("Profile")!.Element("StartWithWindows")!.Remove();
        legacy.Save(AppPaths.Configuration(directory));
        Check(XmlConfiguration.Load(directory).Profile.StartWithWindows, "older XML without the startup option uses the enabled default");
        var executable = Path.Combine(directory, "有空格的目录", "飞驴-v1.1.0.exe");
        Check(WindowsStartupService.BuildCommand(executable, directory) == $"\"{executable}\"",
            "startup quotes the actual versioned executable path including spaces and Unicode");
        Check(WindowsStartupService.BuildCommand(Path.Combine(directory, "dotnet.exe"), directory) is null
            && WindowsStartupService.BuildCommand(Path.Combine(directory, "LanChat.IntegrationTests.exe"), directory) is null,
            "development and test hosts cannot register as the application");
        File.WriteAllText(Path.Combine(directory, "LanChat.Desktop.dll"), "development output");
        Check(WindowsStartupService.BuildCommand(Path.Combine(directory, "LanChat.Desktop.exe"), directory) is null,
            "non-published desktop build cannot change the user's startup registration");
    }
}
