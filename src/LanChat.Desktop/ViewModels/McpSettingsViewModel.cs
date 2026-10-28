using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanChat.Core.Models;
using LanChat.Core.Services;
using LanChat.Desktop.Services;
using LanChat.Infrastructure.Mcp;

namespace LanChat.Desktop.ViewModels;

public partial class McpSettingsViewModel(FeilvMcpServer server, UserProfile profile, ILocalStore store, IUiServices ui) : ViewModelBase, IAsyncDisposable
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanCopy)), NotifyPropertyChangedFor(nameof(ToggleLabel))]
    private bool _isRunning;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanCopy))] private bool _isBusy;
    [ObservableProperty] private string _status = "MCP 服务已关闭";
    public string Url => server.Url;
    public bool CanCopy => IsRunning && !IsBusy;
    public string ToggleLabel => IsRunning ? "关闭 MCP" : "启用 MCP";

    public async Task InitializeAsync()
    {
        if (!profile.McpEnabled) return;
        IsBusy = true;
        try
        {
            if (profile.McpKey.Length != 64 || !profile.McpKey.All(Uri.IsHexDigit)) throw new InvalidDataException("MCP 密钥无效，请重新启用。");
            await server.StartAsync(profile.McpKey);
            IsRunning = true; Status = "MCP 服务运行中";
        }
        catch (Exception ex) { Status = "MCP 启动失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        var previousEnabled = profile.McpEnabled;
        var previousKey = profile.McpKey;
        try
        {
            if (IsRunning)
            {
                profile.McpEnabled = false;
                await store.SaveProfileAsync(profile);
                await server.StopAsync();
                IsRunning = false; Status = "MCP 服务已关闭";
            }
            else
            {
                if (profile.McpKey.Length != 64 || !profile.McpKey.All(Uri.IsHexDigit))
                    profile.McpKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                await server.StartAsync(profile.McpKey);
                try { profile.McpEnabled = true; await store.SaveProfileAsync(profile); }
                catch { await server.StopAsync(); throw; }
                IsRunning = true; Status = "MCP 服务运行中";
            }
        }
        catch (Exception ex)
        {
            profile.McpEnabled = previousEnabled; profile.McpKey = previousKey;
            Status = "MCP 设置失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task CopyConfigurationAsync()
    {
        if (!CanCopy) return;
        var json = "{\n  \"mcpServers\": {\n    \"feilv\": {\n      \"type\": \"http\",\n      \"url\": \"" + Url + "\",\n      \"headers\": {\n        \"Authorization\": \"Bearer " + profile.McpKey + "\"\n      }\n    }\n  }\n}";
        try { await ui.CopyTextAsync(json); Status = "MCP 配置已复制"; }
        catch (Exception ex) { Status = "复制失败：" + ex.Message; }
    }

    public ValueTask DisposeAsync() => server.DisposeAsync();
}
