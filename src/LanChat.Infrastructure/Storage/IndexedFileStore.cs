using System.Collections.Concurrent;
using LanChat.Core.Models;
using LanChat.Core.Services;

namespace LanChat.Infrastructure.Storage;

/// <summary>File metadata repository. The rebuildable index is scanned in bounded blocks; payloads stay in dated media folders.</summary>
public sealed class IndexedFileStore : ILocalStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate;
    public string DataDirectory { get; }
    public string StoragePath => Path.Combine(DataDirectory, "Data", "records.dat");
    public string IndexPath => Path.Combine(DataDirectory, "Data", "records.idx");
    public UserProfile InitialProfile { get; }

    public IndexedFileStore(string dataDirectory)
    {
        DataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        _gate = Gates.GetOrAdd(DataDirectory, static _ => new SemaphoreSlim(1, 1));
        _gate.Wait();
        try
        {
            Directory.CreateDirectory(Path.Combine(DataDirectory, "Data"));
            var config = XmlConfiguration.Load(DataDirectory);
            if (!File.Exists(AppPaths.Configuration(DataDirectory))) config.Save(DataDirectory);
            InitialProfile = config.Profile;
        }
        finally { _gate.Release(); }
    }

    private async Task<T> RunAsync<T>(Func<RecordFileIndex, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Scanning and JSON/CRC work must not run on the Avalonia dispatcher.
            return await Task.Run(async () =>
            {
                await using var files = await RecordFileIndex.OpenAsync(StoragePath, IndexPath, ct).ConfigureAwait(false);
                T result;
                try { result = await action(files).ConfigureAwait(false); }
                catch (RecordFileIndex.CorruptIndexException)
                {
                    await files.RebuildAsync().ConfigureAwait(false);
                    result = await action(files).ConfigureAwait(false);
                }
                await files.CommitAsync().ConfigureAwait(false);
                return result;
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<T> ConfigAsync<T>(Func<XmlConfiguration, T> action, bool save, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                var config = XmlConfiguration.Load(DataDirectory);
                var result = action(config);
                if (save) await config.SaveAsync(DataDirectory, ct).ConfigureAwait(false);
                return result;
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task<UserProfile> LoadProfileAsync(CancellationToken cancellationToken = default) => ConfigAsync(c => c.Profile, false, cancellationToken);
    public Task SaveProfileAsync(UserProfile profile, CancellationToken cancellationToken = default) => ConfigAsync(c => c.Profile = profile, true, cancellationToken);
    public Task<List<SharedFolder>> LoadSharesAsync(CancellationToken cancellationToken = default) => ConfigAsync(c => c.Shares, false, cancellationToken);
    public Task SaveSharesAsync(IReadOnlyList<SharedFolder> shares, CancellationToken cancellationToken = default) => ConfigAsync(c => c.Shares = shares.ToList(), true, cancellationToken);

    private string? StorePath(string? path) => path is null ? null : Path.GetRelativePath(DataDirectory, path);
    private string? LoadPath(string? path) => path is null ? null : Path.GetFullPath(Path.Combine(DataDirectory, path));
    private static string MessageKey(ChatMessage message) => MessageKey(message.PeerId, message.Id);
    private static string MessageKey(string peerId, string id) => peerId.Length + ":" + peerId + id;
    private ChatMessage StoreMessage(ChatMessage message) => message with
    {
        LocalPath = StorePath(message.LocalPath),
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(message.Timestamp.ToUnixTimeMilliseconds()),
        ReceivedAt = DateTimeOffset.FromUnixTimeMilliseconds((message.ReceivedAt ?? message.Timestamp).ToUnixTimeMilliseconds())
    };
    private ChatMessage LoadMessage(ChatMessage message) => message with { LocalPath = LoadPath(message.LocalPath) };

    public Task<ChatMessage?> FindMessageAsync(string peerId, string id, CancellationToken cancellationToken = default) => RunAsync(async files =>
    {
        var record = await files.GetAsync(StoredKind.Message, MessageKey(peerId, id)).ConfigureAwait(false);
        return record is { IsDeleted: false, Message: { } message } ? LoadMessage(message) : null;
    }, cancellationToken);

    public Task SaveMessageAsync(ChatMessage message, CancellationToken cancellationToken = default) => RunAsync(async files =>
    {
        var key = MessageKey(message);
        var record = await files.GetAsync(StoredKind.Message, key).ConfigureAwait(false);
        if (record?.IsDeleted == true) return false;
        var existing = record?.Message;
        var value = existing is null ? StoreMessage(message) : existing with { Text = message.Text, LocalPath = StorePath(message.LocalPath), Delivery = message.Delivery };
        await files.PutAsync(new StoredRecord { Kind = StoredKind.Message, Key = key, Message = value }).ConfigureAwait(false);
        return true;
    }, cancellationToken);

    public Task DeleteMessageAsync(string peerId, string id, CancellationToken cancellationToken = default) => RunAsync(async files =>
    {
        // Keep only a key marker so retries and delayed writes cannot restore a removed record.
        var key = MessageKey(peerId, id);
        if ((await files.GetAsync(StoredKind.Message, key).ConfigureAwait(false))?.IsDeleted != true)
            await files.PutAsync(new StoredRecord { Kind = StoredKind.Message, Key = key, IsDeleted = true }).ConfigureAwait(false);
        return true;
    }, cancellationToken);

    public Task<bool> TrySaveReceivedMessageAsync(ChatMessage message, CancellationToken cancellationToken = default) => RunAsync(async files =>
    {
        var key = MessageKey(message);
        if (await files.GetAsync(StoredKind.Message, key).ConfigureAwait(false) is not null) return false;
        await files.PutAsync(new StoredRecord { Kind = StoredKind.Message, Key = key, Message = StoreMessage(message) }).ConfigureAwait(false);
        return true;
    }, cancellationToken);

    public Task<List<ChatMessage>> LoadMessagesAsync(CancellationToken cancellationToken = default) => RunAsync(async files =>
    {
        var result = new List<ChatMessage>();
        await foreach (var entry in files.EntriesAsync())
            if (entry.Record.Kind == StoredKind.Message && entry.Record.MessageKind != byte.MaxValue)
            {
                var record = await files.ReadAsync(entry.Record).ConfigureAwait(false);
                if (!record.IsDeleted && record.Message is { } message) result.Add(LoadMessage(message));
            }
        result.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return result;
    }, cancellationToken);

    private static int Compare(ChatMessage a, ChatMessage b) => Compare(a, new FileHistoryCursor((b.ReceivedAt ?? b.Timestamp).ToUnixTimeMilliseconds(), b.Id, b.PeerId));
    private static int Compare(ChatMessage a, FileHistoryCursor b)
    {
        var time = (a.ReceivedAt ?? a.Timestamp).ToUnixTimeMilliseconds().CompareTo(b.Timestamp);
        if (time != 0) return time;
        var id = string.CompareOrdinal(a.Id, b.Id);
        return id != 0 ? id : string.CompareOrdinal(a.PeerId, b.PeerId);
    }

    public Task<FileHistoryPage> LoadFileHistoryAsync(string search = "", FileHistoryCursor? before = null, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(pageSize));
        search = search.Trim();
        var bloom = RecordFileIndex.Bloom(search);
        return RunAsync(async files =>
        {
            // Keep only one page and one lookahead, regardless of history size or physical record order.
            var newest = new List<ChatMessage>(pageSize + 2);
            await foreach (var entry in files.EntriesAsync())
            {
                var e = entry.Record;
                if (e.Kind != StoredKind.Message || e.MessageKind is not ((byte)MessageKind.File) and not ((byte)MessageKind.Image)
                    || (before is not null && e.Time > before.Timestamp) || (e.NameBloom & bloom) != bloom
                    || (newest.Count > pageSize && e.Time < (newest[0].ReceivedAt ?? newest[0].Timestamp).ToUnixTimeMilliseconds())) continue;
                var message = (await files.ReadAsync(e).ConfigureAwait(false)).Message!;
                if ((before is not null && Compare(message, before) >= 0) || (search.Length > 0 && !(message.FileName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))) continue;
                var at = newest.BinarySearch(message, Comparer<ChatMessage>.Create(Compare));
                newest.Insert(at < 0 ? ~at : at, message);
                if (newest.Count > pageSize + 1) newest.RemoveAt(0);
            }
            var more = newest.Count > pageSize;
            if (more) newest.RemoveAt(0);
            var first = newest.FirstOrDefault();
            var cursor = first is null ? null : new FileHistoryCursor((first.ReceivedAt ?? first.Timestamp).ToUnixTimeMilliseconds(), first.Id, first.PeerId);
            return new FileHistoryPage(newest.Select(LoadMessage).ToList(), cursor, more);
        }, cancellationToken);
    }

    public Task<List<Peer>> LoadPeersAsync(CancellationToken cancellationToken = default) => RunAsync(async files =>
        ((await files.GetAsync(StoredKind.Peers, "all").ConfigureAwait(false))?.Peers ?? []).Select(p => p with { AvatarPath = LoadPath(p.AvatarPath), IsOnline = false }).ToList(), cancellationToken);
    public Task SavePeersAsync(IReadOnlyList<Peer> peers, CancellationToken cancellationToken = default) => RunAsync(async files =>
    {
        await files.PutAsync(new StoredRecord { Kind = StoredKind.Peers, Key = "all", Peers = peers.Select(p => p with { AvatarPath = StorePath(p.AvatarPath), IsOnline = false }).ToList() }).ConfigureAwait(false);
        return true;
    }, cancellationToken);

    public Task<List<EmojiItem>> LoadEmojisAsync(CancellationToken cancellationToken = default) => RunAsync(async files =>
        ((await files.GetAsync(StoredKind.Emojis, "all").ConfigureAwait(false))?.Emojis ?? []).Select(e => e with { Path = LoadPath(e.Path)! }).ToList(), cancellationToken);
    public Task SaveEmojisAsync(IReadOnlyList<EmojiItem> emojis, CancellationToken cancellationToken = default) => RunAsync(async files =>
    {
        await files.PutAsync(new StoredRecord { Kind = StoredKind.Emojis, Key = "all", Emojis = emojis.Select(e => e with { Path = StorePath(e.Path)! }).ToList() }).ConfigureAwait(false);
        return true;
    }, cancellationToken);

    public Task<List<Attachment>> LoadAttachmentsAsync(CancellationToken cancellationToken = default) => RunAsync(async files =>
    {
        var result = new List<Attachment>();
        await foreach (var entry in files.EntriesAsync())
            if (entry.Record.Kind == StoredKind.Attachment)
            {
                var attachment = (await files.ReadAsync(entry.Record).ConfigureAwait(false)).Attachment!;
                result.Add(attachment with { Path = LoadPath(attachment.Path)! });
            }
        return result;
    }, cancellationToken);
    private Task PutAttachmentAsync(RecordFileIndex files, Attachment attachment) => files.PutAsync(new StoredRecord { Kind = StoredKind.Attachment, Key = attachment.Id, Attachment = attachment with { Path = StorePath(attachment.Path)! } });
    public Task SaveAttachmentsAsync(IReadOnlyList<Attachment> attachments, CancellationToken cancellationToken = default) => RunAsync(async files =>
    {
        var keep = attachments.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var remove = new List<string>();
        await foreach (var entry in files.EntriesAsync())
            if (entry.Record.Kind == StoredKind.Attachment)
            {
                var key = (await files.ReadAsync(entry.Record).ConfigureAwait(false)).Key;
                if (!keep.Contains(key)) remove.Add(key);
            }
        foreach (var key in remove) await files.DeleteAsync(StoredKind.Attachment, key).ConfigureAwait(false);
        foreach (var attachment in attachments) await PutAttachmentAsync(files, attachment).ConfigureAwait(false);
        return true;
    }, cancellationToken);
    public Task SaveAttachmentAsync(Attachment attachment, CancellationToken cancellationToken = default) => RunAsync(async files => { await PutAttachmentAsync(files, attachment).ConfigureAwait(false); return true; }, cancellationToken);
    public Task<Attachment?> FindAttachmentAsync(string id, CancellationToken cancellationToken = default) => RunAsync(async files =>
    {
        var attachment = (await files.GetAsync(StoredKind.Attachment, id).ConfigureAwait(false))?.Attachment;
        return attachment is null ? null : attachment with { Path = LoadPath(attachment.Path)! };
    }, cancellationToken);
    public Task<bool> TrustIdentityAsync(string id, string fingerprint, CancellationToken cancellationToken = default) => RunAsync(async files =>
    {
        var existing = await files.GetAsync(StoredKind.Identity, id).ConfigureAwait(false);
        if (existing is not null) return string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal);
        await files.PutAsync(new StoredRecord { Kind = StoredKind.Identity, Key = id, Fingerprint = fingerprint }).ConfigureAwait(false);
        return true;
    }, cancellationToken);
}
