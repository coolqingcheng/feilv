using LanChat.Core.Models;

namespace LanChat.Core.Services;

public interface IQrHttpServer : IDisposable, IAsyncDisposable
{
    int Port { get; }
    string BasePath { get; }
    Task StartAsync(string uploadDirectory, string downloadDirectory, CancellationToken cancellationToken = default);
    Task StopAsync();
}

public interface ILocalStore
{
    string DataDirectory { get; }
    Task<UserProfile> LoadProfileAsync(CancellationToken cancellationToken = default);
    Task SaveProfileAsync(UserProfile profile, CancellationToken cancellationToken = default);
    Task<List<Peer>> LoadPeersAsync(CancellationToken cancellationToken = default);
    Task SavePeersAsync(IReadOnlyList<Peer> peers, CancellationToken cancellationToken = default);
    Task<List<ChatMessage>> LoadMessagesAsync(CancellationToken cancellationToken = default);
    Task<FileHistoryPage> LoadFileHistoryAsync(string search = "", FileHistoryCursor? before = null, int pageSize = 50, CancellationToken cancellationToken = default);
    Task SaveMessageAsync(ChatMessage message, CancellationToken cancellationToken = default);
    Task DeleteMessageAsync(string peerId, string id, CancellationToken cancellationToken = default);
    Task<bool> TrySaveReceivedMessageAsync(ChatMessage message, CancellationToken cancellationToken = default);
    Task<List<SharedFolder>> LoadSharesAsync(CancellationToken cancellationToken = default);
    Task SaveSharesAsync(IReadOnlyList<SharedFolder> shares, CancellationToken cancellationToken = default);
    Task<List<EmojiItem>> LoadEmojisAsync(CancellationToken cancellationToken = default);
    Task SaveEmojisAsync(IReadOnlyList<EmojiItem> emojis, CancellationToken cancellationToken = default);
    Task<List<Attachment>> LoadAttachmentsAsync(CancellationToken cancellationToken = default);
    Task SaveAttachmentsAsync(IReadOnlyList<Attachment> attachments, CancellationToken cancellationToken = default);
    Task<Attachment?> FindAttachmentAsync(string id, CancellationToken cancellationToken = default);
    Task SaveAttachmentAsync(Attachment attachment, CancellationToken cancellationToken = default);
    Task<bool> TrustIdentityAsync(string id, string fingerprint, CancellationToken cancellationToken = default);
}

public interface ILanService : IAsyncDisposable
{
    UserProfile Profile { get; }
    int Port { get; }
    IReadOnlyCollection<Peer> Peers { get; }
    IReadOnlyList<SharedFolder> SharedFolders { get; }
    event Action<Peer>? PeerChanged;
    event Action<ChatMessage>? MessageReceived;
    event Action<TransferUpdate>? TransferChanged;
    event Action<string>? Error;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task<Peer> ConnectAsync(string address, int port, CancellationToken cancellationToken = default);
    Task<ChatMessage> SendTextAsync(Peer peer, string text, CancellationToken cancellationToken = default);
    Task<ChatMessage> SendFileAsync(Peer peer, string path, bool isImage, CancellationToken cancellationToken = default);
    Task<ChatMessage> SendFolderAsync(Peer peer, string path, CancellationToken cancellationToken = default);
    Task RetryMessageAsync(Peer peer, ChatMessage message, CancellationToken cancellationToken = default);
    Task<string> DownloadAttachmentAsync(Peer peer, ChatMessage message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SharedEntry>> BrowseAsync(Peer peer, string? rootId = null, string relativePath = "", CancellationToken cancellationToken = default);
    Task<string> DownloadSharedFileAsync(Peer peer, SharedEntry entry, CancellationToken cancellationToken = default);
    Task AddSharedFolderAsync(string path, CancellationToken cancellationToken = default);
    Task RemoveSharedFolderAsync(string id, CancellationToken cancellationToken = default);
    Task UpdateProfileAsync(CancellationToken cancellationToken = default);
}
