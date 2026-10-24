# Avalonia Assistant

Windows 桌面 AI 助理。Agent 循环用 [pi.net](https://github.com/AIDotNet/pi.net) 的 **Pi.CodingAgent**，
界面用 [TKCode.Arc](https://github.com/AIDotNet/TKCode.Arc)，交互参考 WorkBuddy：左侧任务与空间，
中间对话，输入框里带模型、权限和工作区选择。

![首页](docs/images/home.png)

## 现在能做什么

- **流式对话**：DeepSeek / OpenAI / Anthropic / 任意 OpenAI 兼容端点（Ollama、LM Studio、vLLM、One-API）。
- **真的会动手**：自建 8 个日常办公 / 编码工具 + CodingAgent 内置的 read / grep / find / ls / edit。
- **技能与 MCP**：工作区 `.pi/skills` 的 `SKILL.md` 按需加载，技能页可从文件夹或 zip 导入；
  `.pi/mcp.json` 的 MCP 服务端启动时连接。
- **@ 直接引用文件**：输入框打 `@` 按名字搜当前工作区的文件——文件名精确 > 前缀 > 包含 > 路径 > 字符子序列，
  自动跳过 `node_modules` / `.git` 这类目录；选中即引用，**发送时才读内容**，不用先把文件导入到哪里。
- **粘贴图片**：输入框里 Ctrl+V 直接贴图（截图也行），缩略图挂在输入框上方，和文字一起发；
  图片落在数据目录的 `images/` 下，会话 JSON 只记路径，存档不会被 base64 撑大。
- **长会话能自己压缩**：CodingAgent 的上下文压缩与自动重试，不必手动清空。
- **三档权限**：只读 / 工作区 / 完全访问；执行命令会在对话里弹授权卡。
- **工具过程可见**：每次调用压成一行（「执行命令：dotnet build」+ 耗时），点开才看参数和结果；
  连续十几次调用也只是一列短行，不会把对话淹掉。
- **会话留在本机**：一个会话一个 JSON 文件，第一句话一发出就落盘（不用等回复跑完）。
  侧栏「任务」列全部任务、按今天 / 昨天 / 最近 7 天分组，「空间」只是按目录再归一次类——
  归了空间的任务在「任务」里照样看得见。
- **工作区一点就换**：输入框下面那条点开是「搜索 + 工作区列表」，当前那个打勾；
  新建工作空间、打开本地文件夹、不使用工作空间都在这一个浮层里，首页右上角是同一个。
  **新建任务默认是普通任务**，上一次选的空间不跟过来；还没发第一条时挑的空间当场生效，
  发过话就钉在那条任务上——中途换目录，前面引用过的文件就对不上了。
  普通任务的文件落在 `~/.avaloniaassistant`（像 `.claude` / `.codex` 那样藏在用户目录里，
  第一次用到时才建），不再把整个用户目录当工作区。
- **窗口记着上次的样子**：位置、尺寸、侧栏宽度（可拖动分隔条）、界面缩放（90%–125%，Ctrl+滚轮 / Ctrl+0）
  和最后看的那条任务，关掉再开直接接上；设置里可选「启动时：首页 / 上次会话」。
- **时间与引用看得清**：跨天的消息之间插日期分隔，时间悬停给完整日期；
  引用文件是带大小、行数、截断状态的标签，点一下在资源管理器中定位。
- **快捷键有张表**：Ctrl+/ 打开；Ctrl+N 新建任务、Ctrl+K 搜任务、Ctrl+F 会话内查找；
  审批卡支持 Ctrl+Enter 允许一次、Ctrl+Shift+Enter 本会话允许、Esc 拒绝。
- **Markdown**：标题、列表、表格、行内代码、代码块高亮。
- **语音输入**：NAudio 录音 + `/audio/transcriptions` 转写（端点需支持）。
- **技能页**：卡片式浏览，按分类筛选、关键词搜索，开关与导入都持久化。
- **MCP 页**：列出 `mcp.json` 里的服务端，并**真的起一次进程握手**——已连接会给出工具数，
  起不来会给出原因（Windows 上 `npx` 少了 `cmd /c` 包装在这里就能看见），每张卡可重试。
  整段粘贴标准 JSON（`mcpServers` / VS Code 的 `servers` 写法）就能一次导入多个，也能从现成的
  `mcp.json` 文件读进来；只加一个 stdio 服务端时用表单。
- **导出会话**：对话页右上角一键存成 Markdown，工具调用、失败轮次、引用文件都在里面。

| 会话 | 设置 |
| --- | --- |
| ![会话](docs/images/chat.png) | ![设置](docs/images/settings.png) |

![技能](docs/images/tools.png)

## 两个核心框架

界面用 **TKCode.Arc**，Agent 循环用 **pi.net**，其余选型都是围着这两个补的。

| 层 | 框架 | 引用包 | 版本 |
| --- | --- | --- | --- |
| UI 框架 | [TKCode.Arc](https://github.com/AIDotNet/TKCode.Arc)（AtomUI 生态） | `TKCode.Arc`、`TKCode.Arc.Fonts` | 0.3.0-preview.3 |
| Agent 框架 | [pi.net](https://github.com/AIDotNet/pi.net)（Pi 的 .NET 移植） | `Pi.CodingAgent`（传递引入 `Pi.Agent` / `Pi.Ai` / `Pi.Mcp` 等） | 1.4.2 |

- **TKCode.Arc 负责界面**：控件、主题、图标字体全部来自 Arc，入口在 `Program.BuildAvaloniaApp`
  里一次装好——`WithArcFonts()` 加 `UseAtomUI(b => b.UseDesktopControls().UseArc())`，
  页面、样式、VM 都在 `AvaloniaAssistant.UI`。
- **pi.net 负责 Agent**：`Sdk.CreateAgentSessionAsync` 装配出 `AgentSession`，技能、MCP、
  上下文压缩、自动重试都由它带着；模型出入仍由本项目自己的 provider 设置决定，
  通过 `ProviderConfigInput.StreamSimple` 直连 `Pi.Ai` 的流式实现，不读 `~/.pi/agent/auth.json`。
- **权限没有让给框架**：CodingAgent 的 `BeforeToolCall` 只服务扩展的 `tool_call` 事件，
  本项目在它外面串了一层三档权限与授权卡片（`PiAgentRuntime.InstallPermissionHook`）。
  只读模式下连 `edit` 都不会出现在工具集里。
- **两边只在一个地方碰面**：`AvaloniaAssistant.Desktop` 这个组合根。`UI` 不引用 `Pi.*`，
  `Agent` 不引用 Avalonia，换掉其中任何一个都不用动另一边。

两个包都直接走 NuGet，不依赖本机参考仓库路径。Arc 包与本地源码的命名空间差异
（`AtomUICommunity` vs `AIDotNet`）及踩坑记录见 [docs/research.md](docs/research.md)。

## 其余技术栈

| 层 | 选型 | 版本 |
| --- | --- | --- |
| 运行时 | .NET | 10.0（`net10.0`） |
| 基础 UI | Avalonia | 12.1.4 |
| 控件库 | AtomUI.Desktop.Controls（Arc 依赖） | 6.2.8 |
| MVVM | CommunityToolkit.Mvvm | 8.4.0 |
| 语音 | NAudio | 2.2.1 |

## 快速开始

要求：Windows 10/11，.NET 10 SDK（在 10.0.401 上验证）。

```bash
dotnet restore
dotnet run --project src/AvaloniaAssistant.Desktop
```

首次启动进入首页，点右上角「设置」或提示条里的「去设置」：

1. 选一个服务商（默认 DeepSeek）；
2. 填 API Key；
3. Base URL 与模型会自动带出，也可以手动改；
4. 保存后回到首页，直接输入任务。

默认配置：

| 服务商 | Base URL | 默认模型 | 备选 |
| --- | --- | --- | --- |
| DeepSeek | `https://api.deepseek.com/v1` | `deepseek-flash` | `deepseek-v4-pro` |
| OpenAI | `https://api.openai.com/v1` | `gpt-6.1-sol` | `gpt-6-astra`（需申请）、`gpt-6-luna` |
| Anthropic | `https://api.anthropic.com` | `claude-sonnet-5-5` | `claude-opus-5-5`、`claude-haiku-5-5` |
| 自定义 | `http://localhost:11434/v1` | `qwen3.8:27b` | `qwen3.8-flash`（云端 API 名） |

> 默认走「够用且便宜」的那一档：DeepSeek 用 Flash、Anthropic 用 Sonnet、本地用 27B。要更强就换成
> `deepseek-v4-pro` 或 `claude-opus-5-5`，Token 消耗也会跟着上去。
>
> 模型名填的是厂商 API 的 `model` 字段，不是 App 里的展示名，而且写法各家不一样：
> DeepSeek `deepseek-flash` 用连字符，Ollama 的 tag 用冒号（`qwen3.8:27b`），填错直接 404。
> 旧的 `deepseek-chat` / `deepseek-reasoner` 已于 2026-07-24 下线，`deepseek-v4-flash` 也已退役
> （请求会被转到 `deepseek-flash`，但别再写了）。

> 自定义端点必须是 OpenAI 兼容的 `/chat/completions`。本地 LLM 不再走 LLamaSharp，
> 用 Ollama / LM Studio 暴露一个兼容端点即可。

### 发布

```bash
dotnet publish src/AvaloniaAssistant.Desktop -c Release -r win-x64 --self-contained false
```

产物在 `src/AvaloniaAssistant.Desktop/bin/Release/net10.0/win-x64/publish/`。

## 项目结构

```
Avalonia-Assistant/
├── Avalonia.Assistant.slnx
├── Directory.Build.props          # net10.0 / 语言版本 / 依赖版本变量
├── Directory.Packages.props       # 集中包版本
├── src/
│   ├── AvaloniaAssistant.Abstractions/   # 契约与 DTO（零依赖）
│   │   ├── IAssistantRuntime.cs          # 运行时、授权、会话、设置、语音接口
│   │   ├── AgentContracts.cs             # 事件、模型、工具描述、运行请求
│   │   ├── ChatSession.cs                # 会话 / 消息 / 工具调用 DTO
│   │   └── AssistantSettings.cs          # 设置、provider 预设、权限
│   ├── AvaloniaAssistant.Agent/          # 唯一认识 pi.net 的项目
│   │   ├── PiAgentRuntime.cs             # 会话缓存、事件映射、权限钩子、abort
│   │   ├── CodingSessionFactory.cs       # ModelRuntime + Sdk 装配（技能 / MCP / 内置扩展）
│   │   ├── CapabilityScanner.cs          # 扫 .pi/skills 与 mcp.json，供界面展示
│   │   ├── ToolAccess.cs                 # 内置 / MCP 工具的安全属性判定
│   │   ├── PiStreamFactory.cs            # 测试可替换的 OpenAI / Anthropic 流式入口
│   │   ├── AgentToolRegistry.cs          # 自建工具清单 → ToolDefinition
│   │   └── Tools/                        # 5 个自建工具（读 / 列 / 搜走 CodingAgent 内置）
│   ├── AvaloniaAssistant.Storage/        # settings.json / sessions/*.json / 技能导入
│   ├── AvaloniaAssistant.Speech/         # NAudio 录音 + 转写
│   ├── AvaloniaAssistant.UI/             # 窗口、页面、VM、样式
│   │   ├── Views/                        # MainWindow / Sidebar / Home / Chat / Tools / Mcp / Library / Settings
│   │   ├── ViewModels/                   # 命令、会话状态、消息 VM
│   │   └── Styles/                       # Tokens.axaml / AppStyles.axaml
│   └── AvaloniaAssistant.Desktop/        # 入口、DI、Arc 初始化
├── tests/AvaloniaAssistant.Agent.Tests/  # 31 个测试
└── docs/
    ├── research.md               # 两个参考项目的结构与集成方式
    ├── migration.md              # 旧功能逐项盘点与取舍
    ├── architecture.md           # 分层、时序、权限、扩展点
    └── images/                   # README 截图
```

## 分层与解耦

```
UI ──► Abstractions ◄── Agent ──► Pi.CodingAgent ──► Pi.Agent / Pi.Ai / Pi.Mcp
        ▲   ▲
   Storage  Speech
```

- `UI` 不引用任何 `Pi.*`，只消费 `IAssistantRuntime`、`AgentStreamEvent` 与 `AgentCapabilities`；
- `Agent` 不引用 Avalonia，工具执行结果只返回文本；
- `Desktop` 是唯一同时认识两边的组合根。

想换 Agent 框架：实现 `IAssistantRuntime` 并在 `Program.cs` 替换注册。
想换 UI：`Agent` / `Storage` 不变。详见 [docs/architecture.md](docs/architecture.md)。

## 技能与 MCP

两者都按 CodingAgent 的目录约定发现，不需要写代码：

```
<工作区>/.pi/
├── skills/<名称>/SKILL.md     # 项目级技能，frontmatter 写 name / description
└── mcp.json                   # 项目级 MCP 服务端
~/.pi/agent/
├── skills/<名称>/SKILL.md     # 全局技能
└── mcp.json                   # 全局 MCP 服务端
```

- 技能的正文由内置 `read` 工具按需加载，模型只先看到名字和描述；`/skill:<名称>` 也能显式触发。
- MCP 服务端**必须写 `"exposure": "direct"`**，否则工具只留给 codemode，模型直接调不到。
- **Windows 上手写 `mcp.json` 时，`npx` / `npm` 这类命令必须用 `cmd /c` 包一层**：

```json
{
  "mcpServers": {
    "filesystem": {
      "command": "cmd",
      "args": ["/c", "npx", "-y", "@modelcontextprotocol/server-filesystem", "D:/work"],
      "exposure": "direct"
    }
  }
}
```

原因是 MCP 的 stdio 传输用 `UseShellExecute = false` 直接起进程，而 `CreateProcess` 不认
`.cmd` / `.bat` 垫片：写 `"command": "npx"`（或 `npx.cmd`）时服务端进程根本起不来，
表现是「配置在、工具永远不出现」，且没有明显报错。通过 MCP 页导入（粘贴 JSON 或填表单）时，
`CapabilityInstaller` 会自动补这层包装，不用自己写；直接手写文件则要自己写 `cmd /c`。
macOS / Linux 直接写 `npx`。
- 项目级 `.pi` 属于「需要信任」的配置（技能、MCP、`AGENTS.md` 都会进上下文）。
  桌面助理默认信任你选的工作区，`settings.json` 里把 `TrustWorkspace` 设为 `false` 可以关掉，
  关掉后只读全局 `~/.pi/agent`。
- 技能页列出扫到的技能，MCP 页列出服务端并显示这一次真实握手的结果（配置在 ≠ 连上了）；
  加完文件点「重新扫描」，新配置在下一个会话生效。
- 技能页的「导入文件夹」选一个直接含 `SKILL.md` 的目录；「导入 zip」选一个 `.zip` 技能包。
  包里可以带一层外层目录，也可以把 `SKILL.md` 放在压缩包根上（用包名当技能名）；
  一个包只装一个技能，重名会拒绝，装完自动让会话重建。校验与解压在 `Storage/CapabilityInstaller`。
- MCP 页的「导入 JSON」认三种写法：`{"mcpServers": {...}}`、VS Code 的 `{"servers": {...}}`
  （含外面再套一层 `mcp`）、以及直接一层「名称 → 配置」。字段原样保留（`env` / `headers` /
  `url` / `cwd` 都不动），只做三件事：缺 `exposure` 补 `direct`、`npx` 这类命令补 Windows 启动层、
  校验能起得来；同名默认跳过，勾「覆盖同名」才替换。JSON 里的尾逗号和注释会被容忍。

## 内置工具

工具分两拨，但同一件事只留一个实现：读文件、列目录、搜内容交给 CodingAgent 的内置工具，
自建工具（`AvaloniaAssistant.Agent/Tools`）只补它没有的能力，一共 5 个。
系统信息、进程管理、在浏览器打开这类低频动作用 `run_command` 或系统自带工具做，
少一个工具就少一次选错。

| 工具 | 说明 | 权限 |
| --- | --- | --- |
| `write_file` | 写工作区文件（含追加、自动建父目录） | 工作区可写 / 只读拦截 |
| `open_path` | 用系统程序打开文件/文件夹 | 只读 |
| `run_command` | 执行 shell 命令（60s 超时） | 需确认 |
| `fetch_url` | 抓网页正文 | 只读 |
| `current_time` | 当前时间与时区 | 只读 |

CodingAgent 提供的内置工具（`tools` allowlist 里固定启用）：

| 工具 | 说明 | 权限 |
| --- | --- | --- |
| `read` | 按行读文件，技能正文靠它加载 | 只读 |
| `grep` | 正则搜内容，遵循 `.gitignore` | 只读 |
| `find` | glob 找文件，遵循 `.gitignore` | 只读 |
| `ls` | 列目录 | 只读 |
| `edit` | 最小替换改文件 | 需确认 / 只读模式不启用 |
| `write` | 新建或整写文件 | 需确认 / 仅完全访问 |
| `mcp__<server>__<tool>` | MCP 服务端暴露的工具 | 需确认 / 只读模式拦截 |

写文件为什么还留 `write_file`：内置 `write` 只在「完全访问」启用，默认权限下新建文件走 `write_file`，
它带工作区沙箱、支持追加，且在工作区内写入不弹确认；改已有文件的单行才用 `edit`。

路径默认限制在当前工作区；要越界需要切到「完全访问」。
`open_path` 不会直接启动 `.exe` / `.bat` / `.ps1` 等可执行文件；需要运行时走 `run_command` 并确认。

## 测试

```bash
dotnet test tests/AvaloniaAssistant.Agent.Tests
```

覆盖：

- Faux 模型驱动的完整 agent 循环与工具调用；
- 只读模式拦截、授权拒绝、缺 API Key；
- 切换权限后会话重建，只读模式立即生效；
- 项目级技能发现（真实 `SKILL.md` → system prompt）；
- MCP 端到端：测试里起一个 Node stdio 服务端，断言 `mcp__demo__echo` 进入可调用工具集；
- 本机 TCP 模拟 OpenAI SSE 端点，验证真实 HTTP 流式与工具声明；
- 文件工具的目录 / 读取 / 越界写入；
- 技能导入：文件夹与 zip（外层目录、根级 `SKILL.md`、缺清单、多技能、重名）各走一遍；
- settings / session 的 JSON 往返。

## 截图模式

用于 UI 回归和文档配图，不需要 API Key：

```bash
dotnet run --project src/AvaloniaAssistant.Desktop -- --screenshot out.png --page home
dotnet run --project src/AvaloniaAssistant.Desktop -- --screenshot out.png --page chat
dotnet run --project src/AvaloniaAssistant.Desktop -- --screenshot out.png --page settings
dotnet run --project src/AvaloniaAssistant.Desktop -- --screenshot out.png --page tools
```

`--page chat` 会加载一份内存里的示例会话（不写入 `sessions/`），用来检查消息、工具卡、表格渲染。

弹层（Popup / Flyout）挂在另一个 TopLevel 上，截主窗口是看不到的，单独有两个入口：

```bash
# 把工作区浮层摆进一个无边框窗口再截，检查列表 / 新建 / 搜索三种版式
dotnet run --project src/AvaloniaAssistant.Desktop -- --screenshot out.png --workspace-picker
dotnet run --project src/AvaloniaAssistant.Desktop -- --screenshot out.png --workspace-picker --workspace-picker-create
dotnet run --project src/AvaloniaAssistant.Desktop -- --screenshot out.png --workspace-picker --workspace-picker-query banana

# 在主窗口里真的打开浮层，让弹层自己出图（含 Flyout 外壳和真实定位）
dotnet run --project src/AvaloniaAssistant.Desktop -- --screenshot out.png --workspace-flyout
```

新增的几处界面也走同一套验收参数：

```bash
# 快捷键总览
dotnet run --project src/AvaloniaAssistant.Desktop -- --screenshot out.png --shortcuts

# 跨天消息：日期分隔与完整时间（--scroll-top 看最早一条）
dotnet run --project src/AvaloniaAssistant.Desktop -- --screenshot out.png --page chat --chat-dates --scroll-top

# 界面缩放 125%，设置页滚到底
dotnet run --project src/AvaloniaAssistant.Desktop -- --screenshot out.png --page settings --scroll-bottom --scale 1.25

# 授权卡版式；--press-escape 会真的发一次 Esc，验证「Esc 拒绝」
dotnet run --project src/AvaloniaAssistant.Desktop -- --screenshot out.png --page chat --approval-demo --press-escape --scroll-bottom

# 隔离数据目录：设置/会话都写到这里，验收启动恢复时不碰真实数据
dotnet run --project src/AvaloniaAssistant.Desktop -- --data-dir .tmp/verify
```

还提供一个开发用的自动发送参数，用来跑真实的端到端链路：

```bash
dotnet run --project src/AvaloniaAssistant.Desktop -- --run "用三句话介绍你自己" --screenshot out.png
```

它会加载 `%APPDATA%\AvaloniaAssistant\settings.json`，走完整的 Agent → HTTP → UI → 会话落盘流程。

## 数据目录

```
%APPDATA%\AvaloniaAssistant\
├── settings.json
├── sessions\<session-id>.json
└── Workspace\                  # 默认工作区
```

删除 `sessions\` 下的文件即可清空对应会话。设置页有「打开数据目录」。

## 已知边界

- **API Key 明文保存**在 `settings.json`，目前没有接 DPAPI；不要在共享机器上使用生产密钥。
- **语音转写依赖端点**支持 `/audio/transcriptions`；DeepSeek 官方端点不支持，会给出错误提示。
- **MCP 工具要写 `exposure: "direct"`**：不写就只有 codemode / tool_search 能碰到，模型直接调不到。
- **会话存档仍是本项目自己的 JSON**：CodingAgent 的会话跑在内存里，压缩结果不跨进程保存；
  重启后由 `sessions/*.json` 的历史重建上下文，压缩收益会重新累计。
- **上下文窗口按 128k 估算**：模型真实窗口不在本地目录里，压缩触发点据此计算，特殊模型需要手动调。
- **图片要模型支持视觉**：不支持的模型由 pi.net 降级成占位符，不会报错，但也看不到图。
- **@ 只按文件名和路径匹配**：不是全文检索，搜文件里的内容要等后面的索引；二进制文件可以引用但没有正文。
- **没有定时任务 / 专家市场**：导航里暂未提供入口，避免假按钮；扩展点见架构文档。
- **Windows 优先**：代码里对 macOS/Linux 的路径和 shell 做了分支，但只在 Windows 上验证过。

## 文档

- [两个参考项目的调研与集成结论](docs/research.md)
- [旧项目功能盘点与迁移决定](docs/migration.md)
- [架构、时序、权限与扩展点](docs/architecture.md)

## 许可证

MIT，见 [LICENSE](LICENSE)。
