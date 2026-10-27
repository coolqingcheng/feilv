using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LanChat.Core.Models;
using LanChat.Infrastructure.Networking;
using LanChat.Infrastructure.Protocol;
using LanChat.Infrastructure.Storage;

namespace LanChat.IntegrationTests;

internal static class PresenceTests
{
    public static async Task<int> RunProbeAsync(string[] args)
    {
        var store = new IndexedFileStore(args[1]);
        await using var node = new LanService(store, await store.LoadProfileAsync(), new NodeOptions { DiscoveryPort = int.Parse(args[2]) });
        await node.StartAsync();
        var readyTemporary = args[3] + ".tmp";
        await File.WriteAllTextAsync(readyTemporary, node.Profile.Id);
        File.Move(readyTemporary, args[3], true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!File.Exists(args[4])) await Task.Delay(20, timeout.Token);
        return 0;
    }

    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        await DiscoveryReplyAsync(check);
        await LifecycleAsync(root, check);
        await ParallelExpiryAsync(root, check);
        await StaleSuccessAsync(root, check);
        await ResourceLifecycleAsync(root, check);
    }

    private static int FreePort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static Announcement Fake(int port) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Nickname = "Probe", Port = port, Fingerprint = new string('A', 64)
    };

    private static async Task SendAsync(int discoveryPort, Announcement announcement, IPAddress? source = null)
    {
        using var sender = new UdpClient(new IPEndPoint(source ?? IPAddress.Loopback, 0));
        await sender.SendAsync(JsonSerializer.SerializeToUtf8Bytes(announcement, AppJsonContext.Default.Announcement),
            new IPEndPoint(IPAddress.Loopback, discoveryPort));
    }

    private static async Task DiscoveryReplyAsync(Action<bool, string> check)
    {
        var port = FreePort();
        var self = Fake(45832);
        var requester = Fake(45833) with { RequestReply = true };
        await using var discovery = new DiscoveryService(port, () => self, (_, _) => Task.CompletedTask);
        discovery.Start();
        using var receiver = new UdpClient(AddressFamily.InterNetwork);
        receiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        receiver.Client.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), port));
        var timer = Stopwatch.StartNew();
        await SendAsync(port, requester, IPAddress.Parse("127.0.0.2"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
        var reply = await receiver.ReceiveAsync(timeout.Token);
        var response = JsonSerializer.Deserialize(reply.Buffer, AppJsonContext.Default.Announcement)!;
        check(response.Id == self.Id && !response.RequestReply && timer.ElapsedMilliseconds < 900,
            "discovery query receives immediate unicast reply without a reply loop");
        await SendAsync(port, requester, IPAddress.Parse("127.0.0.2"));
        await SendAsync(port, requester with { RequestReply = false }, IPAddress.Parse("127.0.0.2"));
        using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var duplicateReply = false;
        try { await receiver.ReceiveAsync(quiet.Token); duplicateReply = true; }
        catch (OperationCanceledException) { }
        check(!duplicateReply, "duplicate broadcasts are rate limited and response packets never trigger responses");
    }

    private static async Task LifecycleAsync(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "presence-process"); Directory.CreateDirectory(directory);
        var port = FreePort();
        var store = new IndexedFileStore(Path.Combine(directory, "observer"));
        await using var observer = new LanService(store, await store.LoadProfileAsync(), new NodeOptions { DiscoveryPort = port });
        await observer.StartAsync();
        var ready = Path.Combine(directory, "ready"); var stop = Path.Combine(directory, "stop");
        using (var child = StartProbe(Path.Combine(directory, "child"), port, ready, stop))
        {
            try
            {
                await UntilAsync(() => File.Exists(ready), 5000);
                var id = await File.ReadAllTextAsync(ready);
                await UntilAsync(() => observer.Peers.Any(x => x.Id == id && x.IsOnline), 2000);
                var timer = Stopwatch.StartNew();
                await File.WriteAllTextAsync(stop, "stop");
                await UntilAsync(() => observer.Peers.Any(x => x.Id == id && !x.IsOnline), 1400);
                check(timer.ElapsedMilliseconds < 1400, "normal process exit broadcasts offline immediately");
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { Stop(child); }
        }
        File.Delete(ready); File.Delete(stop);
        using var crash = StartProbe(Path.Combine(directory, "child"), port, ready, stop);
        try
        {
            await UntilAsync(() => File.Exists(ready), 5000);
            var id = await File.ReadAllTextAsync(ready);
            await UntilAsync(() => observer.Peers.Any(x => x.Id == id && x.IsOnline), 2000);
            check(true, "returning device with the same identity becomes online again");
            var timer = Stopwatch.StartNew();
            crash.Kill(); await crash.WaitForExitAsync();
            await UntilAsync(() => observer.Peers.Any(x => x.Id == id && !x.IsOnline), 5500);
            check(timer.ElapsedMilliseconds < 5500, "abrupt test-process exit expires within five seconds without goodbye");
        }
        finally { Stop(crash); }
    }

    private static async Task ParallelExpiryAsync(string root, Action<bool, string> check)
    {
        var port = FreePort();
        var store = new IndexedFileStore(Path.Combine(root, "presence-expiry"));
        await using var observer = new LanService(store, await store.LoadProfileAsync(), new NodeOptions { DiscoveryPort = port });
        await observer.StartAsync();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var peers = Enumerable.Range(0, 5).Select(_ => Fake(((IPEndPoint)listener.LocalEndpoint).Port)).ToArray();
        var sockets = new ConcurrentBag<TcpClient>();
        using var stop = new CancellationTokenSource();
        var accepted = 0;
        async Task AcceptAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    sockets.Add(await listener.AcceptTcpClientAsync(stop.Token));
                    Interlocked.Increment(ref accepted);
                }
            }
            catch (OperationCanceledException) { }
        }
        var accepting = AcceptAsync();
        try
        {
            foreach (var peer in peers) await SendAsync(port, peer);
            await UntilAsync(() => observer.Peers.Count == peers.Length, 1500);
            await UntilAsync(() => Volatile.Read(ref accepted) >= peers.Length * 2, 4500);
            // A heartbeat arriving during the expiry probe must win over its later failure.
            await SendAsync(port, peers[0]);
            await Task.Delay(900);
            check(observer.Peers.Single(x => x.Id == peers[0].Id).IsOnline,
                "failed stale probe cannot overwrite a newer heartbeat");
            check(observer.Peers.Where(x => x.Id != peers[0].Id).All(x => !x.IsOnline),
                "multiple unresponsive TLS peers expire in parallel instead of waiting ten seconds each");
        }
        finally
        {
            await stop.CancelAsync(); listener.Stop(); await accepting;
            foreach (var socket in sockets) socket.Dispose();
        }
    }

    private static async Task StaleSuccessAsync(string root, Action<bool, string> check)
    {
        var port = FreePort();
        var store = new IndexedFileStore(Path.Combine(root, "presence-race"));
        await using var observer = new LanService(store, await store.LoadProfileAsync(), new NodeOptions { DiscoveryPort = port });
        await observer.StartAsync();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=presence-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null,
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
        var peer = Fake(((IPEndPoint)listener.LocalEndpoint).Port) with { Fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256) };
        var receivedHello = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        async Task ServeAsync()
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate, ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, timeout.Token);
            await PacketIO.ReadAsync(stream, timeout.Token);
            receivedHello.SetResult();
            await release.Task.WaitAsync(timeout.Token);
            await PacketIO.WriteAsync(stream, new WirePacket { Type = "ok", Sender = peer }, timeout.Token);
        }
        var serving = ServeAsync();
        try
        {
            await SendAsync(port, peer);
            await receivedHello.Task.WaitAsync(timeout.Token);
            await SendAsync(port, peer with { IsOnline = false });
            await UntilAsync(() => observer.Peers.Any(x => x.Id == peer.Id && !x.IsOnline), 1000);
            release.SetResult(); await serving;
            await Task.Delay(200);
            check(!observer.Peers.Single(x => x.Id == peer.Id).IsOnline,
                "late successful profile handshake cannot resurrect a device after goodbye");
        }
        finally { release.TrySetResult(); listener.Stop(); }
    }

    private static Process StartProbe(string directory, int port, string ready, string stop)
    {
        var host = Environment.ProcessPath!;
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LanChat.IntegrationTests.dll"));
        foreach (var value in new[] { "--presence-probe", directory, port.ToString(), ready, stop }) start.ArgumentList.Add(value);
        return Process.Start(start)!;
    }

    private static async Task ResourceLifecycleAsync(string root, Action<bool, string> check)
    {
        var store = new IndexedFileStore(Path.Combine(root, "presence-resources"));
        var profile = await store.LoadProfileAsync();
        var options = new NodeOptions { DiscoveryPort = FreePort() };
        async Task CycleAsync(int duration)
        {
            await using var node = new LanService(store, profile, options);
            await node.StartAsync(); await Task.Delay(duration);
        }
        await CycleAsync(100);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh(); var handlesBefore = process.HandleCount;
        var memoryBefore = GC.GetTotalMemory(true);
        for (var index = 0; index < 6; index++) await CycleAsync(100);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        process.Refresh();
        var growth = process.HandleCount - handlesBefore;
        var memoryGrowth = GC.GetTotalMemory(true) - memoryBefore;
        check(growth <= 12 && memoryGrowth < 4 * 1024 * 1024,
            $"discovery start/stop releases sockets, timers and subscriptions (six cycles: handles {growth:+0;-0;0}, managed bytes {memoryGrowth:+0;-0;0})");
        await using var idle = new LanService(store, profile, options);
        await idle.StartAsync(); await Task.Delay(500);
        process.Refresh(); var cpuBefore = process.TotalProcessorTime;
        var allocationBefore = GC.GetTotalAllocatedBytes();
        var elapsed = Stopwatch.StartNew();
        await Task.Delay(3000);
        process.Refresh();
        Console.WriteLine($"RESOURCE: headless idle {elapsed.Elapsed.TotalSeconds:F1}s, CPU {(process.TotalProcessorTime - cpuBefore).TotalMilliseconds:F1}ms, managed allocations {GC.GetTotalAllocatedBytes() - allocationBefore:N0} bytes, process handles {process.HandleCount}");
    }

    private static void Stop(Process process)
    {
        if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
    }

    private static async Task UntilAsync(Func<bool> condition, int milliseconds)
    {
        using var timeout = new CancellationTokenSource(milliseconds);
        while (!condition()) await Task.Delay(15, timeout.Token);
    }
}
