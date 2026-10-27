using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LanChat.Core.Models;
using LanChat.Infrastructure.QrSharing;
using LanChat.Infrastructure.Storage;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static async Task RunQrHttpTestsAsync()
    {
        var root = Path.Combine(TestRoot, "qr-http-tests");
        var uploads = Path.Combine(root, "uploads"); var downloads = Path.Combine(root, "downloads");
        await using var server = new QrHttpServer();
        Check(server.Port == 0, "QR HTTP server has no listener until explicitly started");
        await server.StartAsync(uploads, downloads);
        var address = $"http://127.0.0.1:{server.Port}{server.BasePath}";
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
        var page = await client.GetStringAsync(address);
        Check(page.Contains("上传到电脑", StringComparison.Ordinal) && page.Contains("下载文件", StringComparison.Ordinal)
            && Directory.Exists(uploads) && Directory.Exists(downloads), "QR URL serves a self-contained upload/download page and creates separate roots");
        var script = page[(page.IndexOf("<script>", StringComparison.Ordinal) + 8)..page.IndexOf("</script>", StringComparison.Ordinal)];
        Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), ".build"));
        await File.WriteAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), ".build", "qr-upload.js"), script);
        Check((await client.GetAsync($"http://127.0.0.1:{server.Port}/")).StatusCode == HttpStatusCode.NotFound, "requests without the current sharing link cannot browse files");
        var payload = Encoding.UTF8.GetBytes("来自手机的文件\n异步传输");
        using (var response = await client.PostAsync(address + "upload?name=" + Uri.EscapeDataString("手机 图片 &资料.txt"), new ByteArrayContent(payload)))
            Check(response.StatusCode == HttpStatusCode.Created && (await File.ReadAllBytesAsync(Path.Combine(uploads, "手机 图片 &资料.txt"))).SequenceEqual(payload), "browser-style raw upload preserves Unicode filename and content");
        using (var response = await client.PostAsync(address + "upload?name=" + Uri.EscapeDataString("手机 图片 &资料.txt"), new ByteArrayContent([42])))
            Check(response.StatusCode == HttpStatusCode.Created && File.Exists(Path.Combine(uploads, "手机 图片 &资料 (1).txt"))
                && (await File.ReadAllBytesAsync(Path.Combine(uploads, "手机 图片 &资料.txt"))).SequenceEqual(payload), "duplicate upload names are renamed without overwriting the original file");
        using (var request = new HttpRequestMessage(HttpMethod.Post, address + "upload?name=chunked.bin") { Content = new ByteArrayContent(payload) })
        {
            request.Headers.TransferEncodingChunked = true;
            using var response = await client.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.Created && (await File.ReadAllBytesAsync(Path.Combine(uploads, "chunked.bin"))).SequenceEqual(payload), "chunked uploads are decoded incrementally");
        }
        using (var response = await client.PostAsync(address + "upload?name=empty.txt", new ByteArrayContent([])))
            Check(response.StatusCode == HttpStatusCode.Created && new FileInfo(Path.Combine(uploads, "empty.txt")).Length == 0, "HTTP upload supports empty files");
        Check((await client.GetAsync(address + "download?path=empty.txt")).StatusCode == HttpStatusCode.NotFound, "upload-only files are not exposed through a separately configured download root");
        Directory.CreateDirectory(Path.Combine(downloads, "子目录"));
        var relative = Path.Combine("子目录", "共享 文档&.txt");
        await File.WriteAllBytesAsync(Path.Combine(downloads, relative), payload);
        Check((await client.GetStringAsync(address)).Contains("子目录", StringComparison.Ordinal)
            && (await client.GetStringAsync(address + "?path=" + Uri.EscapeDataString("子目录"))).Contains("共享 文档&amp;.txt", StringComparison.Ordinal), "web directory browser opens nested folders and escapes filenames");
        var downloadUrl = address + "download?path=" + Uri.EscapeDataString(relative);
        using (var response = await client.GetAsync(downloadUrl))
            Check(response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(payload)
                && response.Content.Headers.ContentDisposition?.DispositionType == "attachment", "HTTP downloads return the exact file as an attachment");
        using (var request = new HttpRequestMessage(HttpMethod.Head, downloadUrl))
        using (var response = await client.SendAsync(request))
            Check(response.Content.Headers.ContentLength == payload.Length && (await response.Content.ReadAsByteArrayAsync()).Length == 0, "HEAD returns download metadata without a file body");
        using (var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl))
        {
            request.Headers.Range = new RangeHeaderValue(1, 5);
            using var response = await client.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.PartialContent && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(payload[1..6]), "single HTTP byte ranges support resumed downloads");
        }
        foreach (var path in new[] { "../outside.txt", "C:/Windows/win.ini", "nested/../../outside.txt", "file:stream" })
        {
            using var response = await client.GetAsync(address + "download?path=" + Uri.EscapeDataString(path));
            Check(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest, "HTTP download rejects path escape " + path);
        }
        foreach (var name in new[] { "../escape.txt", "C:\\escape.txt", "CON.txt", ".. ", "bad:stream" })
        {
            using var response = await client.PostAsync(address + "upload?name=" + Uri.EscapeDataString(name), new ByteArrayContent(payload));
            Check(response.StatusCode == HttpStatusCode.BadRequest, "HTTP upload rejects unsafe name " + name);
        }
        using (var request = new HttpRequestMessage(HttpMethod.Post, address + "upload?name=foreign.txt") { Content = new ByteArrayContent(payload) })
        {
            request.Headers.Add("Origin", "http://unrelated.example");
            using var response = await client.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.Forbidden && !File.Exists(Path.Combine(uploads, "foreign.txt")), "HTTP upload rejects a different browser origin");
        }
        await RunHttpStreamingCheckAsync(server, client, uploads, downloads);
        using (var partial = new TcpClient())
        {
            await partial.ConnectAsync("127.0.0.1", server.Port);
            await partial.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"POST {server.BasePath}upload?name=incomplete.bin HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nContent-Length: 1000000\r\n\r\nabc"));
            using var wait = new CancellationTokenSource(5000);
            while (!Directory.EnumerateFiles(uploads, ".feilu-upload-*").Any()) await Task.Delay(10, wait.Token);
            await server.StopAsync();
            Check(server.Port == 0 && !Directory.EnumerateFiles(uploads, ".feilu-upload-*").Any() && !File.Exists(Path.Combine(uploads, "incomplete.bin")),
                "stopping HTTP sharing cancels active uploads, removes partial bodies and releases the listener");
        }
        await server.StartAsync(uploads, downloads);
        Check(!address.EndsWith(server.BasePath, StringComparison.Ordinal), "restarting HTTP sharing creates a fresh QR link");
        using (var response = await client.GetAsync($"http://127.0.0.1:{server.Port}" + new Uri(address).AbsolutePath))
            Check(response.StatusCode == HttpStatusCode.NotFound, "a previous QR link stays invalid after sharing restarts");
        await server.StopAsync();

        var configRoot = Path.Combine(TestRoot, "qr-config"); var store = new IndexedFileStore(configRoot);
        var profile = await store.LoadProfileAsync(); profile.QrUploadDirectory = uploads; profile.QrDownloadDirectory = downloads;
        await store.SaveProfileAsync(profile);
        var loaded = XmlConfiguration.Load(configRoot).Profile;
        Check(loaded.QrUploadDirectory == uploads && loaded.QrDownloadDirectory == downloads, "independent QR roots persist in Data/config.xml and resolve relative paths");
    }

    private static async Task RunHttpStreamingCheckAsync(QrHttpServer server, HttpClient client, string uploads, string downloads)
    {
        const int size = 64 * 1024 * 1024;
        var path = Path.Combine(downloads, "64MiB.bin");
        var block = RandomNumberGenerator.GetBytes(65536);
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            for (var i = 0; i < size / block.Length; i++) await file.WriteAsync(block);
        var address = $"http://127.0.0.1:{server.Port}{server.BasePath}";
        var allocated = GC.GetTotalAllocatedBytes(true);
        await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
        using (var response = await client.PostAsync(address + "upload?name=64MiB.bin", new StreamContent(source)))
            Check(response.StatusCode == HttpStatusCode.Created && new FileInfo(Path.Combine(uploads, "64MiB.bin")).Length == size, "HTTP server streams a complete 64 MiB upload to disk");
        var savedPath = Path.Combine(TestRoot, "http-downloaded.bin");
        using (var response = await client.GetAsync(address + "download?path=64MiB.bin", HttpCompletionOption.ResponseHeadersRead))
        await using (var output = new FileStream(savedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            await response.Content.CopyToAsync(output);
        var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
        Check(bytes < size / 2, $"HTTP upload/download uses bounded buffers (64 MiB, total managed allocations {bytes:N0} bytes)");
        await using var original = File.OpenRead(path); await using var uploaded = File.OpenRead(Path.Combine(uploads, "64MiB.bin")); await using var downloaded = File.OpenRead(savedPath);
        var hash = await SHA256.HashDataAsync(original);
        var uploadedHash = await SHA256.HashDataAsync(uploaded); var downloadedHash = await SHA256.HashDataAsync(downloaded);
        Check(hash.SequenceEqual(uploadedHash) && hash.SequenceEqual(downloadedHash), "HTTP upload and download preserve SHA-256 for the entire large file");
    }

    private static async Task<int> RunQrHttpPreviewAsync(string[] args)
    {
        await using var server = new QrHttpServer();
        await server.StartAsync(args[1], args[1]);
        await File.WriteAllTextAsync(args[2], $"http://127.0.0.1:{server.Port}{server.BasePath}");
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        while (!File.Exists(args[3])) await Task.Delay(100, limit.Token);
        return 0;
    }
}
