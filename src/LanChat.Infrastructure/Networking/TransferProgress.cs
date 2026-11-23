using System.Diagnostics;
using LanChat.Core.Models;

namespace LanChat.Infrastructure.Networking;

internal sealed class TransferProgress(Action<TransferUpdate> changed, string id, string name, long total, bool upload = false, string? attachmentId = null, string? peerId = null)
{
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private long _lastTicks;
    private long _lastBytes;

    public void Report(long completed, string status, bool force = false)
    {
        var ticks = _elapsed.ElapsedTicks;
        var delta = ticks - _lastTicks;
        if (!force && delta < Stopwatch.Frequency / 4) return;
        var speed = (completed - _lastBytes) * (double)Stopwatch.Frequency / Math.Max(1, delta);
        changed(status == "已完成" ? Completion(completed) : CreateUpdate(completed, status, speed));
        _lastTicks = ticks; _lastBytes = completed;
    }

    public TransferUpdate Completion(long completed) =>
        CreateUpdate(completed, "已完成", completed * (double)Stopwatch.Frequency / Math.Max(1, _elapsed.ElapsedTicks));

    private TransferUpdate CreateUpdate(long completed, string status, double speed) => new(id, name, completed, total, status)
    {
        BytesPerSecond = Math.Max(0, speed), IsUpload = upload, AttachmentId = attachmentId, PeerId = peerId
    };
}
