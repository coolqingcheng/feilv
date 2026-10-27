using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanChat.Core.Models;

namespace LanChat.Infrastructure.Storage;

/// <summary>Append-only metadata data file with a rebuildable fixed-record index. File bodies live elsewhere.</summary>
internal sealed class RecordFileIndex : IAsyncDisposable
{
    internal sealed class CorruptIndexException : IOException;
    private const int HeaderSize = 64;
    private const int EntrySize = 64;
    private const int DataHeaderSize = 16;
    private const int BlockSize = EntrySize * 512;
    private static readonly uint[] CrcTable = CreateCrcTable();
    private readonly FileStream _data;
    private readonly FileStream _index;
    private readonly CancellationToken _ct;
    private long _count;
    private bool _dirty;
    private static ReadOnlySpan<byte> DataMagic => "FEILUDAT00000001"u8;
    private static ReadOnlySpan<byte> IndexMagic => "FEILUIDX"u8;

    internal readonly record struct Entry(StoredKind Kind, byte MessageKind, ulong HashA, ulong HashB, long Offset, int Length, uint Crc, long Time, ulong NameBloom);

    private RecordFileIndex(FileStream data, FileStream index, CancellationToken ct) { _data = data; _index = index; _ct = ct; }

    public static async Task<RecordFileIndex> OpenAsync(string dataPath, string indexPath, CancellationToken ct)
    {
        var data = new FileStream(dataPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.Asynchronous);
        FileStream? index = null;
        try
        {
            index = new FileStream(indexPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.Asynchronous);
            var store = new RecordFileIndex(data, index, ct);
            await store.InitializeAsync().ConfigureAwait(false);
            return store;
        }
        catch { await data.DisposeAsync(); if (index is not null) await index.DisposeAsync(); throw; }
    }

    private async Task InitializeAsync()
    {
        if (_data.Length == 0) { await _data.WriteAsync(DataMagic.ToArray(), _ct).ConfigureAwait(false); await _data.FlushAsync(_ct).ConfigureAwait(false); }
        var magic = new byte[DataHeaderSize];
        _data.Position = 0;
        if (_data.Length < DataHeaderSize) throw new InvalidDataException("记录存储文件头不完整。");
        await _data.ReadExactlyAsync(magic, _ct).ConfigureAwait(false);
        if (!magic.AsSpan().SequenceEqual(DataMagic)) throw new InvalidDataException("记录存储文件版本无效。");
        var header = new byte[HeaderSize];
        if (_index.Length >= HeaderSize)
        {
            _index.Position = 0; await _index.ReadExactlyAsync(header, _ct).ConfigureAwait(false);
            _count = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
            if (header.AsSpan(0, 8).SequenceEqual(IndexMagic) && header[8] == 1 && header[32] == 0
                && Crc32(header.AsSpan(0, 60)) == BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(60))
                && _count >= 0 && _count == (_index.Length - HeaderSize) / EntrySize && _index.Length == HeaderSize + _count * EntrySize
                && BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24)) == _data.Length) return;
        }
        await RebuildAsync().ConfigureAwait(false);
    }

    public async Task<StoredRecord?> GetAsync(StoredKind kind, string key)
    {
        var found = await FindAsync(kind, key).ConfigureAwait(false);
        return found?.Item3;
    }

    private async Task<(long, Entry, StoredRecord)?> FindAsync(StoredKind kind, string key)
    {
        var hash = KeyHash(kind, key);
        await foreach (var item in EntriesAsync())
            if (item.Record.Kind == kind && item.Record.HashA == hash.Item1 && item.Record.HashB == hash.Item2)
            {
                var record = await ReadAsync(item.Record).ConfigureAwait(false);
                if (record.Key == key) return (item.Slot, item.Record, record);
            }
        return null;
    }

    public async Task PutAsync(StoredRecord record)
    {
        var existing = await FindAsync(record.Kind, record.Key).ConfigureAwait(false);
        await BeginWriteAsync().ConfigureAwait(false);
        var entry = await AppendAsync(record).ConfigureAwait(false);
        var slot = existing?.Item1 ?? _count++;
        await WriteEntryAsync(slot, entry).ConfigureAwait(false);
    }

    public async Task DeleteAsync(StoredKind kind, string key)
    {
        var existing = await FindAsync(kind, key).ConfigureAwait(false);
        if (existing is null) return;
        await BeginWriteAsync().ConfigureAwait(false);
        await AppendAsync(new StoredRecord { Kind = StoredKind.Deleted, DeletedKind = kind, Key = key }).ConfigureAwait(false);
        await WriteEntryAsync(existing.Value.Item1, existing.Value.Item2 with { Kind = StoredKind.Deleted }).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<(long Slot, Entry Record)> EntriesAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
        try
        {
            var end = _count;
            while (end > 0)
            {
                _ct.ThrowIfCancellationRequested();
                var start = Math.Max(0, end - BlockSize / EntrySize);
                var length = checked((int)(end - start) * EntrySize);
                _index.Position = HeaderSize + start * EntrySize;
                await _index.ReadExactlyAsync(buffer.AsMemory(0, length), _ct).ConfigureAwait(false);
                for (var slot = end - 1; slot >= start; slot--)
                {
                    var entry = Decode(buffer, checked((int)(slot - start) * EntrySize));
                    if (entry.Kind != StoredKind.Deleted) yield return (slot, entry);
                }
                end = start;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public async Task<StoredRecord> ReadAsync(Entry entry)
    {
        if (entry.Length is < 1 or > 16 * 1024 * 1024 || entry.Offset < DataHeaderSize || entry.Offset > _data.Length - entry.Length)
            throw new InvalidDataException("记录索引指向无效位置。");
        var bytes = new byte[entry.Length]; _data.Position = entry.Offset;
        await _data.ReadExactlyAsync(bytes, _ct).ConfigureAwait(false);
        if (Crc32(bytes) != entry.Crc) throw new InvalidDataException("记录数据校验失败。");
        return JsonSerializer.Deserialize(bytes, RecordJsonContext.Default.StoredRecord) ?? throw new InvalidDataException("记录数据格式无效。");
    }

    private async Task<Entry> AppendAsync(StoredRecord record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, RecordJsonContext.Default.StoredRecord);
        if (bytes.Length is < 1 or > 16 * 1024 * 1024) throw new InvalidDataException("单条记录元数据过大。");
        var crc = Crc32(bytes);
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xF17EC001);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), crc);
        _data.Position = _data.Length;
        await _data.WriteAsync(header, _ct).ConfigureAwait(false);
        var offset = _data.Position;
        await _data.WriteAsync(bytes, _ct).ConfigureAwait(false);
        return MakeEntry(record, offset, bytes.Length, crc);
    }

    private static Entry MakeEntry(StoredRecord record, long offset, int length, uint crc)
    {
        var hash = KeyHash(record.Kind, record.Key);
        var time = record.Message is { } message ? (message.ReceivedAt ?? message.Timestamp).ToUnixTimeMilliseconds() : 0;
        return new Entry(record.Kind, record.IsDeleted ? byte.MaxValue : (byte)(record.Message?.Kind ?? MessageKind.Text), hash.Item1, hash.Item2, offset, length, crc, time, Bloom(record.Message?.FileName ?? ""));
    }

    private async Task BeginWriteAsync()
    {
        if (_dirty) return;
        _dirty = true; await WriteHeaderAsync(true).ConfigureAwait(false); await _index.FlushAsync(_ct).ConfigureAwait(false);
    }

    public async Task CommitAsync()
    {
        if (!_dirty) return;
        await _data.FlushAsync(_ct).ConfigureAwait(false);
        await _index.FlushAsync(_ct).ConfigureAwait(false);
        await WriteHeaderAsync(false).ConfigureAwait(false);
        await _index.FlushAsync(_ct).ConfigureAwait(false);
        _dirty = false;
    }

    private async Task WriteHeaderAsync(bool dirty)
    {
        var bytes = new byte[HeaderSize]; IndexMagic.CopyTo(bytes); bytes[8] = 1; bytes[32] = dirty ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), _count);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), _data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), Crc32(bytes.AsSpan(0, 60)));
        _index.Position = 0; await _index.WriteAsync(bytes, _ct).ConfigureAwait(false);
    }

    private async Task WriteEntryAsync(long slot, Entry entry)
    {
        var bytes = new byte[EntrySize]; bytes[0] = (byte)entry.Kind; bytes[1] = entry.MessageKind;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), entry.HashA); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), entry.HashB);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), entry.Offset); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32), entry.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), entry.Crc); BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(40), entry.Time);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48), entry.NameBloom); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), Crc32(bytes.AsSpan(0, 60)));
        _index.Position = HeaderSize + slot * EntrySize; await _index.WriteAsync(bytes, _ct).ConfigureAwait(false);
    }

    private static Entry Decode(byte[] bytes, int offset)
    {
        var span = bytes.AsSpan(offset, EntrySize);
        if (Crc32(span[..60]) != BinaryPrimitives.ReadUInt32LittleEndian(span[60..])) throw new CorruptIndexException();
        return new Entry((StoredKind)span[0], span[1], BinaryPrimitives.ReadUInt64LittleEndian(span[8..]), BinaryPrimitives.ReadUInt64LittleEndian(span[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(span[24..]), BinaryPrimitives.ReadInt32LittleEndian(span[32..]), BinaryPrimitives.ReadUInt32LittleEndian(span[36..]),
            BinaryPrimitives.ReadInt64LittleEndian(span[40..]), BinaryPrimitives.ReadUInt64LittleEndian(span[48..]));
    }

    internal async Task RebuildAsync()
    {
        _count = 0; _index.SetLength(HeaderSize); _dirty = true;
        await WriteHeaderAsync(true).ConfigureAwait(false);
        var positions = new Dictionary<(StoredKind, string), long>();
        long position = DataHeaderSize;
        var header = new byte[16];
        while (position < _data.Length)
        {
            _ct.ThrowIfCancellationRequested();
            if (_data.Length - position < header.Length) { _data.SetLength(position); break; }
            _data.Position = position; await _data.ReadExactlyAsync(header, _ct).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0xF17EC001 || length is < 1 or > 16 * 1024 * 1024) throw new InvalidDataException("记录数据文件损坏。");
            if (_data.Length - position - header.Length < length) { _data.SetLength(position); break; }
            var offset = position + header.Length;
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            var temporary = new Entry(0, 0, 0, 0, offset, length, crc, 0, 0);
            var record = await ReadAsync(temporary).ConfigureAwait(false);
            var key = (record.Kind == StoredKind.Deleted ? record.DeletedKind : record.Kind, record.Key);
            if (record.Kind == StoredKind.Deleted)
            {
                if (positions.Remove(key, out var deleted)) await WriteEntryAsync(deleted, MakeEntry(record, offset, length, crc)).ConfigureAwait(false);
            }
            else
            {
                if (!positions.TryGetValue(key, out var slot)) { slot = _count++; positions.Add(key, slot); }
                await WriteEntryAsync(slot, MakeEntry(record, offset, length, crc)).ConfigureAwait(false);
            }
            position = offset + length;
        }
        await CommitAsync().ConfigureAwait(false);
    }

    internal static ulong Bloom(string value)
    {
        ulong bits = 0;
        foreach (var c in value.ToUpperInvariant()) bits |= 1UL << (int)((c * 31u + ((uint)c >> 4)) % 64);
        return bits;
    }

    private static (ulong, ulong) KeyHash(StoredKind kind, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(((int)kind).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0" + key));
        return (BinaryPrimitives.ReadUInt64LittleEndian(hash), BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(8)));
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var b in bytes) crc = (crc >> 8) ^ CrcTable[(byte)(crc ^ b)];
        return ~crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint value = 0; value < table.Length; value++)
        {
            var crc = value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1)));
            table[value] = crc;
        }
        return table;
    }

    public async ValueTask DisposeAsync() { await _index.DisposeAsync(); await _data.DisposeAsync(); }
}
