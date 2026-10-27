namespace LanChat.Core.Models;

/// <summary>A browser upload whose stream is closed and whose final filename has been committed.</summary>
public sealed record QrUpload(string Id, string FilePath, string FileName, long Size, string SenderAddress,
    DateTimeOffset ReceivedAt, double BytesPerSecond)
{
    // LAN identities must be GUIDs; this local history source cannot collide with a discovered device.
    public const string SourceId = "qr-http";
}
