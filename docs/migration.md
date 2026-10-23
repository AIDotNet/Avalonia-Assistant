# 旧项目功能盘点与迁移决定

旧仓库在 `scr/` 下是 net8 + ReactiveUI 的四个平台头（Desktop / Browser / Android / iOS），
核心代码约 2600 行。下面是逐项盘点和处理方式。

## 1. 功能清单

| # | 旧功能 | 旧实现位置 | 结论 | 新实现 |
| --- | --- | --- | --- | --- |
| 1 | ChatGPT 对话 | `ViewModels/ChatViewModel.ChatGPT` + `Microsoft.SemanticKernel` | **迁移** | `PiAgentRuntime` + `OpenAiCompletions.StreamSimple`，支持流式 |
| 2 | Agent 模式 | `ChatViewModel.AIAgent` + `HandlebarsPlanner` | **替换** | pi.net 原生 tool-calling 循环，不再先生成 Handlebars 计划 |
| 3 | 电脑信息（OS / CPU / 内存 / 磁盘） | `NativePlugins/ComputerPlugin`（WMI） | **迁移** | `system_info`（RuntimeInformation + Win32 内存 API + DriveInfo） |
| 4 | 结束进程 | `NativePlugins/SystemPlugin` | **迁移 + 加护栏** | `list_processes` / `stop_process`，结束进程必须逐次授权 |
| 5 | 打开文件夹 / 文件 | `NativePlugins/FilePlugin` | **迁移** | `open_path` |
| 6 | 搜索文件 | `FilePlugin.SearchFile` + 内置 `es.exe` | **迁移 + 去依赖** | `search_files`，纯 .NET 枚举，支持文件名 / 内容 |
| 7 | 清理垃圾文件 | `FilePlugin.ClearJunkFile`（生成并执行 bat，`del /f /s /q`） | **不迁移** | 破坏性太强、误删风险高；保留 `run_command` 让用户在明确授权下自己做 |
| 8 | Chrome 打开 / 百度搜索抓取 | `NativePlugins/ChromePlugin`（Selenium + ChromeDriver） | **替换** | `open_url` + `fetch_url`；去掉 Selenium 与 118 版 ChromeDriver 依赖 |
| 9 | 抓 Avalonia 文档 | `NativePlugins/AvaloniaPlugin` | **泛化** | `fetch_url`，任意 http/https 页面转正文 |
| 10 | 本地 GGUF 对话 | `StatefulChatService` + LLamaSharp + tinyllama | **替换** | 设置里选“自定义”，指向 Ollama / LM Studio / vLLM 等 OpenAI 兼容端点 |
| 11 | 语音输入 | `AudioRecorder` + Whisper.net + `ggml-base-q5_1.bin` | **迁移 + 换实现** | NAudio 录音 + `/audio/transcriptions`；不再捆绑 140MB 模型 |
| 12 | 会话状态 | ReactiveUI `SuspensionHost` + `appstate.json` | **替换** | 一个会话一个 JSON 文件，含消息、工具调用、token 用量 |
| 13 | 多平台头 | Desktop / Browser / Android / iOS | **收缩** | 先做 Windows 桌面；Avalonia 本身仍可再加头 |
| 14 | 深色 Fluent、400×700 固定窗口 | `App.axaml` + `MainWindow.axaml` | **替换** | Arc 浅色主题、1280×820、可缩放、WorkBuddy 风格三栏 |

> 2026-10-23 调整：按日常办公 / 编码的实际使用，`system_info`、`list_processes`、
> `stop_process`、`open_url` 四个工具已从注册表移除，需要时走 `run_command` 或系统自带工具；
> 上表保留当时的迁移结论。

## 2. 新增的能力

旧项目没有、但这次为了“能日常用”补上的：

- **三档权限**：只读 / 工作区 / 完全访问；写文件和执行命令有明确边界。
- **逐次授权卡片**：`run_command` 在对话里弹“允许一次 / 拒绝”，拒绝后模型会收到原因。
- **工具卡片**：参数、结果、耗时、成功/失败都留在对话里，可展开查看。
- **思考过程**：DeepSeek Reasoner / Claude 的 thinking 增量折叠展示。
- **工作区沙箱**：文件工具默认只能碰工作区，越界需要“完全访问”。
- **会话侧边栏**：按今天 / 昨天 / 最近 7 天 / 更早分组，可删除、可恢复。
- **模型切换与多 provider**：DeepSeek / OpenAI / Anthropic / 任意 OpenAI 兼容端点。
- **技能页**：8 个内置工具可见、可开关，可从文件夹或 zip 导入技能。
- **资料库**：直接浏览当前工作区顶层文件。
- **Markdown 渲染**：表格、代码块、列表、行内代码，使用 Arc 的 `Markdown` 控件。
- **离线截图模式**：`--screenshot` / `--page`，用于 UI 回归和文档配图。

## 3. 明确没做的

| 项 | 原因 | 后续怎么加 |
| --- | --- | --- |
| 定时任务 | 需要后台调度、错过补偿、通知；不是本轮核心 | 新增 `ScheduledTask` 模型 + `PeriodicTimer` 宿主，复用 `IAssistantRuntime` |
| 专家 / 连接器市场 | 需要远程目录、安装、鉴权 | 在 `Tools` 之上加 `ISkillPack` / `IMcpServer` 注册 |
| 会话压缩 | 旧项目用 `ConversationSummaryPlugin`；新项目直接带全量历史 | 在 `ChatViewModel.BuildHistory` 前做摘要或截断 |
| 多平台头 | Windows 桌面优先 | 复用 `AssistantApp`，补 Browser/Android head |
| 密钥加密 | 当前明文存 `settings.json` | 接 DPAPI / Credential Manager，或改成系统钥匙串 |
| 会话内重命名 / 搜索 | 当前只按更新时间排序 | 侧边栏加搜索框与重命名命令 |

## 4. 旧代码里刻意没照搬的东西

- **`OSExtensions.ImportPluginFromObjectByOs`**：用反射按 `[OS]` 特性导入 SK 插件。
  新架构不需要反射，工具在 `AgentToolRegistry.DefaultTools()` 里显式注册，AOT 友好。
- **`HandlebarsPlanner`**：规划质量依赖 prompt，且多一轮模型调用；原生 tool calling 更直接。
- **`StatefulChatService` 的 `using var weights`**：旧代码在构造函数里 `using` 掉权重，属于生命周期 bug；
  新实现不再自己管模型权重。
- **`appstate.json` 的整体视图模型序列化**：会话和 UI 状态耦合，升级容易炸；改成显式的 `ChatSession` DTO。
- **`FilePlugin.ClearJunkFile` 的 bat**：路径里有中文引号、`%systemdrive%` 全盘删除，风险不可控。
