using System.Collections.Concurrent;
using System.Security.Cryptography;
using LanChat.Core.Models;
using LanChat.Infrastructure.Networking;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static async Task RunStreamingTransferTestsAsync(LanService sender, LanService receiver, Peer receivingPeer, Peer sendingPeer)
    {
        const int size = 64 * 1024 * 1024;
        var sourcePath = Path.Combine(TestRoot, "流式传输_64MiB.bin");
        var block = RandomNumberGenerator.GetBytes(65536);
        await using (var file = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            for (var offset = 0; offset < size; offset += block.Length) await file.WriteAsync(block);
        var incoming = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var upload = new ConcurrentQueue<TransferUpdate>();
        var download = new ConcurrentQueue<TransferUpdate>();
        void Received(ChatMessage message) { if (message.FileName == Path.GetFileName(sourcePath)) incoming.TrySetResult(message); }
        void Upload(TransferUpdate update) => upload.Enqueue(update);
        void Download(TransferUpdate update) => download.Enqueue(update);
        receiver.MessageReceived += Received; sender.TransferChanged += Upload; receiver.TransferChanged += Download;
        try
        {
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var sent = await sender.SendFileAsync(receivingPeer, sourcePath, false);
            var offer = await incoming.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var path = await receiver.DownloadAttachmentAsync(sendingPeer, offer);
            var allocation = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            Check(allocation < size / 2, $"64 MiB staging + upload + download uses bounded buffers (total managed allocations {allocation:N0} bytes)");
            Check(upload.Any(x => x.IsUpload && x.AttachmentId == sent.AttachmentId && x.CompletedBytes == size && x.Status == "已完成" && x.BytesPerSecond > 0)
                && download.Any(x => !x.IsUpload && x.CompletedBytes == size && x.Status == "已完成" && x.BytesPerSecond > 0),
                "real file body transfer reports byte counts and positive send/receive speeds at both endpoints");
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            await using var saved = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var sourceHash = await SHA256.HashDataAsync(source);
            var savedHash = await SHA256.HashDataAsync(saved);
            Check(saved.Length == size && sourceHash.SequenceEqual(savedHash),
                "streaming transfer preserves the complete 64 MiB payload and checksum");
        }
        finally { receiver.MessageReceived -= Received; sender.TransferChanged -= Upload; receiver.TransferChanged -= Download; }
    }
}
