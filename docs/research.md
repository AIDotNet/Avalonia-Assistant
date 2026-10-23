# 两个参考项目的调研结论

调研对象是本机的两个仓库，同时也核对了它们发布到 NuGet 的包：

| 项目 | 本地路径 | 本次使用方式 | 版本 |
| --- | --- | --- | --- |
| pi.net | `D:\AI\参考项目\pi.net` | NuGet 包 `Pi.Agent`（传递引入 `Pi.Ai`、`Pi.Telemetry`） | 1.4.1 |
| TKCode.Arc | `D:\AI\参考项目\TKCode.Arc` | NuGet 包 `TKCode.Arc`、`TKCode.Arc.Fonts` | 0.3.0-preview.1 |

> 包和本地源码在少数地方不完全一致，下面单独标注。实现以**包实际暴露的 API**为准，本地源码用来理解行为和边界。

## 1. pi.net：Agent 框架

### 1.1 结构

pi.net 是 Pi（TypeScript）的 .NET 10 移植，按“越往上越薄”分层：

```
Pi.Ai          多 provider LLM API：流式事件、工具声明、thinking、OAuth
  ↑
Pi.Agent       状态机 + 双队列 + agent-loop + 串行/并行工具执行
  ↑
Pi.CodingAgent 完整 coding agent：内置工具、技能、会话存档、模型目录、CLI
```

另外还有 `Pi.Mcp`、`Pi.Durable`、`Pi.Tui`、`Pi.Chord`、`Pi.Server/Client` 等独立包，本次没有使用。

### 1.2 用到的 API

**装配 Agent**

```csharp
var agent = new Agent(new AgentOptions
{
    StreamFn = streamFn,           // 必填：模型出口
    Model = model,                 // Id / Name / Api / Provider
    Messages = messages,           // 初始 transcript（首条是 SystemMessage）
    Tools = tools,                 // AgentTool 列表
    ToolExecution = ToolExecutionMode.Sequential,
    BeforeToolCall = ...,          // 权限与授权拦截
});
```

- `StreamFn` 的签名是 `(Model, TranscriptContext, SimpleStreamOptions?, CancellationToken) → Task<IAssistantMessageEventStream>`。
- 旧版入口 `OpenAiCompletions.StreamSimple(Model, context, options, ct)` 和
  `AnthropicMessages.StreamSimple(...)` 正好匹配这个签名，可以直接作为 `StreamFn`。
- `SimpleStreamOptions` 里放 `ApiKey`、`BaseUrl`、`MaxTokens`、`Temperature`、`Signal`。
  兼容端点必须显式给 `BaseUrl`，适配器会自己拼 `/chat/completions` 或 `/v1/messages`，**不要重复写 `/v1` 之后再拼一次**。

**消息**

`ChatMessage` 的四个标准变体是 `SystemMessage` / `UserMessage` / `AssistantMessage` / `ToolResultMessage`。
`Messages.UserText`、`Messages.AssistantText` 是便捷工厂。
只要 `Messages` 的第一条是 `SystemMessage`，`AgentOptions.SystemPrompt` 就不会再被折成前导消息——本项目的 system prompt 是自己构造的。

**工具**

```csharp
new AgentTool(
    Name, Description,
    new ToolSchema(jsonSchema),
    Label,
    Execute: async (callId, args, ct, onUpdate) => new AgentToolResult([new TextContent(text)]));
```

- `args` 反序列化后通常是 `JsonElement`，统一序列化再读属性最稳。
- 工具失败返回 `AgentToolResult.FromError(...)`，模型会看到错误文本。
- `BeforeToolCall` 返回 `Block = true` 时，工具不会执行，模型会收到拒绝原因。

**事件**

`agent.Subscribe((evt, ct) => ...)`：

| 事件 | 用途 |
| --- | --- |
| `MessageUpdate { AssistantMessageEvent: TextDelta }` | 正文增量 |
| `MessageUpdate { AssistantMessageEvent: ThinkingDelta }` | 思考增量 |
| `ToolExecutionStart` | 工具开始，带 callId / name / args |
| `ToolExecutionEnd` | 工具结束，带结果 / IsError / 耗时 |
| `AgentEnd` | 整个运行结束 |

`agent.Prompt(text)` 在整轮（含工具调用轮次）跑完后才返回；`agent.Abort()` 可中止；
`agent.Messages` 是完整 transcript。

### 1.3 为什么只选 `Pi.Agent`

`Pi.CodingAgent` 自带 read/bash/edit/write、技能、扩展、会话存档、模型目录和 CLI。
这些能力很强，但会把本项目的工具集、权限模型和会话格式绑死在它的实现上。
`Pi.Agent` 只做“循环 + 工具执行”，正好留出：

- 自己的工具注册表（文件 / 命令 / 网络 / 时间）；
- 自己的三档权限与逐次授权；
- 自己的 JSON 会话格式与 UI 事件协议；
- 自己的 provider 配置（不碰 `~/.pi` 凭据体系）。

`PiAgentRuntime` 是整个解决方案里唯一 `using Pi.*` 的实现文件（测试除外），这是刻意划的边界。

### 1.4 包与源码的差异

- 包 `Pi.Agent` 1.4.1 与本地 `Directory.Build.props` 的 `<Version>1.4.1</Version>` 一致，API 对得上。
- 本地源码的 `TreatWarningsAsErrors=true` 只影响它自己的仓库；以包方式引用不会把这条规则带进来。

## 2. TKCode.Arc：UI 框架

### 2.1 结构

Arc 是 AtomUI 之上的 Arc 风格组件库，依赖版本是精确锁定的：

| 依赖 | 版本 |
| --- | --- |
| Avalonia | 12.1.4 |
| AtomUI.Desktop.Controls | 6.2.8 |
| StreamMark（Markdown / TextMate 高亮） | 0.1.0-alpha.25 |

组件按 Arc 侧边栏分类组织：`Buttons`、`Cards`、`Content`、`Editors`、`Messages`、`Navigation`、`Selects`、`TextFields`、`Toggles`、`Activity` 等。
本项目主要用到：

- `Content/Markdown`：流式 Markdown 与代码高亮；
- `TextFields/Textarea`：输入框；
- `TextFields/Input`：设置表单；
- `Buttons/Button` 与 `ArcGlyph`：图标按钮；
- `Avatars`、`Cards`、`Toggles/Switch`、`Selects/ChipGroup`；
- `ArcDesignTokenResource`：颜色 / 圆角 / 字体令牌。

### 2.2 启动方式

```csharp
AppBuilder.Configure<AssistantApp>()
    .UsePlatformDetect()
    .WithInterFont()
    .WithArcFonts()
    .UseAtomUI(builder =>
    {
        builder.UseDesktopControls();
        builder.UseArc();
    });
```

`UseArc()` 会把 Arc 的 ControlTheme 注册进 AtomUI 的主题系统；
`UseAtomUI` 之后**不需要**再手动 `StyleInclude` Arc 的主题。

### 2.3 包与本地源码的三个差异（踩过的坑）

1. **XAML 命名空间不同。**
   本地源码是 `https://github.com/AIDotNet/TKCode.Arc`；
   NuGet 包 0.3.0-preview.1 里是 `https://github.com/AtomUICommunity/TKCode.Arc`。
   本项目统一使用包里的地址，所有 `xmlns:arc` 都是 `AtomUICommunity`。

2. **图标数量不同。**
   本地源码有 213 个 `ArcIcons`，包里有 187 个。
   包内没有 `Wrench` / `BookOpen` / `ShieldCheck` / `Brain` / `Cpu`，本项目改用
   `Zap` / `FolderOpen` / `Shield` / `Sparkles` / `Monitor`。

3. **标准 Avalonia 控件没有主题。**
   只调用 `UseAtomUI` 时，`Avalonia.Controls.TextBox` 能找到类型但**没有模板**，运行起来是隐形的。
   因此输入框统一改用 Arc 的 `Input` / `Textarea`，不要混用标准 `TextBox`。

> 也试过加 `FluentTheme` 来补标准控件模板，但它会覆盖侧边栏导航按钮的背景（实测“助理”和“添加工作区”变成灰色块），
> 所以最终没有引入 FluentTheme，而是全部使用 Arc 控件。

### 2.4 样式定制

- 颜色、字体、阴影放在 `Styles/Tokens.axaml`，尽量用语义名（`SurfaceBrush`、`TextMutedBrush`…）。
- 组件级样式放在 `Styles/AppStyles.axaml`，用 class 选择器（`.nav`、`.chip`、`.task`、`.tool-head`）。
- `Textarea` 作为输入框时，外面已经有圆角卡片，所以用后代选择器把内层 `FieldFrame` 的边框和背景去掉，
  避免“框中框”：

```xml
<Style Selector="arc|Textarea.composer arc|FieldFrame">
  <Setter Property="Background" Value="Transparent" />
  <Setter Property="BorderThickness" Value="0" />
</Style>
```

## 3. 职责划分

| 关注点 | pi.net | TKCode.Arc | 本项目 |
| --- | --- | --- | --- |
| LLM 协议、流式、工具调用 | ✅ | | |
| Agent 循环、abort、队列 | ✅ | | |
| 控件、主题、Markdown、输入 | | ✅ | |
| 工具实现（文件 / 系统 / 网络） | | | ✅ |
| 权限与逐次授权 | 提供钩子 | | ✅ 策略与 UI |
| 会话 / 设置持久化 | | | ✅ |
| UI 事件协议 | | | ✅ |
| 依赖注入 / 组合根 | | | ✅ |

**依赖规则（由项目引用强制）**

```
Desktop ──► UI ───────┐
   │                  ▼
   ├──► Agent ──► Abstractions
   ├──► Storage ──────┘
   └──► Speech ───────┘
```

- `AvaloniaAssistant.UI` 只引用 `Abstractions`，**不引用** `Pi.Agent`；
- `AvaloniaAssistant.Agent` 只引用 `Abstractions` + `Pi.Agent`，**不引用** Avalonia；
- `AvaloniaAssistant.Desktop` 是唯一同时认识两边的项目，负责组装。
