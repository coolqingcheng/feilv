using LanChat.Core.Models;

namespace LanChat.Infrastructure.Files;

public static class SharedFileAccess
{
    public static string Resolve(SharedFolder root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':')) throw new UnauthorizedAccessException("无效的共享路径。");
        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.LocalPath));
        var path = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(rootPath) ? rootPath : rootPath + Path.DirectorySeparatorChar;
        if (!path.Equals(rootPath, comparison) && !path.StartsWith(prefix, comparison))
            throw new UnauthorizedAccessException("不能访问共享文件夹之外的路径。");
        // Reject symlinks and junctions, including the root and its ancestors.
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("不支持通过符号链接或目录联接访问文件。");
        }
        return path;
    }

    public static List<SharedEntry> Browse(SharedFolder root, string relativePath)
    {
        var directory = Resolve(root, relativePath);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("共享目录不存在。");
        var result = new List<SharedEntry>();
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            var isDirectory = entry is DirectoryInfo;
            result.Add(new SharedEntry(root.Id, Path.GetRelativePath(root.LocalPath, entry.FullName), entry.Name, isDirectory, entry is FileInfo file ? file.Length : 0));
            if (result.Count >= 2000) break;
        }
        return result.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
