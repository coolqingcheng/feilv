using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using LanChat.Infrastructure.Protocol;

namespace LanChat.Infrastructure.Networking;

internal sealed class DiscoveryService(int port, Func<Announcement> announcement, Func<Announcement, IPAddress, Task> discovered) : IAsyncDisposable
{
    private static readonly IPAddress Group = IPAddress.Parse("239.47.23.18");
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private bool _leaving;
    private int _disposed;
    private readonly string _selfId = announcement().Id;
    private readonly Dictionary<IPAddress, UdpClient> _senders = [];
    private readonly Dictionary<string, DateTimeOffset> _lastReplies = [];
    private IPAddress[] _addresses = [];
    private DateTimeOffset _lastInterfaceRefresh;
    private volatile bool _interfacesChanged = true;
    private UdpClient? _replySender;
    private UdpClient? _receiver;
    private Task? _receiveTask;
    private Task? _sendTask;

    public void Start()
    {
        _receiver = new UdpClient(AddressFamily.InterNetwork);
        _receiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _receiver.ExclusiveAddressUse = false;
        _receiver.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        _addresses = InterfaceAddresses();
        _lastInterfaceRefresh = DateTimeOffset.UtcNow;
        _interfacesChanged = false;
        foreach (var address in _addresses)
        {
            try { _receiver.JoinMulticastGroup(Group, address); }
            catch (SocketException) { /* Other interfaces may still work. */ }
        }
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _receiveTask = ReceiveAsync(_stop.Token);
        _sendTask = SendLoopAsync(_stop.Token);
    }

    public async Task AnnounceAsync(CancellationToken ct, bool requestReply = false)
    {
        await _sendGate.WaitAsync(ct);
        try { if (!_leaving) await SendAsync(announcement() with { RequestReply = requestReply }, ct); }
        finally { _sendGate.Release(); }
    }

    private async Task SendAsync(Announcement value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, AppJsonContext.Default.Announcement);
        RefreshInterfaces();
        foreach (var address in _addresses)
        {
            try
            {
                if (!_senders.TryGetValue(address, out var sender))
                {
                    sender = new UdpClient(new IPEndPoint(address, 0));
                    _senders.Add(address, sender);
                    sender.MulticastLoopback = true; sender.Ttl = 1; sender.EnableBroadcast = true;
                }
                await sender.SendAsync(bytes, new IPEndPoint(Group, port), ct);
                await sender.SendAsync(bytes, new IPEndPoint(IPAddress.Broadcast, port), ct);
            }
            catch (SocketException)
            {
                if (_senders.Remove(address, out var failed)) failed.Dispose();
            }
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            await AnnounceAsync(ct, requestReply: true);
            while (await timer.WaitForNextTickAsync(ct)) await AnnounceAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task ReceiveAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var datagram = await _receiver!.ReceiveAsync(ct);
                if (datagram.Buffer.Length > 4096) continue;
                var value = JsonSerializer.Deserialize(datagram.Buffer, AppJsonContext.Default.Announcement);
                if (value is { App: "LanChat", Version: 1, Port: > 0 and <= 65535, Fingerprint: { Length: 64 }, Nickname: { Length: > 0 and <= 40 } }
                    && Guid.TryParseExact(value.Id, "N", out _) && value.Id != _selfId)
                {
                    await discovered(value, datagram.RemoteEndPoint.Address);
                    if (value.IsOnline && value.RequestReply) await ReplyAsync(value.Id, datagram.RemoteEndPoint.Address, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (ex is SocketException or JsonException or InvalidDataException or IOException) { }
        }
    }

    private async Task ReplyAsync(string id, IPAddress address, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            if (_leaving) return;
            var now = DateTimeOffset.UtcNow;
            if (_lastReplies.TryGetValue(id, out var last) && now - last < TimeSpan.FromSeconds(1)) return;
            _lastReplies[id] = now;
            if (_lastReplies.Count > 128)
                foreach (var old in _lastReplies.Where(x => now - x.Value > TimeSpan.FromSeconds(10)).Select(x => x.Key).ToArray()) _lastReplies.Remove(old);
            _replySender ??= new UdpClient(AddressFamily.InterNetwork);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(announcement(), AppJsonContext.Default.Announcement);
            // Reply to the discovery listener, not the announcement's ephemeral source port.
            await _replySender.SendAsync(bytes, new IPEndPoint(address, port), ct);
        }
        finally { _sendGate.Release(); }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs args) => _interfacesChanged = true;

    // Network interfaces and UDP send sockets are reused between one-second heartbeats.
    // The network-change event invalidates the cache immediately after Wi-Fi/IP changes.
    private void RefreshInterfaces()
    {
        if (!_interfacesChanged && DateTimeOffset.UtcNow - _lastInterfaceRefresh < TimeSpan.FromSeconds(15)) return;
        _interfacesChanged = false;
        _lastInterfaceRefresh = DateTimeOffset.UtcNow;
        var addresses = InterfaceAddresses();
        foreach (var old in _senders.Keys.Where(x => !addresses.Contains(x)).ToArray())
        {
            _senders[old].Dispose(); _senders.Remove(old);
        }
        foreach (var added in addresses.Where(x => !_addresses.Contains(x)))
        {
            try { _receiver?.JoinMulticastGroup(Group, added); }
            catch (SocketException) { }
        }
        _addresses = addresses;
    }

    private static IPAddress[] InterfaceAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(x => x.OperationalStatus == OperationalStatus.Up)
        .SelectMany(x => x.GetIPProperties().UnicastAddresses)
        .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork)
        .Select(x => x.Address).Distinct().ToArray();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        await _stop.CancelAsync();
        if (_receiveTask is not null) await _receiveTask;
        if (_sendTask is not null) await _sendTask;
        // Serialize with explicit profile announcements: no online packet may follow goodbye.
        await _sendGate.WaitAsync();
        try
        {
            _leaving = true;
            if (_receiver is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                try { await SendAsync(announcement() with { IsOnline = false }, timeout.Token); }
                catch (OperationCanceledException) { }
            }
        }
        finally { _sendGate.Release(); }
        _receiver?.Dispose();
        foreach (var sender in _senders.Values) sender.Dispose();
        _senders.Clear();
        _replySender?.Dispose();
        _stop.Dispose();
    }
}
