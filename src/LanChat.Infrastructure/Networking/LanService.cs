using System.Collections.Concurrent;
using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LanChat.Core.Models;
using LanChat.Core.Services;
using LanChat.Infrastructure.Files;
using LanChat.Infrastructure.Protocol;

namespace LanChat.Infrastructure.Networking;

public sealed class LanService(ILocalStore store, UserProfile profile, NodeOptions options) : ILanService
{
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, Peer> _peers = new();
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private readonly SemaphoreSlim _shareGate = new(1, 1);
    private readonly SemaphoreSlim _peerPersistGate = new(1, 1);
    private readonly SemaphoreSlim _connectionSlots = new(16, 16);
    private readonly SemaphoreSlim _outgoingTransferGate = new(1, 1);
    private readonly SemaphoreSlim _messageStateGate = new(1, 1);
    private readonly object _outgoingGate = new();
    private readonly HashSet<(string PeerId, string TransferId, CancellationTokenSource Cancellation)> _outgoing = [];
    private readonly List<SharedFolder> _shares = [];
    private readonly ConcurrentDictionary<string, DateTimeOffset> _profileRefresh = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _offlineProbes = new();
    private readonly object _presenceGate = new();
    private readonly Dictionary<string, long> _offlineRevisions = [];
    private readonly HashSet<string> _presenceProbes = [];
    private long _presenceRevision;
    private int _disposed;
    private TcpListener? _listener;
    private X509Certificate2? _certificate;
    private string _fingerprint = "";
    private DiscoveryService? _discovery;
    private Task? _acceptTask;
    private Task? _expiryTask;
    private int _connectionId;

    public UserProfile Profile { get; } = profile;
    public int Port { get; private set; }
    public IReadOnlyCollection<Peer> Peers => _peers.Values.ToArray();
    public IReadOnlyList<SharedFolder> SharedFolders { get { lock (_shares) return _shares.ToArray(); } }
    public event Action<Peer>? PeerChanged;
    public event Action<ChatMessage>? MessageReceived;
    public event Action<TransferUpdate>? TransferChanged;
    public event Action<string>? Error;

    private Announcement Self => new()
    {
        Id = Profile.Id,
        Nickname = Profile.Nickname,
        AppVersion = AppReleases.CurrentVersion,
        Port = Port,
        Fingerprint = _fingerprint
    };

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Certificate creation has no asynchronous API and may involve a Windows key container.
        _certificate = await Task.Run(LoadCertificate, cancellationToken).ConfigureAwait(false);
        _fingerprint = _certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var shares = await store.LoadSharesAsync(cancellationToken);
        lock (_shares) _shares.AddRange(shares);
        foreach (var saved in await store.LoadPeersAsync(cancellationToken))
        {
            var offline = saved with { IsOnline = false };
            _peers[offline.Id] = offline;
            PeerChanged?.Invoke(offline);
        }
        _listener = new TcpListener(IPAddress.Any, options.ListenPort);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = AcceptLoopAsync(_stop.Token);
        _expiryTask = ExpirePeersAsync(_stop.Token);
        if (options.EnableDiscovery)
        {
            _discovery = new DiscoveryService(options.DiscoveryPort, () => Self, DiscoveredAsync);
            try { _discovery.Start(); }
            catch (SocketException ex) { Error?.Invoke("自动发现未启动，可手动连接。" + ex.Message); }
        }
    }

    private X509Certificate2 LoadCertificate()
    {
        var path = Path.Combine(store.DataDirectory, "Data", "identity.pfx");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Windows 10 Schannel requires a user key container for TLS private keys.
        var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;
        if (File.Exists(path)) return X509CertificateLoader.LoadPkcs12FromFile(path, null, flags);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + Profile.Id, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var bytes = generated.Export(X509ContentType.Pfx);
        File.WriteAllBytes(path, bytes);
        return X509CertificateLoader.LoadPkcs12(bytes, null, flags);
    }

    private async Task DiscoveredAsync(Announcement sender, IPAddress address)
    {
        if (address.Equals(IPAddress.Any) || _stop.IsCancellationRequested) return;
        var ip = address.ToString();
        if (!sender.IsOnline)
        {
            lock (_presenceGate)
                if (_peers.TryGetValue(sender.Id, out var known) && known.Fingerprint == sender.Fingerprint && known.Address == ip)
                    MarkOfflineLocked(known);
            return;
        }
        long revision;
        lock (_presenceGate) revision = _presenceRevision;
        if (!_peers.TryGetValue(sender.Id, out var trusted) || trusted.Fingerprint != sender.Fingerprint)
            if (!await store.TrustIdentityAsync(sender.Id, sender.Fingerprint, _stop.Token)) return;
        lock (_presenceGate)
        {
            var peer = UpsertPeer(ToPeer(sender, ip), revision);
            if (!peer.IsOnline || _stop.IsCancellationRequested) return;
            if (!_profileRefresh.TryGetValue(peer.Id, out var refreshed) || DateTimeOffset.UtcNow - refreshed > TimeSpan.FromSeconds(30))
            {
                _profileRefresh[peer.Id] = DateTimeOffset.UtcNow;
                Track(RefreshPeerAsync(peer, _presenceRevision, _stop.Token));
            }
        }
    }

    private async Task RefreshPeerAsync(Peer peer, long revision, CancellationToken ct)
    {
        try { await ConnectKnownAsync(peer.Address, peer.Port, peer.Fingerprint, ct, revision, peer.Id); }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException) { }
    }

    public Task<Peer> ConnectAsync(string address, int port, CancellationToken cancellationToken = default) => ConnectKnownAsync(address, port, null, cancellationToken);

    private async Task<Peer> ConnectKnownAsync(string address, int port, string? fingerprint, CancellationToken ct, long? observedRevision = null, string? expectedId = null, bool syncAvatar = true)
    {
        long revision;
        lock (_presenceGate) revision = observedRevision ?? _presenceRevision;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(address, port, timeout.Token);
        using var stream = CreateClientStream(client, fingerprint);
        await AuthenticateClientAsync(stream, timeout.Token);
        await PacketIO.WriteAsync(stream, new WirePacket { Type = "hello", Sender = Self, IncludeAvatar = syncAvatar }, timeout.Token);
        var reply = await PacketIO.ReadAsync(stream, timeout.Token);
        ThrowIfError(reply);
        var sender = reply.Sender ?? throw new InvalidDataException("客户端身份缺失。");
        if (sender.Id == Profile.Id) throw new InvalidOperationException("不能连接到自己。");
        if (expectedId is not null && sender.Id != expectedId) throw new AuthenticationException("设备身份已改变。");
        await ValidateIdentityAsync(sender, stream, timeout.Token);
        var peer = ToPeer(sender, ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString());
        if (reply.Avatar is { Length: > 0 and <= 131072 })
        {
            var avatarPath = Path.Combine(AppPaths.DailyMedia(store.DataDirectory, true, "Avatars"), peer.Id + "-" + Convert.ToHexString(SHA256.HashData(reply.Avatar))[..12] + ".img");
            Directory.CreateDirectory(Path.GetDirectoryName(avatarPath)!);
            if (!File.Exists(avatarPath)) await File.WriteAllBytesAsync(avatarPath, reply.Avatar, timeout.Token);
            peer = peer with { AvatarPath = avatarPath };
        }
        return UpsertPeer(peer, revision, preserveAvatar: !syncAvatar);
    }

    private SslStream CreateClientStream(TcpClient client, string? fingerprint) => new(client.GetStream(), false, (_, cert, _, _) =>
        cert is not null && (fingerprint is null || cert.GetCertHashString(HashAlgorithmName.SHA256) == fingerprint));

    private Task AuthenticateClientAsync(SslStream stream, CancellationToken ct) => stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
    {
        TargetHost = "LanChat",
        ClientCertificates = new X509CertificateCollection { _certificate! },
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
    }, ct);

    private async Task ValidateIdentityAsync(Announcement sender, SslStream stream, CancellationToken ct)
    {
        var trusted = _peers.TryGetValue(sender.Id, out var known) && known.Fingerprint == sender.Fingerprint;
        if (sender.App != "LanChat" || sender.Version != 1 || !Guid.TryParseExact(sender.Id, "N", out _)
            || sender.Port is < 1 or > 65535 || sender.Nickname is not { Length: > 0 and <= 40 }
            || stream.RemoteCertificate?.GetCertHashString(HashAlgorithmName.SHA256) != sender.Fingerprint
            || (!trusted && !await store.TrustIdentityAsync(sender.Id, sender.Fingerprint, ct)))
            throw new AuthenticationException("客户端身份校验失败。");
    }

    private static Peer ToPeer(Announcement sender, string address) => new()
    {
        Id = sender.Id,
        Nickname = sender.Nickname,
        Address = address,
        Port = sender.Port,
        AppVersion = sender.AppVersion is { Length: > 0 and <= 32 } && Version.TryParse(sender.AppVersion, out var version) ? version.ToString() : "",
        Fingerprint = sender.Fingerprint
    };

    private Peer UpsertPeer(Peer peer, long observedRevision, bool preserveAvatar = true)
    {
        lock (_presenceGate)
        {
            _peers.TryGetValue(peer.Id, out var previous);
            if (_stop.IsCancellationRequested || _offlineRevisions.GetValueOrDefault(peer.Id) > observedRevision)
                return previous ?? peer with { IsOnline = false };
            if (preserveAvatar && peer.AvatarPath is null) peer = peer with { AvatarPath = previous?.AvatarPath };
            var changed = previous is null || previous.Nickname != peer.Nickname
                || previous.Address != peer.Address || previous.Port != peer.Port || previous.AvatarPath != peer.AvatarPath
                || previous.AppVersion != peer.AppVersion;
            _peers[peer.Id] = peer;
            if (changed || previous!.IsOnline != peer.IsOnline) PeerChanged?.Invoke(peer);
            if (changed) Track(PersistPeersAsync());
            return peer;
        }
    }

    private void MarkOfflineLocked(Peer expected)
    {
        if (!expected.IsOnline || !_peers.TryGetValue(expected.Id, out var current) || !ReferenceEquals(current, expected)) return;
        var offline = current with { IsOnline = false };
        _offlineRevisions[current.Id] = ++_presenceRevision;
        _peers[current.Id] = offline;
        _profileRefresh.TryRemove(current.Id, out _);
        _offlineProbes[current.Id] = DateTimeOffset.UtcNow;
        PeerChanged?.Invoke(offline);
    }

    private void TouchPeer(string id)
    {
        lock (_presenceGate)
            if (!_stop.IsCancellationRequested && _peers.TryGetValue(id, out var peer) && peer.IsOnline)
                _peers[id] = peer with { LastSeen = DateTimeOffset.UtcNow };
    }

    private async Task PersistPeersAsync()
    {
        await _peerPersistGate.WaitAsync();
        try { await store.SavePeersAsync(_peers.Values.ToList()); }
        finally { _peerPersistGate.Release(); }
    }

    private async Task<(TcpClient Client, SslStream Stream)> OpenAsync(Peer peer, CancellationToken ct)
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        SslStream? stream = null;
        try
        {
            await client.ConnectAsync(peer.Address, peer.Port, ct);
            stream = CreateClientStream(client, peer.Fingerprint);
            await AuthenticateClientAsync(stream, ct);
            return (client, stream);
        }
        catch { stream?.Dispose(); client.Dispose(); throw; }
    }

    private async Task<WirePacket> RequestAsync(Peer peer, WirePacket packet, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var connection = await OpenAsync(peer, timeout.Token);
        using var client = connection.Client;
        using var stream = connection.Stream;
        await PacketIO.WriteAsync(stream, packet with { Sender = Self }, timeout.Token);
        var response = await PacketIO.ReadAsync(stream, timeout.Token);
        ThrowIfError(response);
        return response;
    }

    public async Task<ChatMessage> SendTextAsync(Peer peer, string text, CancellationToken cancellationToken = default)
    {
        text = text.Trim();
        if (text.Length is < 1 or > 16000) throw new ArgumentException("文字长度应为 1–16000 个字符。");
        var message = new ChatMessage { PeerId = peer.Id, SenderId = Profile.Id, Text = text, Kind = MessageKind.Text };
        await RetryMessageAsync(peer, message, cancellationToken);
        return message;
    }

    public async Task<ChatMessage> SendFileAsync(Peer peer, string path, bool isImage, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("文件不存在。", path);
        if (isImage && info.Length > 20 * 1024 * 1024) throw new InvalidOperationException("图片不能超过 20 MB，可作为普通文件发送。");
        var id = Guid.NewGuid().ToString("N");
        var target = Path.Combine(AppPaths.DailyMedia(store.DataDirectory, isImage, "Sent"), id, Path.GetFileName(path));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var (size, hash) = await SnapshotFileAsync(path, target, cancellationToken).ConfigureAwait(false);
        var attachment = new Attachment(id, peer.Id, target, info.Name, size, hash);
        return await OfferAttachmentAsync(peer, attachment, isImage ? MessageKind.Image : MessageKind.File, cancellationToken);
    }

    public async Task<ChatMessage> SendFolderAsync(Peer peer, string path, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("文件夹不存在。");
        var name = new DirectoryInfo(path).Name;
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        if (string.IsNullOrWhiteSpace(name)) name = "文件夹";
        var id = Guid.NewGuid().ToString("N");
        var target = Path.Combine(AppPaths.DailyMedia(store.DataDirectory, false, "Sent"), id, name);
        TransferChanged?.Invoke(new TransferUpdate(id, name, 0, 0, "准备文件夹") { IsUpload = true, AttachmentId = id, PeerId = peer.Id });
        var snapshotComplete = false;
        try
        {
            var size = await FolderTransferProtocol.SnapshotAsync(path, target, cancellationToken).ConfigureAwait(false);
            snapshotComplete = true;
            return await OfferAttachmentAsync(peer, new Attachment(id, peer.Id, target, name, size, "", true), MessageKind.File, cancellationToken);
        }
        catch
        {
            if (!snapshotComplete && Directory.Exists(Path.GetDirectoryName(target))) Directory.Delete(Path.GetDirectoryName(target)!, true);
            TransferChanged?.Invoke(new TransferUpdate(id, name, 0, 0, "失败") { IsUpload = true, AttachmentId = id, PeerId = peer.Id });
            throw;
        }
    }

    private async Task<ChatMessage> OfferAttachmentAsync(Peer peer, Attachment attachment, MessageKind kind, CancellationToken cancellationToken)
    {
        var message = new ChatMessage
        {
            PeerId = peer.Id,
            SenderId = Profile.Id,
            Kind = kind,
            AttachmentId = attachment.Id,
            FileName = attachment.FileName,
            FileSize = attachment.Size,
            Sha256 = attachment.Sha256,
            IsFolder = attachment.IsFolder,
            LocalPath = attachment.Path
        };
        await store.SaveAttachmentAsync(attachment with { MessageId = message.Id }, cancellationToken);
        await RetryMessageAsync(peer, message, cancellationToken);
        return message;
    }

    private static async Task<(long Size, string Hash)> SnapshotFileAsync(string path, string target, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, 65536), ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read); size += read;
            }
            return (size, Convert.ToHexString(hash.GetHashAndReset()));
        }
        catch
        {
            if (File.Exists(target)) File.Delete(target);
            throw;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public async Task RetryMessageAsync(Peer peer, ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (message.Delivery == DeliveryState.Rejected) throw new InvalidOperationException("这个请求已被拒绝，请重新发送文件。");
        if (string.IsNullOrEmpty(message.PeerNickname)) message.PeerNickname = peer.Nickname;
        if (string.IsNullOrEmpty(message.PeerAddress)) message.PeerAddress = peer.Address;
        await _messageStateGate.WaitAsync(cancellationToken);
        try
        {
            if ((await store.FindMessageAsync(peer.Id, message.Id, cancellationToken))?.Delivery == DeliveryState.Rejected)
                throw new InvalidOperationException("这个请求已被拒绝，请重新发送文件。");
            message.Delivery = DeliveryState.Sending;
            await store.SaveMessageAsync(message, cancellationToken);
            MessageReceived?.Invoke(message);
        }
        finally { _messageStateGate.Release(); }
        try
        {
            await RequestAsync(peer, new WirePacket { Type = "message", Message = message with { LocalPath = null } }, cancellationToken);
        }
        catch
        {
            await _messageStateGate.WaitAsync(CancellationToken.None);
            try
            {
                message.Delivery = (await store.FindMessageAsync(peer.Id, message.Id))?.Delivery == DeliveryState.Rejected ? DeliveryState.Rejected : DeliveryState.Failed;
                await store.SaveMessageAsync(message, CancellationToken.None);
                MessageReceived?.Invoke(message);
            }
            finally { _messageStateGate.Release(); }
            throw;
        }
        await _messageStateGate.WaitAsync(cancellationToken);
        try
        {
            var stored = await store.FindMessageAsync(peer.Id, message.Id, cancellationToken);
            message.Delivery = stored?.Delivery == DeliveryState.Rejected ? DeliveryState.Rejected : DeliveryState.Sent;
            await store.SaveMessageAsync(message, cancellationToken);
            MessageReceived?.Invoke(message);
        }
        finally { _messageStateGate.Release(); }
    }

    public async Task RejectAttachmentAsync(Peer peer, ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (message.SenderId != peer.Id || message.PeerId != peer.Id || message.Kind == MessageKind.Text || message.AttachmentId is null || message.LocalPath is not null)
            throw new InvalidOperationException("这条记录不能拒绝。");
        var response = await RequestAsync(peer, new WirePacket { Type = "reject-file", MessageId = message.Id, AttachmentId = message.AttachmentId }, cancellationToken);
        if (response.Type != "ok") throw new InvalidDataException("对方未确认拒绝通知。");
        message.Delivery = DeliveryState.Rejected;
        await store.SaveMessageAsync(message, CancellationToken.None);
        MessageReceived?.Invoke(message);
    }

    private async Task ReceiveRejectionAsync(Peer peer, WirePacket packet, CancellationToken ct)
    {
        if (!Guid.TryParseExact(packet.MessageId, "N", out _) || !Guid.TryParseExact(packet.AttachmentId, "N", out _))
            throw new InvalidDataException("拒绝通知无效。");
        await _messageStateGate.WaitAsync(ct);
        try
        {
            var attachment = await store.FindAttachmentAsync(packet.AttachmentId!, ct);
            if (attachment is null || attachment.PeerId != peer.Id) throw new UnauthorizedAccessException("附件不属于此设备。");
            var message = await store.FindMessageAsync(peer.Id, packet.MessageId!, ct);
            if ((message is not null && (message.SenderId != Profile.Id || message.AttachmentId != attachment.Id))
                || (message is null && attachment.MessageId != packet.MessageId)) throw new InvalidDataException("拒绝通知与发送记录不匹配。");
            var changed = !attachment.IsRejected || message is not null && message.Delivery != DeliveryState.Rejected;
            if (!attachment.IsRejected) await store.SaveAttachmentAsync(attachment with { IsRejected = true }, ct);
            if (message is not null && message.Delivery != DeliveryState.Rejected)
            {
                message.Delivery = DeliveryState.Rejected;
                await store.SaveMessageAsync(message, ct);
                MessageReceived?.Invoke(message);
            }
            if (changed) TransferChanged?.Invoke(new TransferUpdate(packet.MessageId!, attachment.FileName, 0, attachment.Size, "对方已拒绝")
                { IsUpload = true, AttachmentId = attachment.Id, PeerId = peer.Id });
        }
        finally { _messageStateGate.Release(); }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _connectionSlots.WaitAsync(ct);
                TcpClient client;
                try { client = await _listener!.AcceptTcpClientAsync(ct); }
                catch { _connectionSlots.Release(); throw; }
                Track(HandleClientAsync(client, ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (SocketException) when (ct.IsCancellationRequested) { }
    }

    private void Track(Task task)
    {
        var id = Interlocked.Increment(ref _connectionId);
        _connections[id] = task;
        _ = task.ContinueWith(completed => { _connections.TryRemove(id, out _); _ = completed.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        long revision;
        lock (_presenceGate) revision = _presenceRevision;
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        using (var stream = new SslStream(client.GetStream(), false, (_, certificate, _, _) => certificate is not null))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, timeout.Token);
                var packet = await PacketIO.ReadAsync(stream, timeout.Token);
                var sender = packet.Sender ?? throw new InvalidDataException("客户端身份缺失。");
                await ValidateIdentityAsync(sender, stream, timeout.Token);
                if (sender.Id == Profile.Id) throw new InvalidDataException("不能与自己通信。");
                var peer = ToPeer(sender, ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString());
                if (_peers.TryGetValue(peer.Id, out var previous)) peer = peer with { AvatarPath = previous.AvatarPath };
                UpsertPeer(peer, revision);
                switch (packet.Type)
                {
                    case "hello":
                        byte[]? avatar = null;
                        if (packet.IncludeAvatar && Profile.AvatarPath is { } avatarPath && File.Exists(avatarPath) && new FileInfo(avatarPath).Length <= 131072)
                            avatar = await File.ReadAllBytesAsync(avatarPath, timeout.Token);
                        await PacketIO.WriteAsync(stream, new WirePacket { Type = "ok", Sender = Self, Avatar = avatar }, timeout.Token);
                        break;
                    case "message":
                        await ReceiveMessageAsync(peer, packet, timeout.Token);
                        await PacketIO.WriteAsync(stream, new WirePacket { Type = "ok" }, timeout.Token);
                        break;
                    case "reject-file":
                        await ReceiveRejectionAsync(peer, packet, timeout.Token);
                        await PacketIO.WriteAsync(stream, new WirePacket { Type = "ok" }, timeout.Token);
                        break;
                    case "browse":
                        List<SharedEntry> entries = packet.RootId is null
                            ? SharedFolders.Select(x => new SharedEntry(x.Id, "", x.Name, true, 0)).ToList()
                            : SharedFileAccess.Browse(FindShare(packet.RootId), packet.RelativePath);
                        await PacketIO.WriteAsync(stream, new WirePacket { Type = "entries", Entries = entries }, timeout.Token);
                        break;
                    case "attachment":
                        var attachment = await store.FindAttachmentAsync(packet.AttachmentId ?? "", timeout.Token);
                        if (attachment is null || attachment.PeerId != peer.Id || attachment.IsRejected)
                            throw new UnauthorizedAccessException("附件不存在或不属于此会话。");
                        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                        await WithOutgoingCancellationAsync(peer.Id, attachment.Id, stream, timeout.Token,
                            token => SendAttachmentStreamAsync(stream, attachment, token, peer));
                        break;
                    case "shared-file":
                        var path = SharedFileAccess.Resolve(FindShare(packet.RootId ?? ""), packet.RelativePath);
                        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                        var transferId = Guid.NewGuid().ToString("N");
                        await WithOutgoingCancellationAsync(peer.Id, transferId, stream, timeout.Token, async token =>
                        {
                            await _outgoingTransferGate.WaitAsync(token);
                            try { await SendFileStreamAsync(stream, path, null, token, peer, transferId); }
                            finally { _outgoingTransferGate.Release(); }
                        });
                        break;
                    default: throw new InvalidDataException("不支持的请求。");
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                if (ex is AuthenticationException) Error?.Invoke("TLS 握手失败：" + ex.Message);
                if (stream.IsAuthenticated && !timeout.IsCancellationRequested)
                {
                    try { await PacketIO.WriteAsync(stream, new WirePacket { Type = "error", Error = FriendlyError(ex) }, timeout.Token); }
                    catch (Exception sendError) when (sendError is IOException or OperationCanceledException) { }
                }
            }
            finally { _connectionSlots.Release(); }
        }
    }

    public bool CancelOutgoingTransfer(string peerId, string transferId)
    {
        lock (_outgoingGate)
        {
            var matches = _outgoing.Where(x => x.PeerId == peerId && x.TransferId == transferId).ToArray();
            foreach (var item in matches) item.Cancellation.Cancel();
            return matches.Length > 0;
        }
    }

    private async Task WithOutgoingCancellationAsync(string peerId, string transferId, SslStream stream, CancellationToken ct, Func<CancellationToken, Task> transfer)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var entry = (peerId, transferId, cancellation);
        lock (_outgoingGate) _outgoing.Add(entry);
        var monitor = MonitorReceiverAsync();
        try { await transfer(cancellation.Token); }
        finally
        {
            await monitorStop.CancelAsync();
            await monitor;
            lock (_outgoingGate) _outgoing.Remove(entry);
        }

        async Task MonitorReceiverAsync()
        {
            try
            {
                // Transfer requests have no further client packets. One concurrent TLS read observes
                // disconnects even while this transfer is hashing or waiting for the send gate.
                var buffer = new byte[1];
                _ = await stream.ReadAsync(buffer, monitorStop.Token);
                if (!monitorStop.IsCancellationRequested) await cancellation.CancelAsync();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                if (!monitorStop.IsCancellationRequested) await cancellation.CancelAsync();
            }
        }
    }

    private async Task ReceiveMessageAsync(Peer peer, WirePacket packet, CancellationToken ct)
    {
        var incoming = packet.Message ?? throw new InvalidDataException("消息为空。");
        if (!Guid.TryParseExact(incoming.Id, "N", out _) || incoming.SenderId != peer.Id || incoming.PeerId != Profile.Id
            || incoming.Text.Length > 16000 || !Enum.IsDefined(incoming.Kind) || incoming.FileSize < 0
            || (incoming.IsFolder && incoming.Kind != MessageKind.File)
            || (incoming.Kind != MessageKind.Text && (!Guid.TryParseExact(incoming.AttachmentId, "N", out _) || (!incoming.IsFolder && incoming.Sha256?.Length != 64))))
            throw new InvalidDataException("消息格式不正确。");
        var message = incoming with { PeerId = peer.Id, LocalPath = null, Delivery = DeliveryState.Received,
            PeerNickname = peer.Nickname, PeerAddress = peer.Address, ReceivedAt = DateTimeOffset.UtcNow };
        // The repository deduplicates atomically without loading all history into RAM.
        if (!await store.TrySaveReceivedMessageAsync(message, ct)) return;
        MessageReceived?.Invoke(message);
    }

    private async Task SendAttachmentStreamAsync(Stream stream, Attachment attachment, CancellationToken ct, Peer peer)
    {
        void Changed(TransferUpdate update) { TouchPeer(peer.Id); TransferChanged?.Invoke(update); }
        var acquired = false;
        try
        {
            if (_outgoingTransferGate.CurrentCount == 0)
                Changed(new TransferUpdate(attachment.Id, attachment.FileName, 0, attachment.Size, "排队等待") { IsUpload = true, AttachmentId = attachment.Id, PeerId = peer.Id });
            await _outgoingTransferGate.WaitAsync(ct); acquired = true;
            if (!attachment.IsFolder) { await SendFileStreamAsync(stream, attachment.Path, attachment.Sha256, ct, peer, attachment.Id); return; }
            var progress = new TransferProgress(Changed, attachment.Id, attachment.FileName, attachment.Size, true, attachment.Id, peer.Id);
            progress.Report(0, "发送中", force: true);
            await FolderTransferProtocol.SendAsync(stream, attachment, total => progress.Report(total, "发送中"), ct);
            progress.Report(attachment.Size, "已完成", force: true);
        }
        catch (Exception ex)
        {
            Changed(new TransferUpdate(attachment.Id, attachment.FileName, 0, attachment.Size, ex is OperationCanceledException ? "已取消" : "失败") { IsUpload = true, AttachmentId = attachment.Id, PeerId = peer.Id });
            throw;
        }
        finally { if (acquired) _outgoingTransferGate.Release(); }
    }

    private async Task SendFileStreamAsync(Stream stream, string path, string? expectedHash, CancellationToken ct, Peer peer, string? attachmentId = null)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var name = Path.GetFileName(path);
        var id = attachmentId ?? Guid.NewGuid().ToString("N");
        void Changed(TransferUpdate update) { TouchPeer(peer.Id); TransferChanged?.Invoke(update); }
        Changed(new TransferUpdate(id, name, 0, file.Length, "准备发送") { IsUpload = true, AttachmentId = attachmentId, PeerId = peer.Id });
        TransferProgress? progress = null;
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        long completed = 0;
        try
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            if (expectedHash is not null && expectedHash != hash) throw new IOException("附件内容已变化，请重新发送。");
            file.Position = 0;
            await PacketIO.WriteAsync(stream, new WirePacket { Type = "file", FileName = name, FileSize = file.Length, Sha256 = hash }, ct);
            progress = new TransferProgress(Changed, id, name, file.Length, true, attachmentId, peer.Id);
            int read;
            while ((read = await file.ReadAsync(buffer.AsMemory(0, 65536), ct).ConfigureAwait(false)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                completed += read; progress?.Report(completed, "发送中");
            }
            await stream.FlushAsync(ct).ConfigureAwait(false);
            progress?.Report(completed, "已完成", force: true);
        }
        catch (Exception ex)
        {
            Changed(new TransferUpdate(id, name, completed, file.Length, ex is OperationCanceledException ? "已取消" : "失败") { IsUpload = true, AttachmentId = attachmentId, PeerId = peer.Id });
            throw;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public async Task<string> DownloadAttachmentAsync(Peer peer, ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (message.Delivery == DeliveryState.Rejected) throw new InvalidOperationException("这个文件已拒绝接收，请让对方重新发送。");
        var path = await DownloadAsync(peer, new WirePacket { Type = "attachment", AttachmentId = message.AttachmentId },
            message.Id, message.FileName ?? "file", message.Sha256, message.FileSize, message.Kind == MessageKind.Image, cancellationToken, message.IsFolder);
        message.LocalPath = path;
        await store.SaveMessageAsync(message, cancellationToken);
        MessageReceived?.Invoke(message);
        return path;
    }

    public async Task<IReadOnlyList<SharedEntry>> BrowseAsync(Peer peer, string? rootId = null, string relativePath = "", CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(peer, new WirePacket { Type = "browse", RootId = rootId, RelativePath = relativePath }, cancellationToken);
        return reply.Entries ?? [];
    }

    public async Task<string> DownloadSharedFileAsync(Peer peer, SharedEntry entry, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var path = await DownloadAsync(peer, new WirePacket { Type = "shared-file", RootId = entry.RootId, RelativePath = entry.RelativePath },
            id, entry.Name, null, null, false, cancellationToken);
        var message = new ChatMessage { Id = id, PeerId = peer.Id, SenderId = peer.Id, Kind = MessageKind.File,
            FileName = entry.Name, FileSize = new FileInfo(path).Length, LocalPath = path, Delivery = DeliveryState.Received,
            PeerNickname = peer.Nickname, PeerAddress = peer.Address, ReceivedAt = DateTimeOffset.UtcNow };
        await store.SaveMessageAsync(message, cancellationToken);
        MessageReceived?.Invoke(message);
        return path;
    }

    private async Task<string> DownloadAsync(Peer peer, WirePacket request, string id, string name, string? expectedHash, long? expectedSize, bool image, CancellationToken ct, bool isFolder = false)
    {
        var directory = AppPaths.DailyMedia(store.DataDirectory, image, "Received");
        Directory.CreateDirectory(directory);
        // Remote names are display-only: every download owns an isolated directory.
        var safeName = Path.GetFileName(name.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(safeName) || safeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || safeName is "." or "..") safeName = "download";
        var folder = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, safeName);
        var partial = target + ".partial";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        TransferChanged?.Invoke(new TransferUpdate(id, safeName, 0, expectedSize ?? 0, "连接中") { PeerId = peer.Id });
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            (TcpClient Client, SslStream Stream) connection;
            try { connection = await OpenAsync(peer, connectTimeout.Token); }
            catch (OperationCanceledException ex) when (!timeout.IsCancellationRequested) { throw new IOException("连接超时。", ex); }
            connectTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
            using var client = connection.Client;
            using var stream = connection.Stream;
            await PacketIO.WriteAsync(stream, request with { Sender = Self }, timeout.Token);
            var response = await PacketIO.ReadAsync(stream, timeout.Token);
            ThrowIfError(response);
            if (isFolder)
            {
                if (response.Type != "folder" || response.FileSize < 0 || response.FileSize != expectedSize)
                    throw new InvalidDataException("文件夹元数据校验失败。");
                var folderProgress = new TransferProgress(update => { TouchPeer(peer.Id); TransferChanged?.Invoke(update); }, id, safeName, response.FileSize, peerId: peer.Id);
                await FolderTransferProtocol.ReceiveAsync(stream, partial, response.FileSize, total => folderProgress.Report(total, "下载中"), timeout.Token);
                Directory.Move(partial, target);
                folderProgress.Report(response.FileSize, "已完成", force: true);
                return target;
            }
            if (response.Type != "file" || response.FileSize < 0 || response.Sha256?.Length != 64
                || (expectedHash is not null && expectedHash != response.Sha256)
                || (expectedSize.HasValue && expectedSize != response.FileSize)
                || (image && response.FileSize > 20 * 1024 * 1024)) throw new InvalidDataException("文件元数据校验失败。");
            long total = 0;
            var progress = new TransferProgress(update => { TouchPeer(peer.Id); TransferChanged?.Invoke(update); }, id, safeName, response.FileSize, peerId: peer.Id);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536];
                while (total < response.FileSize)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, response.FileSize - total)), timeout.Token);
                    if (read == 0) throw new EndOfStreamException("文件传输中断。");
                    await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    hash.AppendData(buffer, 0, read);
                    total += read;
                    progress.Report(total, "下载中");
                }
            }
            if (Convert.ToHexString(hash.GetHashAndReset()) != response.Sha256) throw new InvalidDataException("文件 SHA-256 校验失败。");
            File.Move(partial, target);
            progress.Report(total, "已完成", force: true);
            return target;
        }
        catch (Exception ex)
        {
            if (File.Exists(partial)) File.Delete(partial);
            if (Directory.Exists(partial)) Directory.Delete(partial, true);
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            TransferChanged?.Invoke(new TransferUpdate(id, safeName, 0, expectedSize ?? 0, ex is OperationCanceledException ? "已取消" : "失败") { PeerId = peer.Id });
            throw;
        }
    }

    private SharedFolder FindShare(string id) => SharedFolders.FirstOrDefault(x => x.Id == id) ?? throw new UnauthorizedAccessException("共享文件夹已被移除。");

    public async Task AddSharedFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("文件夹不存在。");
        var folder = new SharedFolder(Guid.NewGuid().ToString("N"), new DirectoryInfo(path).Name, path);
        _ = SharedFileAccess.Resolve(folder, "");
        await _shareGate.WaitAsync(cancellationToken);
        try
        {
            if (SharedFolders.Any(x => string.Equals(x.LocalPath, path, StringComparison.OrdinalIgnoreCase))) return;
            lock (_shares) _shares.Add(folder);
            await store.SaveSharesAsync(SharedFolders, cancellationToken);
        }
        finally { _shareGate.Release(); }
    }

    public async Task RemoveSharedFolderAsync(string id, CancellationToken cancellationToken = default)
    {
        await _shareGate.WaitAsync(cancellationToken);
        try
        {
            lock (_shares) _shares.RemoveAll(x => x.Id == id);
            await store.SaveSharesAsync(SharedFolders, cancellationToken);
        }
        finally { _shareGate.Release(); }
    }

    public async Task UpdateProfileAsync(CancellationToken cancellationToken = default)
    {
        Profile.Nickname = Profile.Nickname.Trim();
        if (Profile.Nickname.Length is < 1 or > 40) throw new ArgumentException("昵称应为 1–40 个字符。");
        await store.SaveProfileAsync(Profile, cancellationToken);
        if (_discovery is not null) await _discovery.AnnounceAsync(cancellationToken);
    }

    private async Task ExpirePeersAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_peers.IsEmpty) continue;
                lock (_presenceGate)
                {
                    var now = DateTimeOffset.UtcNow;
                    // Active peers take priority. Slow offline hosts cannot block the timer or
                    // consume an unbounded number of sockets and cancellation sources.
                    foreach (var pair in _peers)
                    {
                        var peer = pair.Value;
                        if (_presenceProbes.Count >= 8) break;
                        if (peer.IsOnline && now - peer.LastSeen > TimeSpan.FromSeconds(3) && _presenceProbes.Add(peer.Id))
                            Track(ProbePeerAsync(peer, _presenceRevision, ct));
                    }
                    var offlineBudget = 2;
                    foreach (var pair in _peers)
                    {
                        if (_presenceProbes.Count >= 8 || offlineBudget == 0) break;
                        var peer = pair.Value;
                        if (peer.IsOnline || (_offlineProbes.TryGetValue(peer.Id, out var lastProbe) && now - lastProbe < TimeSpan.FromSeconds(5))
                            || !_presenceProbes.Add(peer.Id)) continue;
                        _offlineProbes[peer.Id] = now; offlineBudget--;
                        Track(ProbePeerAsync(peer, _presenceRevision, ct));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task ProbePeerAsync(Peer expected, long revision, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(750));
        try { await ConnectKnownAsync(expected.Address, expected.Port, expected.Fingerprint, timeout.Token, revision, expected.Id, syncAvatar: false); }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException)
        {
            if (!ct.IsCancellationRequested) lock (_presenceGate) MarkOfflineLocked(expected);
        }
        finally { lock (_presenceGate) _presenceProbes.Remove(expected.Id); }
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "没有权限访问此文件或目录。",
        FileNotFoundException => "文件已被移动或删除。",
        DirectoryNotFoundException => "共享文件夹已被移动或删除。",
        _ => "请求失败，请刷新后重试。"
    };

    private static void ThrowIfError(WirePacket packet)
    {
        if (packet.Type == "error") throw new IOException(packet.Error ?? "远端请求失败。");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync();
        _listener?.Stop();
        if (_discovery is not null) await _discovery.DisposeAsync();
        if (_acceptTask is not null) await _acceptTask;
        if (_expiryTask is not null) await _expiryTask;
        await Task.WhenAll(_connections.Values);
        _certificate?.Dispose();
        _stop.Dispose();
    }
}
