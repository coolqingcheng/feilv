namespace LanChat.Desktop.Services;

public interface IUiServices
{
    Task<string?> PickFileAsync(bool imageOnly = false);
    async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var path = await PickFileAsync();
        return path is null ? [] : [path];
    }
    Task<string?> PickFolderAsync();
    Task<string?> PickDownloadFolderAsync();
    async Task<IReadOnlyList<string>> PickFoldersAsync()
    {
        var folder = await PickFolderAsync();
        return folder is null ? [] : [folder];
    }
    void OpenFile(string path);
    void RevealFile(string path);
    Task CopyTextAsync(string text);
    Task<string?> EditPeerRemarkAsync(string name, string current);
    Task<string?> EditShareNameAsync(string path, string current);
    void SetTheme(string theme);
    void SetStartWithWindows(bool enabled);
}
