using System.Text.Json.Serialization;
using LanChat.Core.Models;

namespace LanChat.Infrastructure.Storage;

internal enum StoredKind { Deleted, Message, Peers, Attachment, Identity, Emojis }

internal sealed record StoredRecord
{
    public required StoredKind Kind { get; init; }
    public required string Key { get; init; }
    public StoredKind DeletedKind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsDeleted { get; init; }
    public ChatMessage? Message { get; init; }
    public List<Peer>? Peers { get; init; }
    public Attachment? Attachment { get; init; }
    public string? Fingerprint { get; init; }
    public List<EmojiItem>? Emojis { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StoredRecord))]
internal partial class RecordJsonContext : JsonSerializerContext;
