using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LanChat.Core.Models;
using LanChat.Desktop.ViewModels;
using LanChat.Infrastructure.Mcp;
using LanChat.Infrastructure.Networking;
using LanChat.Infrastructure.Storage;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static async Task RunMcpTestsAsync()
    {
        var root = Path.Combine(TestRoot, "mcp");
        var storeA = new IndexedFileStore(Path.Combine(root, "a"));
        var storeB = new IndexedFileStore(Path.Combine(root, "b"));
        var a = await storeA.LoadProfileAsync();
        var b = await storeB.LoadProfileAsync(); b.Nickname = "MCP test recipient";
        Check(!a.McpEnabled && a.McpKey == "", "MCP is off by default");
        await using var sender = new LanService(storeA, a, new NodeOptions { ListenPort = 0, EnableDiscovery = false });
        await using var recipient = new LanService(storeB, b, new NodeOptions { ListenPort = 0, EnableDiscovery = false });
        await sender.StartAsync(); await recipient.StartAsync();
        var peer = await sender.ConnectAsync("127.0.0.1", recipient.Port);
        var received = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        recipient.MessageReceived += message => received.TrySetResult(message);
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var ui = new TestUiServices();
        await using var settings = new McpSettingsViewModel(new FeilvMcpServer(sender, port), a, storeA, ui);
        await settings.ToggleCommand.ExecuteAsync(null);
        Check(settings.IsRunning && a.McpEnabled && a.McpKey.Length == 64, "MCP starts with a random persistent key");
        var saved = XmlConfiguration.Load(storeA.DataDirectory).Profile;
        Check(saved.McpEnabled && saved.McpKey == a.McpKey, "MCP key and enabled state survive XML roundtrip");
        await settings.CopyConfigurationCommand.ExecuteAsync(null);
        using var json = JsonDocument.Parse(ui.CopiedText!);
        var entry = json.RootElement.GetProperty("mcpServers").GetProperty("feilv");
        Check(entry.GetProperty("type").GetString() == "http" && entry.GetProperty("url").GetString() == settings.Url && entry.GetProperty("headers").GetProperty("Authorization").GetString() == "Bearer " + a.McpKey,
            "copied MCP configuration contains the local endpoint and bearer key");
        using var http = new HttpClient();
        Check((await http.GetAsync(settings.Url)).StatusCode == HttpStatusCode.Unauthorized, "requests without the MCP key are denied");
        using var origin = new HttpRequestMessage(HttpMethod.Get, settings.Url);
        origin.Headers.Add("Origin", "https://example.com"); origin.Headers.Add("Authorization", "Bearer " + a.McpKey);
        Check((await http.SendAsync(origin)).StatusCode == HttpStatusCode.Forbidden, "browser cross-origin requests are denied");
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(settings.Url), AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + a.McpKey }
        });
        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();
        Check(tools.Any(x => x.Name == "list_devices") && tools.Any(x => x.Name == "send_file"), "official MCP client discovers the file tools over HTTP");
        var devices = await client.CallToolAsync("list_devices", new Dictionary<string, object?>());
        Check(devices.Content.OfType<TextContentBlock>().Any(x => x.Text.Contains(peer.Id) && x.Text.Contains(peer.Address)), "MCP lists the online recipient with ID and IP");
        var source = Path.Combine(root, "MCP test.txt"); await File.WriteAllTextAsync(source, "MCP file contents");
        var sent = await client.CallToolAsync("send_file", new Dictionary<string, object?> { ["deviceId"] = peer.Id, ["path"] = source });
        Check(sent.IsError != true, "MCP sends a file offer to the selected device");
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(message.FileName == "MCP test.txt" && message.LocalPath is null, "recipient receives a normal file offer");
        var peerA = await recipient.ConnectAsync("127.0.0.1", sender.Port);
        var downloaded = await recipient.DownloadAttachmentAsync(peerA, message);
        Check(await File.ReadAllTextAsync(downloaded) == "MCP file contents", "recipient accepts and downloads the file sent through MCP");
        var folder = Path.Combine(root, "MCP folder"); Directory.CreateDirectory(Path.Combine(folder, "nested"));
        await File.WriteAllTextAsync(Path.Combine(folder, "nested", "file.txt"), "folder contents");
        var folderReceived = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        recipient.MessageReceived += item => { if (item.IsFolder) folderReceived.TrySetResult(item); };
        var folderResult = await client.CallToolAsync("send_file", new Dictionary<string, object?> { ["deviceId"] = peer.Id, ["path"] = folder });
        var folderMessage = await folderReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var downloadedFolder = await recipient.DownloadAttachmentAsync(peerA, folderMessage);
        Check(folderResult.IsError != true && await File.ReadAllTextAsync(Path.Combine(downloadedFolder, "nested", "file.txt")) == "folder contents",
            "MCP recursively sends folders without an archive");
        var invalid = await client.CallToolAsync("send_file", new Dictionary<string, object?> { ["deviceId"] = peer.Id, ["path"] = "relative.txt" });
        Check(invalid.IsError == true, "MCP rejects relative local paths");
        await client.DisposeAsync();
        await settings.ToggleCommand.ExecuteAsync(null);
        Check(!settings.IsRunning && !XmlConfiguration.Load(storeA.DataDirectory).Profile.McpEnabled, "disabling MCP stops the server and persists the setting");
        await settings.ToggleCommand.ExecuteAsync(null);
        Check(settings.IsRunning && a.McpKey == saved.McpKey, "re-enabling MCP retains the copied key and address");
    }
}
