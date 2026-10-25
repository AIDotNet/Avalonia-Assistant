using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Storage;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaAssistant.UI.ViewModels;

/// <summary>
/// MCP 卡片 + 这一次真实握手的结果。配置本身还是 <see cref="McpServerInfo"/>，
/// 这里只多带三样东西：检测中 / 连上了没有 / 失败原因。
/// </summary>
public sealed partial class McpServerCardViewModel : ObservableObject
{
    public McpServerCardViewModel(McpServerInfo info)
    {
        Name = info.Name;
        Target = info.Target;
        IsProjectScope = info.IsProjectScope;
    }

    public string Name { get; }

    public string Target { get; }

    public bool IsProjectScope { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(ShowRetry))]
    private bool isChecking;

    /// <summary>null = 还没探测过。不要默认成「连接失败」，那是在冤枉它。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    private bool? connected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? detail;

    public string StatusText => IsChecking
        ? "检测中…"
        : Detail is null
            ? "未检测"
            : Connected == true
                ? Detail
                : "连接失败：" + Shorten(Detail);

    /// <summary>两列卡片、两行文字：截到四十字左右，剩下的留给悬停。</summary>
    private static string Shorten(string text)
        => text.Length <= 40 ? text : text[..40] + "…";

    public IBrush StatusBrush => IsChecking || Connected is null
        ? Brush.Parse("#8A8A82")
        : Connected == true
            ? Brush.Parse("#2E7D32")
            : Brush.Parse("#B3261E");

    public bool ShowRetry => !IsChecking;

    /// <summary>删除是不可逆的：先就地换成确认态。一点垃圾桶就把整段 env / headers 抹掉太便宜了。</summary>
    [ObservableProperty]
    private bool isConfirmingDelete;

    [RelayCommand]
    private void StartDelete() => IsConfirmingDelete = true;

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    public void Apply(McpProbeResult result)
    {
        Connected = result.Ok;
        Detail = result.Ok
            ? result.ToolCount == 0 ? "已连接，但没有暴露工具（检查 exposure）" : $"已连接 · {result.ToolCount} 个工具"
            : result.Detail;
    }
}

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

    /// <summary>编辑态：非空表示表单是在改这一条，而不是新增。</summary>
    private string? _editingName;

    /// <summary>刚删掉的那条配置（原文），用来给「撤销」兜 10 秒。</summary>
    /// <summary>撤销条目：这一条自己的原文，加上删除前后的整份文件文本（撤销要原样写回去）。</summary>
    private (string Name, string Json, byte[] OriginalBytes, byte[] AfterBytes)? _undoEntry;

    private DispatcherTimer? _undoTimer;

    /// <summary>表单标题和保存按钮：编辑和新增要说清自己在干什么。</summary>
    public string FormTitle => _editingName is null ? "添加 MCP 服务端（stdio）" : $"编辑「{_editingName}」";

    public string SaveLabel => _editingName is null ? "保存" : "保存修改";

    [ObservableProperty]
    private bool canUndo;

    public string UndoText => _undoEntry is { } entry ? $"已移除「{entry.Name}」" : string.Empty;

    public McpViewModel(MainWindowViewModel host)
    {
        _host = host;
        Refresh();
    }

    public ObservableCollection<McpServerCardViewModel> Servers { get; } = [];

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

    /// <summary>有来源任务时写清返回哪里：从对话页来查 MCP，返回不该落在首页。</summary>
    public string BackHint => _host.ReturnHint;

    /// <summary>页头那条面包屑显不显示：有「刚才那条任务」才显示。</summary>
    public bool HasReturnTarget => _host.HasReturnTarget;

    [RelayCommand]
    private void Back() => _host.GoBack();

    [RelayCommand]
    private void Refresh()
    {
        var capabilities = _host.Runtime.Capabilities;
        _configPathRaw = capabilities.McpConfigPath;

        Servers.Clear();
        foreach (var server in capabilities.McpServers)
        {
            Servers.Add(new McpServerCardViewModel(server));
        }

        OnPropertyChanged(nameof(HasServers));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ConfigPathText));

        // 配置刚变过：上一轮的「已连接」不能再挂着，重新真连一次
        _ = ProbeAllAsync();
    }

    // ---- 连接状态 ----

    /// <summary>
    /// 串行探测：一次只起一个进程，避免三个 npx 同时下载把机器拖住。
    /// 页面打开和导入之后各跑一遍，状态才会反映「刚刚写的配置」。
    /// </summary>
    private async Task ProbeAllAsync()
    {
        // 串行是以前的做法：6 个服务端排队，每个最多 20 秒，最后一张卡要等两分钟才出状态。
        // 限 3 个并发，既快又不会同时拉起一堆 npx 把机器拖住。
        using var gate = new SemaphoreSlim(3);
        var tasks = Servers.ToList().Select(async server =>
        {
            await gate.WaitAsync();
            try
            {
                await ProbeAsync(server);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    [RelayCommand]
    private async Task ProbeAsync(McpServerCardViewModel? server)
    {
        if (server is null || server.IsChecking)
        {
            return;
        }

        server.IsChecking = true;
        try
        {
            var result = await _host.Runtime.ProbeMcpServerAsync(server.Name);
            server.Apply(result);
        }
        catch (Exception ex)
        {
            server.Apply(McpProbeResult.Failed(ex.Message));
        }
        finally
        {
            server.IsChecking = false;
        }
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
        _editingName = null;
        IsFormOpen = true;
        IsImportOpen = false;
        Notice = null;
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(SaveLabel));
    }

    /// <summary>改一个已有服务端。远程（url / headers）表单表达不了，转去 JSON 编辑。</summary>
    [RelayCommand]
    private void Edit(McpServerCardViewModel? server)
    {
        if (server is null || string.IsNullOrWhiteSpace(_configPathRaw))
        {
            return;
        }

        var raw = CapabilityInstaller.ReadMcpServer(_configPathRaw, server.Name);
        if (raw is null)
        {
            ShowNotice("读不到这个服务端的配置，可能刚被改过；点「重新扫描」再看一次。", isError: true);
            return;
        }

        var node = JsonNode.Parse(raw) as JsonObject;
        if (node?["url"] is not null)
        {
            // 远程服务端的 headers 原样保留，用 JSON 改最稳；勾上覆盖同名就是原地替换
            ImportJson = "{\"mcpServers\":{\"" + server.Name + "\":" + raw + "}}";
            OverwriteExisting = true;
            IsImportOpen = true;
            IsFormOpen = false;
            Notice = null;
            ShowNotice("远程服务端（url / headers）请在 JSON 里改；导入时已勾上「覆盖同名」。", isError: false);
            return;
        }

        _editingName = server.Name;
        Name = server.Name;
        Command = node?["command"]?.GetValue<string>() ?? "npx";
        Args = JoinArgs(node?["args"]);
        IsFormOpen = true;
        IsImportOpen = false;
        Notice = null;
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(SaveLabel));
    }

    private static string JoinArgs(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return string.Empty;
        }

        var parts = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text) && text is not null)
            {
                parts.Add(text);
            }
        }

        return string.Join(' ', parts);
    }

    [RelayCommand]
    private void CancelForm()
    {
        IsFormOpen = false;
        Name = string.Empty;
        Args = string.Empty;
        _editingName = null;
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(SaveLabel));
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_configPathRaw))
        {
            ShowNotice("mcp.json 路径不可用，先选一个工作区。", isError: true);
            return;
        }

        var editing = _editingName;

        try
        {
            if (editing is null)
            {
                CapabilityInstaller.AddMcpServer(_configPathRaw, Name, Command, Args);
            }
            else
            {
                CapabilityInstaller.ReplaceMcpServer(_configPathRaw, editing, Name, Command, Args);
            }

            _host.Runtime.InvalidateSessions();
            Refresh();
            IsFormOpen = false;
            _editingName = null;
            var savedName = Name;
            Name = string.Empty;
            Args = string.Empty;
            OnPropertyChanged(nameof(FormTitle));
            OnPropertyChanged(nameof(SaveLabel));

            ShowNotice(
                editing is null
                    ? "已写入 mcp.json。下一个会话会连接它，工具以 mcp__<名称>__<工具> 出现。"
                    : $"已保存对「{savedName}」的修改。下一个会话按新配置连接。",
                isError: false);
        }
        catch (Exception ex)
        {
            ShowNotice(ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private void Remove(McpServerCardViewModel? server)
    {
        if (server is null || string.IsNullOrWhiteSpace(_configPathRaw))
        {
            return;
        }

        try
        {
            // 删除前把原文留一份：撤销要按原样放回去（env / headers 一个字都不改）
            var raw = CapabilityInstaller.ReadMcpServer(_configPathRaw, server.Name);
            var originalBytes = File.Exists(_configPathRaw) ? File.ReadAllBytes(_configPathRaw) : [];

            if (CapabilityInstaller.RemoveMcpServer(_configPathRaw, server.Name))
            {
                if (raw is not null)
                {
                    var afterBytes = File.Exists(_configPathRaw) ? File.ReadAllBytes(_configPathRaw) : [];
                    SetUndo(server.Name, raw, originalBytes, afterBytes);
                }

                _host.Runtime.InvalidateSessions();
                Refresh();
                ShowNotice($"已移除「{server.Name}」。配置里的 env / headers 一起删了，10 秒内可以撤销。", isError: false);
            }
        }
        catch (Exception ex)
        {
            ShowNotice(ex.Message, isError: true);
        }
    }

    /// <summary>撤销窗口。10 秒够用，又不至于让「已删除」的提示一直挂在页面上。</summary>
    private void SetUndo(string name, string json, byte[] originalBytes, byte[] afterBytes)
    {
        _undoEntry = (name, json, originalBytes, afterBytes);
        CanUndo = true;
        OnPropertyChanged(nameof(UndoText));

        _undoTimer?.Stop();
        _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _undoTimer.Tick += (_, _) =>
        {
            ClearUndo();
        };
        _undoTimer.Start();
    }

    private void ClearUndo()
    {
        _undoTimer?.Stop();
        _undoTimer = null;
        _undoEntry = null;
        CanUndo = false;
        OnPropertyChanged(nameof(UndoText));
    }

    [RelayCommand]
    private void UndoRemove()
    {
        if (_undoEntry is not { } entry || string.IsNullOrWhiteSpace(_configPathRaw))
        {
            return;
        }

        try
        {
            CapabilityInstaller.UndoRemoveMcpServer(
                _configPathRaw, entry.OriginalBytes, entry.AfterBytes, entry.Name, entry.Json);
            ClearUndo();
            _host.Runtime.InvalidateSessions();
            Refresh();
            ShowNotice($"已恢复「{entry.Name}」。", isError: false);
        }
        catch (Exception ex)
        {
            ShowNotice("恢复失败：" + ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private void OpenConfig()
    {
        if (!string.IsNullOrWhiteSpace(_configPathRaw) && File.Exists(_configPathRaw))
        {
            if (!Ui.OpenExternal(_configPathRaw))
            {
                ShowNotice("打不开 mcp.json，可能没有关联的编辑器。", isError: true);
            }

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
