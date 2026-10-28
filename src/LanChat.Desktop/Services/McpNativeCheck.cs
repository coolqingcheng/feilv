using System.Net;
using System.Net.Sockets;
using System.Text;
using LanChat.Core.Models;
using LanChat.Infrastructure.Mcp;
using LanChat.Infrastructure.Networking;
using LanChat.Infrastructure.Storage;

namespace LanChat.Desktop.Services;

internal static class McpNativeCheck
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "feilv-mcp-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new IndexedFileStore(directory);
            await using var lan = new LanService(store, store.InitialProfile, new NodeOptions { EnableDiscovery = false });
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            await using var server = new FeilvMcpServer(lan, port);
            const string key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            await server.StartAsync(key);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Url);
            request.Headers.Add("Authorization", "Bearer " + key);
            request.Headers.Add("Accept", "application/json, text/event-stream");
            request.Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}", Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode || !body.Contains("list_devices", StringComparison.Ordinal) || !body.Contains("send_file", StringComparison.Ordinal))
                throw new InvalidDataException("Native MCP self-test failed: " + response.StatusCode + " " + body);
            using var call = new HttpRequestMessage(HttpMethod.Post, server.Url);
            call.Headers.Add("Authorization", "Bearer " + key);
            call.Headers.Add("Accept", "application/json, text/event-stream");
            call.Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"list_devices\",\"arguments\":{}}}", Encoding.UTF8, "application/json");
            using var callResponse = await http.SendAsync(call);
            var result = await callResponse.Content.ReadAsStringAsync();
            if (!callResponse.IsSuccessStatusCode || !result.Contains("No online devices.", StringComparison.Ordinal))
                throw new InvalidDataException("Native MCP tool invocation failed: " + callResponse.StatusCode + " " + result);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
