using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace LanChat.Desktop.Services;

/// <summary>One desktop client per login session, independent of its EXE path, data directory or port.</summary>
public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _activationGate = new();
    private Task? _listener;
    private Action? _activate;
    private bool _activationPending;
    private bool _ownsMutex;
    private bool _disposed;

    public SingleInstanceService(string instanceKey = "LanChat.Desktop.SingleInstance.v1")
    {
        var session = OperatingSystem.IsWindows() ? Process.GetCurrentProcess().SessionId.ToString() : "desktop";
        var scope = $"{instanceKey}/{Environment.UserDomainName}/{Environment.UserName}/{session}";
        var name = "LanChat-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..32];
        _pipeName = name;
        _mutex = new Mutex(false, OperatingSystem.IsWindows() ? @"Local\" + name : name);
    }

    // Acquire and dispose on the entry thread: named mutex ownership is thread-affine.
    public bool TryStart()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ownsMutex) return true;
        try { if (!_mutex.WaitOne(0)) return false; }
        catch (AbandonedMutexException) { /* The previous process exited unexpectedly; we now own the mutex. */ }
        _ownsMutex = true;
        try
        {
            // Create the endpoint before returning so a second launch can notify during UI startup.
            _listener = ListenAsync(CreateServer(), _stop.Token);
            return true;
        }
        catch
        {
            _mutex.ReleaseMutex(); _ownsMutex = false;
            throw;
        }
    }

    public void SetActivationHandler(Action activate)
    {
        bool pending;
        lock (_activationGate)
        {
            _activate = activate;
            pending = _activationPending;
            _activationPending = false;
        }
        if (pending) activate();
    }

    public async Task<bool> ActivateExistingAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var primaryId = new byte[4];
            await client.ReadExactlyAsync(primaryId, timeout.Token).ConfigureAwait(false);
            // The user-launched second process can let the existing Windows process take foreground focus.
            if (OperatingSystem.IsWindows()) _ = AllowSetForegroundWindow((uint)BinaryPrimitives.ReadInt32LittleEndian(primaryId));
            await client.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
            await client.FlushAsync(timeout.Token).ConfigureAwait(false);
            var acknowledgement = new byte[1];
            await client.ReadExactlyAsync(acknowledgement, timeout.Token).ConfigureAwait(false);
            return acknowledgement[0] == 1;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException) { return false; }
    }

    private NamedPipeServerStream CreateServer() => new(_pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ListenAsync(NamedPipeServerStream firstServer, CancellationToken ct)
    {
        var server = firstServer;
        while (true)
        {
            using (server)
            {
                try
                {
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    var primaryId = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(primaryId, Environment.ProcessId);
                    await server.WriteAsync(primaryId, requestTimeout.Token).ConfigureAwait(false);
                    await server.FlushAsync(requestTimeout.Token).ConfigureAwait(false);
                    var command = new byte[1];
                    await server.ReadExactlyAsync(command, requestTimeout.Token).ConfigureAwait(false);
                    if (command[0] == 1)
                    {
                        Action? activate;
                        lock (_activationGate)
                        {
                            activate = _activate;
                            if (activate is null) _activationPending = true;
                        }
                        activate?.Invoke();
                        await server.WriteAsync(new byte[] { 1 }, requestTimeout.Token).ConfigureAwait(false);
                        await server.FlushAsync(requestTimeout.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            }
            if (ct.IsCancellationRequested) break;
            server = CreateServer();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        try { _listener?.GetAwaiter().GetResult(); }
        finally
        {
            if (_ownsMutex) _mutex.ReleaseMutex();
            _mutex.Dispose(); _stop.Dispose();
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int AllowSetForegroundWindow(uint processId);
}
