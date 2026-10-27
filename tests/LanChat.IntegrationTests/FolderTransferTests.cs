using System.Security.Cryptography;
using LanChat.Core.Models;
using LanChat.Infrastructure.Networking;
using LanChat.Infrastructure.Protocol;
using LanChat.Infrastructure.Storage;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static async Task RunFolderTransferTestsAsync(LanService sender, LanService receiver, Peer receivingPeer, Peer sendingPeer)
    {
        var root = Path.Combine(TestRoot, "项目目录");
        Directory.CreateDirectory(Path.Combine(root, "子目录", "空目录"));
        await File.WriteAllTextAsync(Path.Combine(root, "说明.txt"), "文件夹递归传输，不打包 ZIP");
        await File.WriteAllBytesAsync(Path.Combine(root, "子目录", "empty.txt"), []);
        var bytes = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 37);
        await File.WriteAllBytesAsync(Path.Combine(root, "子目录", "内容.bin"), bytes);
        var offered = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Received(ChatMessage message) { if (message.IsFolder && message.FileName == "项目目录" && message.LocalPath is null) offered.TrySetResult(message); }
        receiver.MessageReceived += Received;
        try
        {
            var sent = await sender.SendFolderAsync(receivingPeer, root);
            var offer = await offered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(offer.IsFolder && offer.LocalPath is null && sent.FileSize == bytes.Length + new FileInfo(Path.Combine(root, "说明.txt")).Length,
                "folder offer records total bytes and waits for explicit acceptance");
            await File.WriteAllTextAsync(Path.Combine(root, "说明.txt"), "原文件已修改");
            var saved = await receiver.DownloadAttachmentAsync(sendingPeer, offer);
            Check(Directory.Exists(Path.Combine(saved, "子目录", "空目录")) && new FileInfo(Path.Combine(saved, "子目录", "empty.txt")).Length == 0
                && (await File.ReadAllBytesAsync(Path.Combine(saved, "子目录", "内容.bin"))).SequenceEqual(bytes)
                && await File.ReadAllTextAsync(Path.Combine(saved, "说明.txt")) == "文件夹递归传输，不打包 ZIP",
                "recursive folder transfer preserves nested names, empty entries and the send-time snapshot without an archive");
            Check((await new IndexedFileStore(Path.Combine(TestRoot, "bob")).LoadFileHistoryAsync("项目目录")).Messages.Single().IsFolder,
                "folder history survives repository reopen");

            var thirdStore = new IndexedFileStore(Path.Combine(TestRoot, "serial-receiver"));
            await using var third = new LanService(thirdStore, thirdStore.InitialProfile, new NodeOptions { EnableDiscovery = false });
            await third.StartAsync();
            var thirdPeer = await sender.ConnectAsync("127.0.0.1", third.Port);
            var thirdSender = await third.ConnectAsync("127.0.0.1", sender.Port);
            var firstFile = await sender.SendFileAsync(receivingPeer, Path.Combine(TestRoot, "流式传输_64MiB.bin"), false);
            var secondFile = await sender.SendFolderAsync(thirdPeer, root);
            var firstIncoming = firstFile with { PeerId = sender.Profile.Id, LocalPath = null };
            var secondIncoming = secondFile with { PeerId = sender.Profile.Id, LocalPath = null };
            var active = new HashSet<string>(); var maximum = 0; var completed = 0;
            var sync = new object();
            void Transfer(TransferUpdate update)
            {
                if (!update.IsUpload || update.AttachmentId is null) return;
                lock (sync)
                {
                    if (update.Status is "发送中" or "准备发送") { active.Add(update.AttachmentId); maximum = Math.Max(maximum, active.Count); }
                    if (update.Status is "已完成" or "失败" or "已取消") { active.Remove(update.AttachmentId); if (update.Status == "已完成") completed++; }
                }
            }
            sender.TransferChanged += Transfer;
            try
            {
                await Task.WhenAll(receiver.DownloadAttachmentAsync(sendingPeer, firstIncoming), third.DownloadAttachmentAsync(thirdSender, secondIncoming));
                Check(maximum == 1 && completed == 2 && active.Count == 0, "simultaneous recipients transfer file bodies strictly one at a time, including recursive folders");
            }
            finally { sender.TransferChanged -= Transfer; }

            var empty = Path.Combine(TestRoot, "空文件夹"); Directory.CreateDirectory(empty);
            var emptySent = await sender.SendFolderAsync(receivingPeer, empty);
            var emptyPath = await receiver.DownloadAttachmentAsync(sendingPeer, emptySent with { PeerId = sender.Profile.Id, LocalPath = null });
            Check(Directory.Exists(emptyPath) && !Directory.EnumerateFileSystemEntries(emptyPath).Any(), "an entirely empty folder transfers successfully");
        }
        finally { receiver.MessageReceived -= Received; }

        foreach (var relative in new[] { "../escape", "C:/Windows", "/absolute", "nested/../../outside", "file:stream", "nested//file", ".. /escape", "nested./file" })
        {
            var rejected = false;
            try { FolderTransferProtocol.ResolveChild(root, relative); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "folder entry rejects unsafe path " + relative);
        }
        using var malformed = new MemoryStream();
        await PacketIO.WriteAsync(malformed, new WirePacket { Type = "folder-file", RelativePath = "bad.bin", FileSize = 1, Sha256 = new string('0', 64) }, default);
        malformed.WriteByte(42); malformed.Position = 0;
        await MustFailAsync(() => FolderTransferProtocol.ReceiveAsync(malformed, Path.Combine(TestRoot, "bad-folder"), 1, _ => { }, default), "folder receiver verifies every individual file checksum");
    }
}
