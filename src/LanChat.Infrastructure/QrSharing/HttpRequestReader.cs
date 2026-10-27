using System.Buffers;
using System.Globalization;
using System.Text;

namespace LanChat.Infrastructure.QrSharing;

/// <summary>Bounded HTTP/1 request reader. One request is served per connection.</summary>
internal sealed class HttpRequestReader : IAsyncDisposable
{
    private const int MaximumHeader = 16384;
    private readonly Stream _stream;
    private readonly byte[] _header = ArrayPool<byte>.Shared.Rent(MaximumHeader);
    private int _prefixStart;
    private int _prefixEnd;
    public string Method { get; private set; } = "";
    public string Target { get; private set; } = "";
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HttpRequestReader(Stream stream) => _stream = stream;

    public async Task ReadAsync(CancellationToken ct)
    {
        var count = 0; var scanned = 0;
        while (count < MaximumHeader)
        {
            var read = await _stream.ReadAsync(_header.AsMemory(count, MaximumHeader - count), ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            count += read;
            for (var i = Math.Max(3, scanned); i < count; i++)
            {
                if (_header[i - 3] != 13 || _header[i - 2] != 10 || _header[i - 1] != 13 || _header[i] != 10) continue;
                Parse(Encoding.ASCII.GetString(_header, 0, i - 3));
                _prefixStart = i + 1; _prefixEnd = count;
                return;
            }
            scanned = count;
        }
        throw new InvalidDataException("请求头过长。");
    }

    private void Parse(string value)
    {
        var lines = value.Split("\r\n", StringSplitOptions.None);
        var first = lines[0].Split(' ');
        if (first.Length != 3 || first[2] is not ("HTTP/1.1" or "HTTP/1.0") || !first[1].StartsWith('/') || first[1].Length > 8192)
            throw new InvalidDataException("HTTP 请求无效。");
        Method = first[0]; Target = first[1];
        foreach (var line in lines.Skip(1))
        {
            var split = line.IndexOf(':');
            if (split <= 0 || line[..split].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || line.Any(c => c < 32 && c != '\t')
                || !Headers.TryAdd(line[..split], line[(split + 1)..].Trim())) throw new InvalidDataException("HTTP 请求头无效。");
        }
        if (Headers.ContainsKey("Transfer-Encoding") && Headers.ContainsKey("Content-Length")) throw new InvalidDataException("请求长度冲突。");
    }

    private async ValueTask<int> ReadBodyAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (_prefixStart >= _prefixEnd) return await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
        var count = Math.Min(buffer.Length, _prefixEnd - _prefixStart);
        _header.AsMemory(_prefixStart, count).CopyTo(buffer); _prefixStart += count;
        return count;
    }

    public async Task CopyBodyAsync(Stream destination, CancellationTokenSource deadline)
    {
        var chunked = Headers.TryGetValue("Transfer-Encoding", out var encoding);
        if (chunked && !string.Equals(encoding, "chunked", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("不支持的传输编码。");
        long size = 0;
        if (!chunked && (!Headers.TryGetValue("Content-Length", out var length) || !long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out size) || size < 0))
            throw new InvalidDataException("上传需要有效的内容长度。");
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (true)
            {
                if (chunked)
                {
                    var line = await ReadLineAsync(128, deadline).ConfigureAwait(false);
                    var number = line.Split(';', 2)[0];
                    if (!long.TryParse(number, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out size) || size < 0)
                        throw new InvalidDataException("上传分块无效。");
                    if (size == 0)
                    {
                        var trailers = 0;
                        while (true)
                        {
                            var trailer = await ReadLineAsync(4096, deadline).ConfigureAwait(false);
                            trailers += trailer.Length + 2;
                            if (trailers > 8192) throw new InvalidDataException("上传尾部过长。");
                            if (trailer.Length == 0) return;
                        }
                    }
                }
                while (size > 0)
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(60));
                    var read = await ReadBodyAsync(buffer.AsMemory(0, (int)Math.Min(65536, size)), deadline.Token).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException("上传中断。");
                    await destination.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                    size -= read;
                }
                if (!chunked) return;
                if ((await ReadLineAsync(2, deadline).ConfigureAwait(false)).Length != 0) throw new InvalidDataException("上传分块结尾无效。");
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task<string> ReadLineAsync(int maximum, CancellationTokenSource deadline)
    {
        var bytes = new byte[maximum + 2]; var count = 0;
        while (count < bytes.Length)
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            if (await ReadBodyAsync(bytes.AsMemory(count, 1), deadline.Token).ConfigureAwait(false) == 0) throw new EndOfStreamException();
            count++;
            if (count >= 2 && bytes[count - 2] == 13 && bytes[count - 1] == 10) return Encoding.ASCII.GetString(bytes, 0, count - 2);
        }
        throw new InvalidDataException("上传分块头过长。");
    }

    public ValueTask DisposeAsync() { ArrayPool<byte>.Shared.Return(_header); return ValueTask.CompletedTask; }
}
