using System.ComponentModel;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using LanChat.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace LanChat.Infrastructure.Mcp;

public sealed class FeilvMcpServer(ILanService lan, int port = 45833) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WebApplication? _app;
    public int Port { get; } = port;
    public string Url => $"http://127.0.0.1:{Port}/mcp";
    public bool IsRunning => _app is not null;

    public async Task StartAsync(string key, CancellationToken ct = default)
    {
        if (key.Length != 64 || !key.All(Uri.IsHexDigit)) throw new ArgumentException("MCP key is invalid.");
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_app is not null) return;
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, Port);
                options.Limits.MaxRequestBodySize = 64 * 1024;
                options.Limits.MaxConcurrentConnections = 8;
            });
            builder.Services.AddSingleton(lan);
            builder.Services.AddSingleton<FeilvMcpTools>();
            builder.Services.AddMcpServer().WithHttpTransport().WithTools<FeilvMcpTools>();
            var app = builder.Build();
            var expected = Encoding.ASCII.GetBytes("Bearer " + key);
            app.Use(async (context, next) =>
            {
                var host = context.Request.Host;
                if (host.Port != Port || !(host.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) || host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                    || context.Request.Headers.ContainsKey("Origin"))
                { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
                var supplied = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
                if (!CryptographicOperations.FixedTimeEquals(supplied, expected))
                { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
                await next(context);
            });
            app.MapMcp("/mcp");
            try { await app.StartAsync(ct).ConfigureAwait(false); }
            catch { await app.DisposeAsync().ConfigureAwait(false); throw; }
            _app = app;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var app = _app; _app = null;
            if (app is null) return;
            try { await app.StopAsync().ConfigureAwait(false); }
            finally { await app.DisposeAsync().ConfigureAwait(false); }
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

[McpServerToolType]
public sealed class FeilvMcpTools(ILanService lan, IHostApplicationLifetime lifetime)
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    [McpServerTool(Name = "list_devices", ReadOnly = true), Description("List online Feilv devices with their IDs, names, IP addresses and versions.")]
    public string ListDevices()
    {
        var peers = lan.Peers.Where(p => p.IsOnline).OrderBy(p => p.Nickname, StringComparer.OrdinalIgnoreCase)
            .Select(p => $"ID: {p.Id} | Name: {p.Nickname} | IP: {p.Address} | Version: {p.AppVersion}").ToArray();
        return peers.Length == 0 ? "No online devices." : string.Join('\n', peers);
    }

    [McpServerTool(Name = "send_file"), Description("Send a local file or recursively send a local folder to one online Feilv device. This creates an offer; the recipient may accept or reject it.")]
    public async Task<string> SendFileAsync(
        [Description("Device ID returned by list_devices.")] string deviceId,
        [Description("Absolute path to a local file or folder on the computer running Feilv.")] string path,
        CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("The path must be absolute.");
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        await _sendGate.WaitAsync(stopping.Token).ConfigureAwait(false);
        try
        {
            var peer = lan.Peers.FirstOrDefault(p => p.Id == deviceId && p.IsOnline)
                ?? throw new InvalidOperationException("The device is offline or was not found.");
            path = Path.GetFullPath(path);
            var message = Directory.Exists(path)
                ? await lan.SendFolderAsync(peer, path, stopping.Token).ConfigureAwait(false)
                : await lan.SendFileAsync(peer, path, false, stopping.Token).ConfigureAwait(false);
            return $"File offer sent to {peer.Nickname} ({peer.Address}). Message ID: {message.Id}. The recipient may accept or reject it.";
        }
        finally { _sendGate.Release(); }
    }
}
