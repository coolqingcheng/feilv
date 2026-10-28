using System.Globalization;

namespace LanChat.Core.Models;

public static class AppPaths
{
    public static string DefaultBaseDirectory => ResolveBaseDirectory(Environment.ProcessPath, AppContext.BaseDirectory,
        OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable("APPIMAGE"));

    public static string ResolveBaseDirectory(string? processPath, string fallback, bool windows, bool macOS, string userDataDirectory, string? appImage = null)
    {
        if (!windows && !string.IsNullOrWhiteSpace(userDataDirectory))
        {
            var bundledMacApp = macOS && processPath?.EndsWith("/Feilv.app/Contents/MacOS/feilv", StringComparison.Ordinal) == true;
            if (bundledMacApp || (!macOS && !string.IsNullOrEmpty(appImage))) return Path.Combine(userDataDirectory, "Feilv");
        }
        return processPath is not null && !Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(processPath) ?? fallback : fallback;
    }

    public static string DailyMedia(string baseDirectory, bool image, string category) => Path.Combine(
        baseDirectory, image ? "Images" : "Files", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), category);
    public static string Configuration(string baseDirectory) => Path.Combine(baseDirectory, "Data", "config.xml");
}
