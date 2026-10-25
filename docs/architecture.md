# 架构说明

## 1. 一句话

**UI 只认识 `IAssistantRuntime` 和一组事件；Agent 只认识 Pi 的 `Agent`；`Desktop` 在中间把它们接起来。**

```
┌────────────────────────────────────────────────────────────────────┐
│ AvaloniaAssistant.Desktop                                          │
│ Program.cs：DI 组合根 + Arc/AtomUI 初始化                            │
└───────────────┬───────────────────────────────┬────────────────────┘
                │                               │
┌───────────────▼──────────────┐  ┌─────────────▼──────────────────┐
│ AvaloniaAssistant.UI         │  │ AvaloniaAssistant.Agent        │
│ MainWindow / Views / VMs     │  │ PiAgentRuntime / ToolRegistry  │
│ 依赖：Abstractions + Arc     │  │ 依赖：Abstractions + Pi.Agent  │
└───────────────┬──────────────┘  └─────────────┬──────────────────┘
                │                               │
        ┌───────▼───────────────────────────────▼───────┐
        │ AvaloniaAssistant.Abstractions                │
        │ IAssistantRuntime / 事件 / Session / Settings │
        │ 零第三方依赖                                   │
        └───────▲───────────────────────────────▲───────┘
                │                               │
     ┌──────────┴──────────┐         ┌──────────┴──────────┐
     │ Storage             │         │ Speech              │
     │ settings / sessions │         │ NAudio + 转写 API   │
     └─────────────────────┘         └─────────────────────┘
```

依赖方向是单向的：`UI` 与 `Agent` 互不引用。想换掉 pi.net，只需要再写一个
`IAssistantRuntime` 实现；想换 UI 框架，`Agent` / `Storage` 一行不用改。

## 2. 项目与目录

| 项目 | 职责 | 关键目录 |
| --- | --- | --- |
| `AvaloniaAssistant.Abstractions` | 契约与 DTO，零依赖 | `IAssistantRuntime.cs`、`AgentContracts.cs`、`ChatSession.cs`、`AssistantSettings.cs` |
| `AvaloniaAssistant.Agent` | pi.net 适配、系统提示、工具注册与执行 | `PiAgentRuntime.cs`、`PiStreamFactory.cs`、`Tools/` |
| `AvaloniaAssistant.Storage` | JSON 设置/会话读写，原子写入 | `JsonSettingsProvider.cs`、`JsonSessionStore.cs`、`AppPaths.cs` |
| `AvaloniaAssistant.Speech` | NAudio 录音 + 语音转写 | `NAudioVoiceInput.cs` |
| `AvaloniaAssistant.UI` | 窗口、页面、ViewModel、样式、截图 | `Views/`、`ViewModels/`、`Styles/` |
| `AvaloniaAssistant.Desktop` | 入口、DI、Arc 初始化、参数解析 | `Program.cs` |
| `AvaloniaAssistant.Agent.Tests` | 循环、工具、HTTP、持久化、技能导入测试 | 6 个测试文件，24 个用例 |

## 3. 一次对话的时序

```
ChatView 输入
   │  ComposerView.SendCommand
   ▼
ChatViewModel.SendPromptAsync
   │  1. 追加 UserMessageViewModel + ChatEntry
   │  2. 追加 AssistantMessageViewModel（占位）
   │  3. 组装 AgentRunRequest（history / permission / workspace / gate）
   ▼
IAssistantRuntime.RunAsync  ──►  PiAgentRuntime
   │                               ├─ 建 SystemMessage + 历史
   │                               ├─ 注册 8 个 AgentTool
   │                               ├─ StreamFn = OpenAi/Anthropic StreamSimple
   │                               └─ Agent.Prompt(prompt)
   │                                        │
   │   ◄── AgentStreamEvent ────────────────┘
   │        TextDelta / ThinkingDelta
   │        ToolStarted / ToolFinished
   │        RunCompleted / RunFailed
   ▼
ChatViewModel 更新 MessageViewModel
   │  - 文本 55ms 批量刷新（避免每个 token 重排 Markdown）
   │  - 工具卡片原位更新
   │  - 自动滚动（用户上滚后停止粘底）
   ▼
落盘 ChatSession（JSON）+ 刷新侧边栏
```

## 4. 事件协议

`AgentStreamEvent` 是 UI 与 Agent 之间唯一的通信面：

| 事件 | 携带 | UI 行为 |
| --- | --- | --- |
| `RunStarted` | sessionId / model | 顶部状态改为“正在连接 …” |
| `TextDelta` | 增量文本 | 追加到 Markdown，节流刷新 |
| `ThinkingDelta` | 思考增量 | 折叠区“思考过程” |
| `ToolStarted` | callId / name / label / args | 插入工具卡片，状态“运行中” |
| `ToolFinished` | callId / result / isError / duration | 更新状态、耗时、可展开结果 |
| `RunCompleted` | stopReason / error / token 用量 | 结束流式，写 token 脚注 |
| `RunFailed` | message | 红色错误块 |

`IAssistantRuntime.Abort(sessionId)` 是带外的中止通道：UI 调 `Stop` 时同时取消
`CancellationToken` 并调用它。工具执行前的授权也走带外回调 `IToolApprovalGate`，
它由 `ChatViewModel` 实现，把授权卡片插进当前对话并等待用户选择：
允许一次 / 本会话都允许（按工具名记住，只在这一个对话实例里生效）/ 拒绝。

设置的「测试连接」走 `IAssistantRuntime.ProbeAsync`，只请求 `GET /models`：
验证地址、密钥和模型名，不产生生成费用；失败时把 URL 与状态码原样带回，
因为 401 和 404 对应的修法不同。

会话缓存按「这次带来的历史是不是在上一轮历史后面接着长的」判断复用（`SessionHolder.Matches`）：
工作区、模型、端点、协议、采样参数（Temperature / MaxTokens）、工具开关、权限档位、
工作区信任开关任一变化都会重建。不能要求历史全等——正常追问时历史必然比上一轮多出
「上一轮的问 + 答」，按全等比较会导致每条消息都重建会话。
用户重试时界面历史会被截短，这时反而必须重建，否则旧回复还留在会话里。

采样参数也要比：它们在建会话时被 `StreamSimple` 的闭包捕获，漏比的话改了设置下一轮仍是旧值。

换页或换任务时 `ChatViewModel.Dispose` 会取消本轮并 `Abort`：授权卡片随页面一起消失后
没人能再点，不取消的话这一轮会永久挂在等待授权上。

## 5. 权限模型

| 模式 | 读工作区 | 写工作区 | 越界路径 | 执行命令 |
| --- | --- | --- | --- | --- |
| 只读 | ✅ | ❌ 直接阻止 | ❌ | ❌ |
| 工作区 | ✅ | ✅ | ❌ 直接阻止 | ⚠️ 逐次授权 |
| 完全访问 | ✅ | ✅ | ✅ | ✅ 直接执行 |

实现分两层：

1. **描述层**：`AgentToolInfo.IsMutating` / `RequiresApproval`。
2. **执行层**：
   - `BeforeToolCall` 检查 `PermissionMode`：只读模式拦下所有 mutating 工具；
   - 需要授权的工具调用 `IToolApprovalGate.RequestAsync`，用户拒绝则返回 `Block=true`；
   - 文件工具再用 `ToolPaths.Resolve` 做工作区边界检查，`FullAccess` 才放行越界。

CodingAgent 的内置工具（`read` / `grep` / `find` / `ls` / `edit` / `write`）和 MCP 工具没有
`AgentToolInfo`，由 `ToolAccess` 按名字分类：`edit` / `write` / `bash` / `powershell` 与
`mcp__*` 视为 mutating 且逐次授权，其余按只读处理。工作区内的文件写入走自建 `write_file`，
不弹确认；`edit` 改已有文件仍要确认。

`run_command` 还有 60 秒超时、进程树 kill、20 万字符输出上限；
`open_path` 拒绝直接启动 `.exe` / `.bat` / `.ps1` 等可执行文件，避免把“打开”变成“运行”。

## 6. 持久化

```
%APPDATA%\AvaloniaAssistant\
├── settings.json           # 模型、密钥、权限、工作区、工具开关
├── sessions\
│   ├── 9f2c….json          # 一个会话一个文件
│   └── …
└── Workspace\              # 默认工作区
```

- 写入走 `*.tmp` + `File.Move(overwrite)`，避免半截 JSON。
- 会话 DTO 与 UI 解耦：`ChatSession` / `ChatEntry` / `ToolCallRecord`，不含任何 Avalonia 类型。
- `settings.json` 里的 API Key 目前是明文；这是已知边界，见 `README` 的“已知边界”。

## 7. UI 结构

```
MainWindow (1280×820)
├── SidebarView (268px)
│   ├── 品牌 / 收起
│   ├── 新建任务
│   ├── 导航：助理 / 技能 / 资料库 / 设置
│   ├── 任务分组（今天 / 昨天 / 最近 7 天 / 更早）
│   ├── 空间（工作区）
│   └── 用户卡
└── Border（白底圆角面板）
    └── ContentControl + DataTemplate
        ├── HomeView        欢迎、模式、快捷提示、建议卡、输入框
        ├── ChatView        会话头、消息列表、授权卡、输入框
        ├── ToolsView       5 个自建工具、开关与技能导入
        ├── McpView         MCP 服务端列表与添加表单
        ├── LibraryView     工作区顶层文件
        └── SettingsView    模型 / 权限 / 工作区 / 关于
```

`ComposerView` 是独立控件，首页和会话页共用；它只依赖 `ComposerViewModel`。
模型和权限两个菜单在 code-behind 里动态生成 `MenuFlyout` / `Flyout`；工作区那条换成了独立控件
`WorkspacePicker`（搜索 + 列表 + 新建 / 打开本地文件夹 / 不使用工作空间），首页右上角的「工作区」
挂的是同一份，弹层装配收在 `WorkspacePickerPopup` 里。

UI 的状态变更全部通过 ViewModel 命令；code-behind 只做四件事：
输入框回车、动态菜单、弹层挂载、消息列表自动滚动。

## 8. 扩展点

### 加一个 provider

1. `AgentProviderKind` 加枚举；
2. `ProviderPreset.All` 加一条（BaseUrl / 默认模型 / 协议）；
3. 如果协议是新的，在 `PiStreamFactory` 里加分支。

UI 的 provider 卡片和模型菜单会自动出现。

### 加一个工具

1. 在 `Tools/` 实现 `IAgentTool`（`Info` + `ExecuteAsync`）；
2. 加进 `AgentToolRegistry.DefaultTools()`；
3. 设置 `IsMutating` / `RequiresApproval`。

UI 的技能页与系统提示里的工具清单都会自动更新。

### 加一个页面

1. 写 `XxxViewModel`；
2. 写 `XxxView.axaml`，构造函数里 `AvaloniaXamlLoader.Load(this)`；
3. 在 `App.axaml` 的 `Application.DataTemplates` 里加一条 `DataType → View`；
4. 在 `MainWindowViewModel` 里给一个 `CurrentPage = new XxxViewModel(this)` 的命令。

### 换掉 Agent 框架

实现 `IAssistantRuntime`，在 `Program.cs` 里替换注册即可。UI 不需要改。

## 9. 线程模型

- `PiAgentRuntime.RunAsync` 在后台任务里跑 Pi 的循环，事件写进无界 `Channel`；
- UI 的 `await foreach` 在 Avalonia 同步上下文里消费，所以 ViewModel 更新天然在 UI 线程；
- `IToolApprovalGate` 可能从后台线程调用，实现里用 `Dispatcher.UIThread` 切回 UI；
- 文本增量在 `AssistantMessageViewModel` 里用 55ms 的 `DispatcherTimer` 批量刷新，
  避免每个 token 都重新解析 Markdown。

## 10. 测试

| 测试文件 | 验证什么 |
| --- | --- |
| `PiAgentRuntimeTests` | Faux 模型驱动的完整循环、工具调用、只读拦截、授权拒绝、缺 Key |
| `OpenAiWireTests` | 本机 TCP 起一个 OpenAI 兼容 SSE 端点，走真实 HTTP/流式解析，断言工具声明在请求体里 |
| `ToolExecutionTests` | 自建 `write_file` 写入正常，CodingAgent 的 `read` 能读回来，越界写被工作区沙箱拦下 |
| `CapabilityInstallerTests` | 技能文件夹 / zip 导入、缺清单、多技能、重名与非法扩展名 |
| `StorageTests` | settings / session 的 JSON 往返与删除 |

运行：

```bash
dotnet test tests/AvaloniaAssistant.Agent.Tests
```

UI 没有做自动化测试，但提供 `--screenshot` 离线渲染：

```bash
dotnet run --project src/AvaloniaAssistant.Desktop -- --screenshot docs/images/home.png --page home
```
