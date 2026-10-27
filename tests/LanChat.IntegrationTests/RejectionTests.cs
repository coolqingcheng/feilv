using LanChat.Core.Models;
using LanChat.Infrastructure.Networking;
using LanChat.Infrastructure.Storage;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static async Task RunRejectionTestsAsync()
    {
        var aliceStore = new IndexedFileStore(Path.Combine(TestRoot, "reject-sender"));
        var bobStore = new IndexedFileStore(Path.Combine(TestRoot, "reject-receiver"));
        var otherStore = new IndexedFileStore(Path.Combine(TestRoot, "reject-other"));
        await using var alice = new LanService(aliceStore, new UserProfile { Nickname = "发送方" }, new NodeOptions { EnableDiscovery = false });
        await using var bob = new LanService(bobStore, new UserProfile { Nickname = "接收方" }, new NodeOptions { EnableDiscovery = false });
        await using var other = new LanService(otherStore, new UserProfile { Nickname = "其他电脑" }, new NodeOptions { EnableDiscovery = false });
        await alice.StartAsync(); await bob.StartAsync(); await other.StartAsync();
        var receiver = await alice.ConnectAsync("127.0.0.1", bob.Port);
        var sender = await bob.ConnectAsync("127.0.0.1", alice.Port);
        var foreignSender = await other.ConnectAsync("127.0.0.1", alice.Port);
        var source = Path.Combine(TestRoot, "拒绝接收资料.txt"); await File.WriteAllTextAsync(source, "未确认接收的文件");
        var offer = await alice.SendFileAsync(receiver, source, false);
        var incoming = (await bobStore.FindMessageAsync(alice.Profile.Id, offer.Id))!;
        var rejectedUpdates = 0;
        alice.TransferChanged += update => { if (update.Status == "对方已拒绝") Interlocked.Increment(ref rejectedUpdates); };
        await MustFailAsync(() => other.RejectAttachmentAsync(foreignSender, incoming), "a different authenticated client cannot reject another device's attachment");
        await MustFailAsync(() => bob.RejectAttachmentAsync(sender, incoming with { Id = Guid.NewGuid().ToString("N") }), "rejection receipts must match the offered message and attachment");
        Check((await aliceStore.FindMessageAsync(bob.Profile.Id, offer.Id))!.Delivery == DeliveryState.Sent, "forged rejection attempts do not alter the sender's state");
        await bob.RejectAttachmentAsync(sender, incoming);
        Check((await aliceStore.FindMessageAsync(bob.Profile.Id, offer.Id))!.Delivery == DeliveryState.Rejected
            && (await bobStore.FindMessageAsync(alice.Profile.Id, offer.Id))!.Delivery == DeliveryState.Rejected && rejectedUpdates == 1,
            "a real TLS rejection persists both histories and immediately notifies the sender");
        await bob.RejectAttachmentAsync(sender, incoming);
        Check(rejectedUpdates == 1, "replayed rejection receipts are idempotent");
        var blocked = false;
        try { await bob.DownloadAttachmentAsync(sender, incoming); }
        catch (InvalidOperationException) { blocked = true; }
        Check(blocked, "a locally rejected offer cannot be accepted again");
        await MustFailAsync(() => bob.DownloadAttachmentAsync(sender, incoming with { Delivery = DeliveryState.Received }), "the sender revokes rejected attachment downloads even if the receiver resets its local status");
        Check(File.Exists(offer.LocalPath), "rejecting an offer preserves the sender's source snapshot");

        var deleted = await alice.SendFileAsync(receiver, source, false);
        await aliceStore.DeleteMessageAsync(bob.Profile.Id, deleted.Id);
        await bob.RejectAttachmentAsync(sender, (await bobStore.FindMessageAsync(alice.Profile.Id, deleted.Id))!);
        Check(await aliceStore.FindMessageAsync(bob.Profile.Id, deleted.Id) is null
            && (await aliceStore.FindAttachmentAsync(deleted.AttachmentId!))!.IsRejected, "a rejection does not resurrect a deleted sender history row and still revokes its attachment");

        var folder = Path.Combine(TestRoot, "拒绝文件夹"); Directory.CreateDirectory(folder); await File.WriteAllTextAsync(Path.Combine(folder, "文件.txt"), "folder");
        var folderOffer = await alice.SendFolderAsync(receiver, folder);
        await bob.RejectAttachmentAsync(sender, (await bobStore.FindMessageAsync(alice.Profile.Id, folderOffer.Id))!);
        Check((await aliceStore.FindMessageAsync(bob.Profile.Id, folderOffer.Id))!.Delivery == DeliveryState.Rejected, "folder rejection uses the same sender receipt and persistent state");

        // Hold the original offer acknowledgement until its rejection is processed on another TLS connection.
        void RejectBeforeAcknowledgement(ChatMessage message)
        {
            if (message.FileName == "立即拒绝.txt" && message.Delivery == DeliveryState.Received)
                bob.RejectAttachmentAsync(sender, message).GetAwaiter().GetResult();
        }
        var immediateSource = Path.Combine(TestRoot, "立即拒绝.txt"); await File.WriteAllTextAsync(immediateSource, "race");
        bob.MessageReceived += RejectBeforeAcknowledgement;
        try
        {
            var immediate = await alice.SendFileAsync(receiver, immediateSource, false).WaitAsync(TimeSpan.FromSeconds(10));
            Check(immediate.Delivery == DeliveryState.Rejected && (await aliceStore.FindMessageAsync(bob.Profile.Id, immediate.Id))!.Delivery == DeliveryState.Rejected,
                "a rejection arriving before the original offer acknowledgement cannot be overwritten by the later sent status");
        }
        finally { bob.MessageReceived -= RejectBeforeAcknowledgement; }
        var resent = await alice.SendFileAsync(receiver, source, false);
        Check(resent.AttachmentId != offer.AttachmentId && !(await aliceStore.FindAttachmentAsync(resent.AttachmentId!))!.IsRejected,
            "explicitly sending the file again creates an independent offer that can be accepted");
    }
}
