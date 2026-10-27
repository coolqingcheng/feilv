using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LanChat.Core.Models;
using LanChat.Core.Localization;
using LanChat.Core.Services;
using LanChat.Infrastructure.Files;

namespace LanChat.Infrastructure.QrSharing;

/// <summary>On-demand LAN HTTP file server, with bounded connections and streamed bodies. No HTTP.sys registration is needed.</summary>
public sealed class QrHttpServer : IQrHttpServer
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private readonly ConcurrentDictionary<int, Task> _tasks = new();
    private readonly SemaphoreSlim _slots = new(8, 8);
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private Task? _accept;
    private string _uploadRoot = "";
    private string _downloadRoot = "";
    private int _nextId;
    private bool _disposed;
    public int Port { get; private set; }
    public string BasePath { get; private set; } = "";
    public event Func<QrUpload, Task>? UploadCompleted;

    public async Task StartAsync(string uploadDirectory, string downloadDirectory, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null) return;
            await Task.Run(() =>
            {
                Directory.CreateDirectory(uploadDirectory); Directory.CreateDirectory(downloadDirectory);
                _uploadRoot = SharedFileAccess.Resolve(new SharedFolder("qr", "", uploadDirectory), "");
                _downloadRoot = SharedFileAccess.Resolve(new SharedFolder("qr", "", downloadDirectory), "");
            }, cancellationToken).ConfigureAwait(false);
            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start(16);
            _listener = listener; _stop = new CancellationTokenSource();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            BasePath = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant() + "/";
            _accept = AcceptAsync(listener, _stop.Token);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _slots.WaitAsync(ct).ConfigureAwait(false);
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                catch { _slots.Release(); throw; }
                var id = Interlocked.Increment(ref _nextId);
                _clients[id] = client;
                var task = HandleAsync(client, ct); _tasks[id] = task;
                _ = task.ContinueWith(done => { _clients.TryRemove(id, out _); _tasks.TryRemove(id, out _); _ = done.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var stream = client.GetStream();
            var responseStarted = false;
            var webLanguage = Localizer.Language;
            try
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                await using var request = new HttpRequestReader(stream);
                await request.ReadAsync(deadline.Token).ConfigureAwait(false);
                var requestedLanguage = Query(request.Target, "lang");
                webLanguage = requestedLanguage is "zh-CN" or "en-US" ? requestedLanguage
                    : request.Headers.TryGetValue("Accept-Language", out var accepted)
                        ? (accepted.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US") : Localizer.Language;
                deadline.CancelAfter(TimeSpan.FromSeconds(60));
                var queryAt = request.Target.IndexOf('?');
                var route = queryAt < 0 ? request.Target : request.Target[..queryAt];
                if (!route.StartsWith(BasePath, StringComparison.Ordinal)) { await TextAsync(404, "分享链接无效或已停止。"); return; }
                var relative = Query(request.Target, "path");
                if (request.Method is "GET" or "HEAD")
                {
                    if (route == BasePath)
                    {
                        var root = new SharedFolder("qr", "", _downloadRoot);
                        var entries = SharedFileAccess.Browse(root, relative).Where(e => !e.Name.StartsWith(".feilu-upload-", StringComparison.Ordinal)).ToList();
                        var html = QrWebPage.Render(BasePath, relative, entries, webLanguage);
                        await BytesAsync(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), request.Method == "HEAD");
                    }
                    else if (route == BasePath + "download")
                    {
                        var path = SharedFileAccess.Resolve(new SharedFolder("qr", "", _downloadRoot), relative);
                        if (Path.GetFileName(path).StartsWith(".feilu-upload-", StringComparison.Ordinal)) throw new UnauthorizedAccessException();
                        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        long offset = 0; var length = file.Length; var code = 200;
                        var extra = "Accept-Ranges: bytes\r\nContent-Disposition: attachment; filename*=UTF-8''" + Uri.EscapeDataString(Path.GetFileName(path)) + "\r\n";
                        if (request.Headers.TryGetValue("Range", out var range))
                        {
                            if (!TryRange(range, file.Length, out offset, out length))
                            {
                                await HeadersAsync(416, "text/plain", 0, "Content-Range: bytes */" + file.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"); return;
                            }
                            code = 206; extra += $"Content-Range: bytes {offset}-{offset + length - 1}/{file.Length}\r\n";
                        }
                        await HeadersAsync(code, "application/octet-stream", length, extra);
                        if (request.Method != "HEAD")
                        {
                            file.Position = offset;
                            var buffer = ArrayPool<byte>.Shared.Rent(65536);
                            try
                            {
                                while (length > 0)
                                {
                                    deadline.CancelAfter(TimeSpan.FromSeconds(60));
                                    var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(65536, length)), deadline.Token).ConfigureAwait(false);
                                    if (read == 0) throw new EndOfStreamException();
                                    await stream.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false); length -= read;
                                }
                            }
                            finally { ArrayPool<byte>.Shared.Return(buffer); }
                        }
                    }
                    else await TextAsync(404, "找不到此页面。");
                }
                else if (request.Method == "POST" && route == BasePath + "upload")
                {
                    if (request.Headers.TryGetValue("Origin", out var origin)
                        && (!request.Headers.TryGetValue("Host", out var host) || !string.Equals(origin, "http://" + host, StringComparison.OrdinalIgnoreCase)))
                        throw new UnauthorizedAccessException();
                    var name = Query(request.Target, "name");
                    ValidateFileName(name);
                    var root = SharedFileAccess.Resolve(new SharedFolder("qr", "", _uploadRoot), "");
                    var id = Guid.NewGuid().ToString("N");
                    var temporary = Path.Combine(root, ".feilu-upload-" + id + ".partial");
                    try
                    {
                        if (request.Headers.TryGetValue("Expect", out var expect))
                        {
                            if (!string.Equals(expect, "100-continue", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("不支持的上传请求。");
                            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), deadline.Token).ConfigureAwait(false);
                        }
                        var started = Stopwatch.GetTimestamp();
                        long size;
                        await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                        {
                            await request.CopyBodyAsync(file, deadline).ConfigureAwait(false);
                            size = file.Length;
                        }
                        var saved = CommitUpload(temporary, root, name);
                        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
                        var upload = new QrUpload(id, Path.Combine(root, saved), saved, size,
                            (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "", DateTimeOffset.UtcNow,
                            elapsed > 0 ? size / elapsed : 0);
                        await NotifyUploadAsync(upload).ConfigureAwait(false);
                        await TextAsync(201, "已上传：" + saved);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                else await TextAsync(405, "不支持此操作。");
            }
            catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException or OperationCanceledException or InvalidDataException or ArgumentException or ObjectDisposedException)
            {
                if (!responseStarted && !deadline.IsCancellationRequested)
                {
                    try { await TextAsync(ex is UnauthorizedAccessException ? 403 : ex is FileNotFoundException or DirectoryNotFoundException ? 404 : ex is InvalidDataException or ArgumentException ? 400 : 500, "无法完成操作，请检查文件和目录后重试。"); }
                    catch (Exception sendError) when (sendError is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                }
            }
            finally { _slots.Release(); }

            async Task HeadersAsync(int code, string type, long length, string extra = "")
            {
                responseStarted = true;
                var reason = code switch { 200 => "OK", 201 => "Created", 206 => "Partial Content", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 416 => "Range Not Satisfiable", _ => "Internal Server Error" };
                var headers = $"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\nContent-Length: {length.ToString(CultureInfo.InvariantCulture)}\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'\r\n{extra}\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), deadline.Token).ConfigureAwait(false);
            }
            async Task BytesAsync(int code, string type, byte[] bytes, bool head = false)
            {
                await HeadersAsync(code, type, bytes.Length);
                if (!head) await stream.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
            }
            Task TextAsync(int code, string text) => BytesAsync(code, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(Localizer.Translate(text, webLanguage)));
        }
    }

    private async Task NotifyUploadAsync(QrUpload upload)
    {
        if (UploadCompleted is not { } completed) return;
        // Await observers within the existing eight connection slots instead of creating unbounded background tasks.
        foreach (Func<QrUpload, Task> observer in completed.GetInvocationList())
        {
            try { await observer(upload).ConfigureAwait(false); }
            catch (Exception ex) { Trace.TraceError("QR upload completion notification failed: {0}", ex); }
        }
    }

    private static string Query(string target, string key)
    {
        var at = target.IndexOf('?');
        if (at < 0) return "";
        foreach (var item in target[(at + 1)..].Split('&'))
        {
            var parts = item.Split('=', 2);
            if (parts[0] == key) return Uri.UnescapeDataString(parts.Length == 2 ? parts[1].Replace('+', ' ') : "");
        }
        return "";
    }

    private static void ValidateFileName(string name)
    {
        if (name.Length is < 1 or > 240 || name is "." or ".." || name.Contains('/') || name.Contains('\\') || name.Contains(':')
            || name.EndsWith(' ') || name.EndsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.StartsWith(".feilu-upload-", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("文件名无效。");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9'))
            throw new InvalidDataException("文件名无效。");
    }

    private static string CommitUpload(string temporary, string root, string name)
    {
        for (var i = 0; i < 10000; i++)
        {
            var candidate = i == 0 ? name : Path.GetFileNameWithoutExtension(name) + " (" + i.ToString(CultureInfo.InvariantCulture) + ")" + Path.GetExtension(name);
            try { File.Move(temporary, Path.Combine(root, candidate), false); return candidate; }
            catch (IOException) when (File.Exists(Path.Combine(root, candidate)) || Directory.Exists(Path.Combine(root, candidate))) { }
        }
        throw new IOException("同名文件过多。");
    }

    private static bool TryRange(string text, long total, out long start, out long count)
    {
        start = count = 0;
        if (total == 0 || !text.StartsWith("bytes=", StringComparison.Ordinal) || text.Contains(',')) return false;
        var parts = text[6..].Split('-', 2);
        if (parts.Length != 2) return false;
        if (parts[0].Length == 0)
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var tail) || tail <= 0) return false;
            count = Math.Min(total, tail); start = total - count; return true;
        }
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) || start < 0 || start >= total) return false;
        var end = total - 1;
        if (parts[1].Length != 0 && !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end)) return false;
        if (end < start) return false;
        count = Math.Min(total - 1, end) - start + 1; return true;
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stop is null) return;
            await _stop.CancelAsync().ConfigureAwait(false); _listener?.Stop();
            foreach (var client in _clients.Values) client.Dispose();
            if (_accept is not null) await _accept.ConfigureAwait(false);
            await Task.WhenAll(_tasks.Values).ConfigureAwait(false);
            _stop.Dispose(); _stop = null; _listener = null; _accept = null; Port = 0; BasePath = "";
        }
        finally { _lifecycle.Release(); }
    }

    public void Dispose()
    {
        _disposed = true; _stop?.Cancel(); _listener?.Stop();
        foreach (var client in _clients.Values) client.Dispose();
    }
    public async ValueTask DisposeAsync() { _disposed = true; await StopAsync().ConfigureAwait(false); }
}
