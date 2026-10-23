using System.Collections.ObjectModel;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Storage;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaAssistant.UI.ViewModels;

/// <summary>
/// MCP 服务端管理页。与技能页平级：技能管 SKILL.md，这里管 mcp.json，
/// 写入后清掉会话缓存，下一个会话就能连上新服务端。
/// </summary>
public sealed partial class McpViewModel : ObservableObject
{
    private readonly MainWindowViewModel _host;
    private string _configPathRaw = string.Empty;

    public McpViewModel(MainWindowViewModel host)
    {
        _host = host;
        Refresh();
    }

    public ObservableCollection<McpServerInfo> Servers { get; } = [];

    public bool HasServers => Servers.Count > 0;

    public string CountText => $"{Servers.Count} 个服务端";

    /// <summary>配置文件路径，用户目录缩成 ~。</summary>
    public string ConfigPathText => Ui.ShortenPath(_configPathRaw);

    public string EmptyHint => "还没有 MCP 服务端。点右上角「添加服务端」填名称和启动命令，"
                               + "写入后下一个会话启动时连接。";

    [ObservableProperty]
    private bool isFormOpen;

    [ObservableProperty]
    private string name = string.Empty;

    [ObservableProperty]
    private string command = "npx";

    [ObservableProperty]
    private string args = string.Empty;

    [ObservableProperty]
    private string? notice;

    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);

    partial void OnNoticeChanged(string? value) => OnPropertyChanged(nameof(HasNotice));

    [RelayCommand]
    private void Back() => _host.OpenHome();

    [RelayCommand]
    private void Refresh()
    {
        var capabilities = _host.Runtime.Capabilities;
        _configPathRaw = capabilities.McpConfigPath;

        Servers.Clear();
        foreach (var server in capabilities.McpServers)
        {
            Servers.Add(server);
        }

        OnPropertyChanged(nameof(HasServers));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ConfigPathText));
    }

    [RelayCommand]
    private void ShowForm()
    {
        IsFormOpen = true;
        Notice = null;
    }

    [RelayCommand]
    private void CancelForm()
    {
        IsFormOpen = false;
        Name = string.Empty;
        Args = string.Empty;
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_configPathRaw))
        {
            return;
        }

        try
        {
            CapabilityInstaller.AddMcpServer(_configPathRaw, Name, Command, Args);
            _host.Runtime.InvalidateSessions();
            Refresh();
            IsFormOpen = false;
            Name = string.Empty;
            Args = string.Empty;
            Notice = "已写入 mcp.json。下一个会话会连接它，工具以 mcp__<名称>__<工具> 出现。";
        }
        catch (Exception ex)
        {
            Notice = ex.Message;
        }
    }

    [RelayCommand]
    private void Remove(McpServerInfo? server)
    {
        if (server is null || string.IsNullOrWhiteSpace(_configPathRaw))
        {
            return;
        }

        try
        {
            if (CapabilityInstaller.RemoveMcpServer(_configPathRaw, server.Name))
            {
                _host.Runtime.InvalidateSessions();
                Refresh();
                Notice = $"已移除「{server.Name}」。";
            }
        }
        catch (Exception ex)
        {
            Notice = ex.Message;
        }
    }

    [RelayCommand]
    private void OpenConfig()
    {
        if (!string.IsNullOrWhiteSpace(_configPathRaw) && File.Exists(_configPathRaw))
        {
            Ui.OpenExternal(_configPathRaw);
            return;
        }

        Notice = "还没有 mcp.json，先用「添加服务端」建一个。";
    }
}
