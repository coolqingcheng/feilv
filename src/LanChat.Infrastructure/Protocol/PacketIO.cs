using System.Buffers.Binary;
using System.Text.Json;

namespace LanChat.Infrastructure.Protocol;

internal static class PacketIO
{
    private const int MaxPacketBytes = 512 * 1024;

    public static async Task WriteAsync(Stream stream, WirePacket packet, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(packet, AppJsonContext.Default.WirePacket);
        if (bytes.Length > MaxPacketBytes) throw new InvalidDataException("消息超过大小限制。");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<WirePacket> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaxPacketBytes) throw new InvalidDataException("无效的数据包长度。");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct);
        return JsonSerializer.Deserialize(bytes, AppJsonContext.Default.WirePacket)
            ?? throw new InvalidDataException("无法读取消息。");
    }
}
