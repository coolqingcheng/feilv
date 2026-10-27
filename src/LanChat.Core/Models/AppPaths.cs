using System.Globalization;

namespace LanChat.Core.Models;

public static class AppPaths
{
    public static string DefaultBaseDirectory => Environment.ProcessPath is { } processPath
        && !Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory
            : AppContext.BaseDirectory;

    public static string DailyMedia(string baseDirectory, bool image, string category) => Path.Combine(
        baseDirectory, image ? "Images" : "Files", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), category);
    public static string Configuration(string baseDirectory) => Path.Combine(baseDirectory, "Data", "config.xml");
}
