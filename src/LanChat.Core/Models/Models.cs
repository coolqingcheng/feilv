namespace LanChat.Core.Models;

public enum MessageKind { Text, Image, File }
public enum DeliveryState { Sending, Sent, Failed, Received, Rejected }

public sealed record UserProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Nickname { get; set; } = Environment.UserName;
    public string? AvatarPath { get; set; }
    public string Theme { get; set; } = "System";
    public bool ShowNotifications { get; set; } = true;
    public bool AutoReceive { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool AutoDownloadUpdates { get; set; } = true;
    public string QrUploadDirectory { get; set; } = "";
    public string QrDownloadDirectory { get; set; } = "";
}

public sealed record Peer
{
    public required string Id { get; init; }
    public required string Nickname { get; init; }
    public required string Address { get; init; }
    public required int Port { get; init; }
    public required string Fingerprint { get; init; }
    public string AppVersion { get; init; } = "";
    public string? AvatarPath { get; init; }
    public bool IsOnline { get; init; } = true;
    public DateTimeOffset LastSeen { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ChatMessage
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string PeerId { get; init; }
    public required string SenderId { get; init; }
    public MessageKind Kind { get; init; }
    public string Text { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? AttachmentId { get; init; }
    public string? FileName { get; init; }
    public long FileSize { get; init; }
    public bool IsFolder { get; init; }
    public string? Sha256 { get; init; }
    public string? LocalPath { get; set; }
    public DeliveryState Delivery { get; set; }
    // Local snapshots are supplied by the authenticated connection, never trusted from a remote packet.
    public string PeerNickname { get; set; } = "";
    public string PeerAddress { get; set; } = "";
    public DateTimeOffset? ReceivedAt { get; init; }
}

public sealed record FileHistoryCursor(long Timestamp, string Id, string PeerId);
public sealed record FileHistoryPage(IReadOnlyList<ChatMessage> Messages, FileHistoryCursor? OlderCursor, bool HasMore);

public sealed record SharedFolder(string Id, string Name, string LocalPath);
public sealed record SharedEntry(string RootId, string RelativePath, string Name, bool IsDirectory, long Size);
public sealed record EmojiItem(string Id, string Path, string Name);
public sealed record Attachment(string Id, string PeerId, string Path, string FileName, long Size, string Sha256, bool IsFolder = false)
{
    public string MessageId { get; init; } = "";
    public bool IsRejected { get; init; }
}
public sealed record TransferUpdate(string Id, string Name, long CompletedBytes, long TotalBytes, string Status)
{
    public double BytesPerSecond { get; init; }
    public bool IsUpload { get; init; }
    public string? AttachmentId { get; init; }
    public string? PeerId { get; init; }
}
public sealed record PeerIdentity(string Id, string Fingerprint);

public sealed record NodeOptions
{
    public string DataDirectory { get; init; } = AppPaths.DefaultBaseDirectory;
    public int DiscoveryPort { get; init; } = 45831;
    public int ListenPort { get; init; }
    public bool EnableDiscovery { get; init; } = true;
}
