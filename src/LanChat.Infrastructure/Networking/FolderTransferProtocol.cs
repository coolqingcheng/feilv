using System.Buffers;
using System.IO.Enumeration;
using System.Security.Cryptography;
using LanChat.Core.Models;
using LanChat.Infrastructure.Protocol;

namespace LanChat.Infrastructure.Networking;

/// <summary>Streams folder entries and file bodies on one connection; no archive or in-memory tree.</summary>
internal static class FolderTransferProtocol
{
    private static EnumerationOptions Entries => new()
    {
        RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false
    };

    public static Task<long> SnapshotAsync(string source, string target, CancellationToken ct) => Task.Run(async () =>
    {
        source = Path.GetFullPath(source);
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("请选择实际文件夹，不能发送目录链接。");
        Directory.CreateDirectory(target);
        long total = 0;
        foreach (var entry in Enumerate(source, Path.GetDirectoryName(target)))
        {
            ct.ThrowIfCancellationRequested();
            var destination = Path.Combine(target, Path.GetRelativePath(source, entry));
            if (Directory.Exists(entry)) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await input.CopyToAsync(output, 65536, ct).ConfigureAwait(false);
            total = checked(total + output.Length);
        }
        return total;
    }, ct);

    public static async Task SendAsync(Stream stream, Attachment attachment, Action<long> report, CancellationToken ct)
    {
        await PacketIO.WriteAsync(stream, new WirePacket { Type = "folder", FileName = attachment.FileName, FileSize = attachment.Size }, ct);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        long total = 0;
        try
        {
            foreach (var path in Enumerate(attachment.Path))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(attachment.Path, path).Replace('\\', '/');
                if (Directory.Exists(path))
                {
                    await PacketIO.WriteAsync(stream, new WirePacket { Type = "folder-directory", RelativePath = relative }, ct);
                    continue;
                }
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (file.Length > attachment.Size - total) throw new IOException("文件夹内容已变化，请重新发送。");
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
                file.Position = 0;
                await PacketIO.WriteAsync(stream, new WirePacket { Type = "folder-file", RelativePath = relative, FileSize = file.Length, Sha256 = hash }, ct);
                int read;
                while ((read = await file.ReadAsync(buffer.AsMemory(0, 65536), ct).ConfigureAwait(false)) > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    total += read; report(total);
                }
            }
            if (total != attachment.Size) throw new IOException("文件夹内容已变化，请重新发送。");
            await PacketIO.WriteAsync(stream, new WirePacket { Type = "folder-end" }, ct);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static async Task ReceiveAsync(Stream stream, string target, long expectedSize, Action<long> report, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        long total = 0;
        try
        {
            while (true)
            {
                var entry = await PacketIO.ReadAsync(stream, ct);
                if (entry.Type == "folder-end")
                {
                    if (total != expectedSize) throw new InvalidDataException("文件夹总大小校验失败。");
                    return;
                }
                if (entry.Type is not ("folder-directory" or "folder-file")) throw new InvalidDataException("文件夹传输中断或格式无效。");
                var path = ResolveChild(target, entry.RelativePath);
                if (entry.Type == "folder-directory") { Directory.CreateDirectory(path); continue; }
                if (entry.FileSize < 0 || entry.FileSize > expectedSize - total || entry.Sha256?.Length != 64)
                    throw new InvalidDataException("文件夹中的文件元数据无效。");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long copied = 0;
                while (copied < entry.FileSize)
                {
                    var count = (int)Math.Min(65536, entry.FileSize - copied);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException("文件夹传输中断。");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read); copied += read; total += read; report(total);
                }
                if (Convert.ToHexString(hash.GetHashAndReset()) != entry.Sha256) throw new InvalidDataException("文件夹中的文件 SHA-256 校验失败。");
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    internal static string ResolveChild(string root, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length == 0 || parts.Any(x => x.Length == 0 || x is "." or ".." || x.Contains(':') || x.EndsWith('.') || x.EndsWith(' ') || x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("文件夹路径无效。");
        var path = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("文件夹路径越界。");
        return path;
    }

    private static IEnumerable<string> Enumerate(string root, string? excluded = null)
    {
        var entries = new FileSystemEnumerable<string>(root, (ref FileSystemEntry entry) => entry.ToFullPath(), Entries);
        if (excluded is not null)
        {
            var blocked = Path.GetFullPath(excluded);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            bool Include(ref FileSystemEntry entry) => !entry.ToFullPath().Equals(blocked, comparison);
            entries.ShouldIncludePredicate = Include;
            entries.ShouldRecursePredicate = Include;
        }
        return entries;
    }
}
