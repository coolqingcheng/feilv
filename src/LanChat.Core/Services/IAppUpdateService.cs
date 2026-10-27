using LanChat.Core.Models;

namespace LanChat.Core.Services;

public interface IAppUpdateService : IDisposable
{
    Task<AppRelease?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default);
    Task<PreparedAppUpdate> DownloadAsync(AppRelease release, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}
