using System.Text.Json.Serialization;
using LanChat.Core.Models;

namespace LanChat.Infrastructure.Protocol;

internal sealed record Announcement
{
    public int Version { get; init; } = 1;
    public string App { get; init; } = "LanChat";
    public string AppVersion { get; init; } = "";
    public required string Id { get; init; }
    public required string Nickname { get; init; }
    public required int Port { get; init; }
    public required string Fingerprint { get; init; }
    public bool IsOnline { get; init; } = true;
    public bool RequestReply { get; init; }
}

internal sealed record WirePacket
{
    public required string Type { get; init; }
    public Announcement? Sender { get; init; }
    public ChatMessage? Message { get; init; }
    public string? AttachmentId { get; init; }
    public string? MessageId { get; init; }
    public string? RootId { get; init; }
    public string RelativePath { get; init; } = "";
    public List<SharedEntry>? Entries { get; init; }
    public string? FileName { get; init; }
    public long FileSize { get; init; }
    public string? Sha256 { get; init; }
    public byte[]? Avatar { get; init; }
    public bool IncludeAvatar { get; init; } = true;
    public string? Error { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(Announcement))]
[JsonSerializable(typeof(WirePacket))]
internal partial class AppJsonContext : JsonSerializerContext;
