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
        Check(AppPaths.ResolveBaseDirectory("/Applications/Feilv.app/Contents/MacOS/feilv", "/fallback", false, true, "/users/me/Library/Application Support")
            == Path.Combine("/users/me/Library/Application Support", "Feilv")
            && AppPaths.ResolveBaseDirectory("/tmp/.mount_feilv/usr/bin/feilv", "/fallback", false, false, "/users/me/.local/share", "/users/me/Feilv.AppImage")
                == Path.Combine("/users/me/.local/share", "Feilv"), "macOS bundles and AppImage keep writable data in the user's directory");
        Check(AppPaths.ResolveBaseDirectory("/portable/feilv", "/fallback", false, false, "/users/me/.local/share") == Path.GetDirectoryName("/portable/feilv")
            && AppPaths.ResolveBaseDirectory(Path.Combine(directory, "飞驴.exe"), "/fallback", true, false, directory) == directory,
            "Windows and existing portable builds keep their data alongside the executable");
        Check(config.Profile.StartWithWindows && config.Profile.Theme == "System", "new configuration defaults to Windows startup and the system theme");
        Check(config.Profile.AutoReceive, "new configuration enables automatic file and folder reception by default");
        config.Save(directory);
        config.Profile.StartWithWindows = false;
        config.Profile.AutoReceive = false;
        config.Save(directory);
        Check(!XmlConfiguration.Load(directory).Profile.StartWithWindows, "explicit startup opt-out survives XML roundtrip");
        Check(!XmlConfiguration.Load(directory).Profile.AutoReceive, "automatic reception can be disabled and persists in XML");
        var legacy = XDocument.Load(AppPaths.Configuration(directory));
        legacy.Root!.Element("Profile")!.Element("StartWithWindows")!.Remove();
        legacy.Root!.Element("Profile")!.Element("AutoReceive")!.Remove();
        legacy.Save(AppPaths.Configuration(directory));
        Check(XmlConfiguration.Load(directory).Profile.StartWithWindows, "older XML without the startup option uses the enabled default");
        Check(XmlConfiguration.Load(directory).Profile.AutoReceive, "older configurations without an automatic receive option use the enabled default");
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
