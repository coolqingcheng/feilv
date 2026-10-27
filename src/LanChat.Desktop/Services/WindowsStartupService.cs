using Microsoft.Win32;

namespace LanChat.Desktop.Services;

public static class WindowsStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FeiLv";

    // Development hosts and test executables must never register themselves at logon.
    public static string? BuildCommand(string? executablePath, string applicationDirectory)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath)
            || executablePath.Contains('"') || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(applicationDirectory, "LanChat.Desktop.dll"))) return null;
        var name = Path.GetFileNameWithoutExtension(executablePath);
        if (!name.Equals("LanChat.Desktop", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("飞驴", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("飞驴-v", StringComparison.OrdinalIgnoreCase)) return null;
        return $"\"{executablePath}\"";
    }

    public static void Apply(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        var command = BuildCommand(Environment.ProcessPath, AppContext.BaseDirectory);
        if (command is null) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new IOException("无法访问当前用户的开机启动项。");
        if (enabled) key.SetValue(ValueName, command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
