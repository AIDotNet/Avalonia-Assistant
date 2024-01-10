# Avalonia-Assistant

Avalonia-Assistant 是一个开源的桌面智能助理，旨在提供一个基于 Avalonia UI 框架和 Semantic Kernel 与 OpenAI 集成的友好交互体验。通过使用 Avalonia-Assistant，您可以通过文本或语音命令来执行各种桌面操作，从而增强您的工作效率和日常办公体验。

## 特性

- **文本和语音命令**: 支持通过文本和语音命令与助理互动。
- **垃圾文件清理**: 智能识别并清理系统中的垃圾文件。
- **文件管理**: 方便地打开和管理本地文件。
- **浏览器支持**: 通过语音指令快速打开您的默认网络浏览器。
- **日历查看**: 检查您的日程安排并管理日历。
- **邮件操作**: 发送和管理邮件。
- **其他**：还有很多，我们只要把SK的Plugin封装，理论上都可以去让AI助理完成。

## 开始使用

在您的本机上开始使用 Avalonia-Assistant 很简单。您需要遵循以下步骤：

克隆这个仓库:
   ```sh
   git clone https://github.com/xuzeyu91/Avalonia-Assistant.git
  ```
使用IDE打开，启动。

或者执行命令

`dotnet build && dotnet run --project Desktop.Assistant/Desktop.Assistant.csproj` 

填入你的OpenAI Key 

然后开始使用吧！

假如你没有OpenAI,想用国内模型，那么你需要另一个项目
```
https://github.com/songquanpeng/one-api
```


## 示例
![界面](https://github.com/xuzeyu91/Avalonia-Assistant/blob/main/img/cpu.png?raw=true)

例如这个图，通过本地插件就能获取我的电脑相关信息。
```
 [KernelFunction, Description("查询计算机系统信息")]
 public string QueryComputerSystemInformation()
 {
     StringBuilder info = new StringBuilder(); 
     // 查询计算机系统信息
     ManagementObjectSearcher osSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem");
     foreach (ManagementObject os in osSearcher.Get())
     {
         info.AppendLine("操作系统版本: " + os["Caption"]);
         info.AppendLine("操作系统位数: " + os["OSArchitecture"]);
         info.AppendLine("注册人: " + os["RegisteredUser"]);
         info.AppendLine("安装日期: " + ManagementDateTimeConverter.ToDateTime(os["InstallDate"].ToString()).ToString("yyyy-MM-dd HH:mm:ss"));
     }
     return info.ToString();
 }
……
```

我还会继续探索，客户端助手有哪些有趣的场景！

## 如何贡献
我们欢迎任何形式的贡献，无论是通过提供反馈、报告问题还是通过发送拉取请求来添加新功能或修复错误。如果你想为 Avalonia-Assistant 做出贡献，请遵循以下步骤：
Fork 这个仓库。
创建你的特性分支 (git checkout -b feature/AmazingFeature)。
提交你的更改 (git commit -m 'Add some AmazingFeature')。
推送到分支 (git push origin feature/AmazingFeature)。
打开一个拉取请求。
许可证
该项目使用 MIT LICENSE 下的许可证。请查看 LICENSE.md 文件以了解更多信息。

## 联系我
如有任何问题或建议，请通过以下方式关注我的公众号，可以发送进群等消息，然后我会拉你进交流群
![公众号](https://raw.githubusercontent.com/xuzeyu91/semantic-kernel-aiagent/main/gzh.jpg)
