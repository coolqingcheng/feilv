using LanChat.Core.Models;
using LanChat.Infrastructure.Storage;


namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static async Task RunFileHistoryStorageTestsAsync()
    {
        var root = Path.Combine(TestRoot, "file-history");
        var store = new IndexedFileStore(root);
        var cached = new Attachment("old-attachment", "peer", Path.Combine(root, "Files", "old.bin"), "old.bin", 128, new string('A', 64));
        await store.SaveAttachmentAsync(cached);
        await store.SaveAttachmentAsync(cached with { Id = "new-attachment", FileName = "new.bin" });
        await store.SaveAttachmentAsync(cached with { FileName = "updated.bin" });
        var lazy = new IndexedFileStore(root);
        Check((await lazy.FindAttachmentAsync(cached.Id))?.FileName == "updated.bin" && (await lazy.LoadAttachmentsAsync()).Count == 2
            && (await lazy.FindAttachmentAsync("new-attachment"))?.Path == cached.Path,
            "incremental attachment upserts preserve other entries and resolve stored relative paths after restart");
        Check(await lazy.FindAttachmentAsync("' OR 1=1 --") is null, "attachment lookup is parameterized and returns only the requested ID");
        var time = DateTimeOffset.UtcNow.AddDays(-1);
        var records = new List<ChatMessage>();
        for (var index = 0; index < 157; index++)
        {
            var message = new ChatMessage { Id = index.ToString("x32"), PeerId = "peer", SenderId = "peer", Kind = index % 3 == 0 ? MessageKind.Image : MessageKind.File,
                FileName = $"历史文件_{index:D3}.dat", FileSize = index == 0 ? 0 : 1024L * index, Timestamp = time.AddDays(10), ReceivedAt = time,
                PeerNickname = "接收时的电脑", PeerAddress = "192.168.1.21", Delivery = DeliveryState.Received };
            records.Add(message); await store.SaveMessageAsync(message);
        }
        await store.SaveMessageAsync(new ChatMessage { PeerId = "peer", SenderId = "peer", Text = "不属于文件列表", Timestamp = time.AddDays(20) });
        var first = await store.LoadFileHistoryAsync();
        Check(first.Messages.Count == 50 && first.HasMore && first.Messages[^1].Id == records[^1].Id, "file history reads only the newest page and orders it oldest to newest");
        Check(first.Messages.All(x => x.Kind != MessageKind.Text && x.ReceivedAt == time.AddTicks(-(time.Ticks % TimeSpan.TicksPerMillisecond))), "file history uses local receipt time and includes files and images only");
        var page = first; var all = first.Messages.ToList();
        // A new arrival after the first query must not shift the older-page boundary.
        await store.SaveMessageAsync(records[0] with { Id = Guid.NewGuid().ToString("N"), ReceivedAt = time.AddHours(1), FileName = "新到达的文件.zip" });
        while (page.HasMore)
        {
            page = await store.LoadFileHistoryAsync(before: page.OlderCursor);
            all.InsertRange(0, page.Messages);
        }
        Check(all.Count == 157 && all.Select(x => (x.PeerId, x.Id)).Distinct().Count() == 157 && all.Select(x => x.Id).SequenceEqual(records.Select(x => x.Id)), "keyset paging handles equal timestamps and concurrent arrivals without gaps or duplicates");
        var search = await store.LoadFileHistoryAsync("历史文件_000");
        Check(search.Messages.Single().FileSize == 0, "filename search covers older, unloaded history and preserves zero-byte metadata");
        var special = records[0] with { Id = Guid.NewGuid().ToString("N"), FileName = "100%_备份.ZIP" };
        await store.SaveMessageAsync(special);
        Check((await store.LoadFileHistoryAsync("%_")).Messages.Single().Id == special.Id && (await store.LoadFileHistoryAsync("zip")).Messages.Count == 2, "filename search treats percent/underscore literally and ignores ASCII case");
        Check((await store.LoadFileHistoryAsync("'; DELETE FROM messages; --")).Messages.Count == 0 && (await store.LoadFileHistoryAsync()).Messages.Count == 50, "search is parameterized and cannot alter history");
        var duplicateId = special with { Id = Guid.NewGuid().ToString("N"), FileName = "去重验证.txt", LocalPath = Path.Combine(root, "file.txt") };
        var secondStore = new IndexedFileStore(root);
        var inserts = await Task.WhenAll(store.TrySaveReceivedMessageAsync(duplicateId), secondStore.TrySaveReceivedMessageAsync(duplicateId));
        Check(inserts.Count(x => x) == 1, "shared repository gate rejects concurrent duplicate receipts atomically");
        Check(!await new IndexedFileStore(root).TrySaveReceivedMessageAsync(duplicateId with { LocalPath = null, ReceivedAt = time.AddDays(5), PeerAddress = "10.0.0.1" })
            && (await store.LoadFileHistoryAsync("去重验证")).Messages.Single() is { LocalPath: not null, PeerAddress: "192.168.1.21" }, "receipt deduplication survives repository restart and preserves the downloaded path and IP snapshot");
        await store.SavePeersAsync([new Peer { Id = "peer", Nickname = "改过名的电脑", Address = "10.0.0.99", Port = 1, Fingerprint = "test" }]);
        var snapshot = (await store.LoadFileHistoryAsync("历史文件_000")).Messages.Single();
        Check(snapshot.PeerNickname == "接收时的电脑" && snapshot.PeerAddress == "192.168.1.21", "historical sender name and IP do not change when the device profile changes");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await store.LoadFileHistoryAsync(cancellationToken: cancellation.Token); throw new InvalidOperationException("Cancellation ignored"); }
        catch (OperationCanceledException) { Check(true, "history query supports cancellation"); }

        var retainedPath = Path.Combine(root, "保留的本地文件.txt");
        await File.WriteAllTextAsync(retainedPath, "删除历史不会删除正文");
        var removable = records[0] with { Id = "delete-history", FileName = "删除验证.txt", AttachmentId = cached.Id, LocalPath = retainedPath };
        await store.SaveMessageAsync(removable);
        await store.SaveMessageAsync(removable with { PeerId = "other-peer" });
        await store.DeleteMessageAsync(removable.PeerId, removable.Id);
        Check((await store.LoadFileHistoryAsync("删除验证")).Messages is [{ PeerId: "other-peer" }]
            && File.Exists(retainedPath) && await store.FindAttachmentAsync(cached.Id) is not null,
            "history deletion targets one peer/message and preserves file bodies and outgoing attachments");
        var deletedLength = new FileInfo(store.StoragePath).Length;
        await store.DeleteMessageAsync(removable.PeerId, removable.Id);
        Check(new FileInfo(store.StoragePath).Length == deletedLength, "deleting the same history entry twice does not append redundant records");
        await secondStore.SaveMessageAsync(removable with { Delivery = DeliveryState.Sent });
        Check(!await secondStore.TrySaveReceivedMessageAsync(removable)
            && (await secondStore.LoadFileHistoryAsync("删除验证")).Messages is [{ PeerId: "other-peer" }],
            "delayed updates and retried offers cannot restore deleted history");
        File.Delete(store.IndexPath);
        Check((await new IndexedFileStore(root).LoadFileHistoryAsync("删除验证")).Messages is [{ PeerId: "other-peer" }],
            "history deletion survives both repository restart and index reconstruction");

        var count = (await store.LoadMessagesAsync()).Count;
        File.Delete(store.IndexPath);
        Check((await new IndexedFileStore(root).LoadMessagesAsync()).Count == count && File.Exists(store.IndexPath), "missing index rebuilds from the storage log without losing messages");
        await using (var index = new FileStream(store.IndexPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            index.Position = 64 + 10;
            await index.WriteAsync(new byte[] { 0xFF, 0xEE, 0xDD });
        }
        Check((await new IndexedFileStore(root).LoadMessagesAsync()).Count == count, "corrupt index entries rebuild automatically from valid storage records");
        var intactLength = new FileInfo(store.StoragePath).Length;
        await using (var data = new FileStream(store.StoragePath, FileMode.Append, FileAccess.Write, FileShare.None))
            await data.WriteAsync(new byte[] { 1, 2, 3, 4, 5 });
        Check((await new IndexedFileStore(root).LoadMessagesAsync()).Count == count && new FileInfo(store.StoragePath).Length == intactLength,
            "an interrupted final record is discarded while complete records survive");
        await store.SaveAttachmentsAsync([cached with { Id = "replacement", IsFolder = true }]);
        File.Delete(store.IndexPath);
        Check((await new IndexedFileStore(root).LoadAttachmentsAsync()) is [{ Id: "replacement", IsFolder: true }], "index rebuild respects replacements, tombstones and folder metadata");
        var badRoot = Path.Combine(TestRoot, "corrupt-data");
        var badStore = new IndexedFileStore(badRoot);
        await badStore.SaveMessageAsync(records[0]);
        await using (var data = new FileStream(badStore.StoragePath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            data.Position = 40; await data.WriteAsync(new byte[] { 0xFF });
        }
        var badLength = new FileInfo(badStore.StoragePath).Length;
        await MustFailAsync(() => badStore.LoadMessagesAsync(), "damaged record payload reports a checksum error");
        Check(new FileInfo(badStore.StoragePath).Length == badLength, "payload corruption never silently deletes complete history");
    }
}
