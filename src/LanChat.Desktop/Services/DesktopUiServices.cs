using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Styling;

namespace LanChat.Desktop.Services;

public sealed class DesktopUiServices(Func<Window> window) : IUiServices
{
    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var files = await window().StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要发送的文件", AllowMultiple = true, FileTypeFilter = [FilePickerFileTypes.All]
        });
        return TakePaths(files);
    }

    public async Task<string?> PickFileAsync(bool imageOnly = false)
    {
        var files = await window().StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = imageOnly ? "选择图片" : "选择要发送的文件",
            AllowMultiple = false,
            FileTypeFilter = imageOnly ? [new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif"] }] : [FilePickerFileTypes.All]
        });
        return TakePaths(files).FirstOrDefault();
    }

    public async Task<IReadOnlyList<string>> PickFoldersAsync()
    {
        var folders = await window().StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择要发送的文件夹", AllowMultiple = true });
        return TakePaths(folders);
    }

    public async Task<string?> PickFolderAsync()
    {
        var folders = await window().StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择文件夹", AllowMultiple = false });
        return TakePaths(folders).FirstOrDefault();
    }

    private static string[] TakePaths(IEnumerable<IStorageItem> items)
    {
        var paths = new List<string>();
        foreach (var item in items)
            using (item) if (item.TryGetLocalPath() is { } path) paths.Add(path);
        return paths.ToArray();
    }

    public void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    public Task CopyTextAsync(string text) => window().Clipboard?.SetTextAsync(text) ?? Task.FromException(new IOException("无法访问剪贴板。"));
    public void RevealFile(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("接收的文件或文件夹已被移动或删除。", path);
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true };
            start.ArgumentList.Add("/select,");
            start.ArgumentList.Add(path);
            Process.Start(start)?.Dispose();
        }
        else OpenFile(Path.GetDirectoryName(path)!);
    }
    public void SetTheme(string theme) => Application.Current!.RequestedThemeVariant = theme switch { "Light" => ThemeVariant.Light, "Dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
    public void SetStartWithWindows(bool enabled) => WindowsStartupService.Apply(enabled);
}
