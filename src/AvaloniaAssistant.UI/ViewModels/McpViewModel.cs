using System.Collections.ObjectModel;
using Avalonia.Media;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Storage;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaAssistant.UI.ViewModels;

/// <summary>
/// MCP 服务端管理页。与技能页平级：技能管 SKILL.md，这里管 mcp.json，
/// 写入后清掉会话缓存，下一个会话就能连上新服务端。
/// 添加有两路：整段粘贴标准 JSON（主路径，多服务端一次进去），或者填三个框的表单。
/// </summary>
public sealed partial class McpViewModel : ObservableObject
{
    private const string ExampleJson = """
        {
          "mcpServers": {
            "filesystem": {
              "command": "npx",
              "args": ["-y", "@modelcontextprotocol/server-filesystem", "D:/work"]
            },
            "docs": {
              "url": "https://mcp.example.com/mcp",
              "headers": { "Authorization": "Bearer <token>" }
            }
          }
        }
        """;

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

    public string EmptyHint => "点右上角「导入 JSON」，把 mcp.json 的内容整段粘进来；"
                               + "写入后下一个会话启动时连接。只想加一个 stdio 服务端可以用「表单添加」。";

    /// <summary>导入面板开着的时候不再叠一个表单，两个都占地方。</summary>
    [ObservableProperty]
    private bool isImportOpen;

    [ObservableProperty]
    private string importJson = string.Empty;

    /// <summary>同名服务端是覆盖还是留着不动，默认留着。</summary>
    [ObservableProperty]
    private bool overwriteExisting;

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

    public bool NoticeIsError { get; private set; }

    /// <summary>提示条配色：导入成功是绿的，失败是红的。</summary>
    public IBrush NoticeBackground => Brush.Parse(NoticeIsError ? "#FBEAE8" : "#EAF4EA");

    public IBrush NoticeForeground => Brush.Parse(NoticeIsError ? "#B3261E" : "#2E7D32");

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

    // ---- 导入 JSON ----

    [RelayCommand]
    public void ShowImport()
    {
        IsImportOpen = true;
        IsFormOpen = false;
        Notice = null;
    }

    [RelayCommand]
    private void CancelImport()
    {
        IsImportOpen = false;
        ImportJson = string.Empty;
    }

    [RelayCommand]
    private void ToggleOverwrite() => OverwriteExisting = !OverwriteExisting;

    /// <summary>填一份能直接跑的样例，省得用户自己猜字段名。</summary>
    [RelayCommand]
    public void FillExample()
    {
        ImportJson = ExampleJson;
        Notice = null;
    }

    /// <summary>选一个 mcp.json 读进编辑框，确认后再导入——先看不写。</summary>
    [RelayCommand]
    private async Task ImportFileAsync()
    {
        var file = await _host.Picker.PickFileAsync("选择 mcp.json", "*.json");
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        try
        {
            ImportJson = await File.ReadAllTextAsync(file);
            IsImportOpen = true;
            IsFormOpen = false;
            ShowNotice($"已读入 {Ui.ShortenPath(file)}，确认后点「导入」。", isError: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice($"读不了这个文件：{ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    private void Import()
    {
        if (string.IsNullOrWhiteSpace(_configPathRaw))
        {
            ShowNotice("mcp.json 路径不可用，先选一个工作区。", isError: true);
            return;
        }

        try
        {
            var result = CapabilityInstaller.ImportMcpServers(_configPathRaw, ImportJson, OverwriteExisting);

            if (result.Written > 0)
            {
                _host.Runtime.InvalidateSessions();
                Refresh();
            }

            IsImportOpen = result.Written == 0 && result.Skipped.Count > 0;
            if (result.Written > 0)
            {
                ImportJson = string.Empty;
            }

            ShowNotice(DescribeImport(result), isError: result.Written == 0);
        }
        catch (Exception ex)
        {
            ShowNotice(ex.Message, isError: true);
        }
    }

    private static string DescribeImport(McpImportResult result)
    {
        var parts = new List<string>();
        if (result.Added.Count > 0)
        {
            parts.Add($"新增 {string.Join('、', result.Added)}");
        }

        if (result.Replaced.Count > 0)
        {
            parts.Add($"覆盖 {string.Join('、', result.Replaced)}");
        }

        if (result.Skipped.Count > 0)
        {
            parts.Add($"跳过同名的 {string.Join('、', result.Skipped)}（勾「覆盖同名」可替换）");
        }

        var summary = string.Join("，", parts) + "。";
        return result.Written > 0
            ? $"已写入 mcp.json：{summary}下一个会话会连接它们，工具以 mcp__<名称>__<工具> 出现。"
            : summary;
    }

    // ---- 表单 ----

    [RelayCommand]
    private void ShowForm()
    {
        IsFormOpen = true;
        IsImportOpen = false;
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
            ShowNotice("mcp.json 路径不可用，先选一个工作区。", isError: true);
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
            ShowNotice("已写入 mcp.json。下一个会话会连接它，工具以 mcp__<名称>__<工具> 出现。", isError: false);
        }
        catch (Exception ex)
        {
            ShowNotice(ex.Message, isError: true);
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
                ShowNotice($"已移除「{server.Name}」。", isError: false);
            }
        }
        catch (Exception ex)
        {
            ShowNotice(ex.Message, isError: true);
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

        ShowNotice("还没有 mcp.json，先用「导入 JSON」建一个。", isError: true);
    }

    private void ShowNotice(string message, bool isError)
    {
        NoticeIsError = isError;
        Notice = message;
        OnPropertyChanged(nameof(NoticeIsError));
        OnPropertyChanged(nameof(NoticeBackground));
        OnPropertyChanged(nameof(NoticeForeground));
    }
}
