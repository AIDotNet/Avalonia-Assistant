using System;
using System.Diagnostics;
using System.Management;

// 创建一个进程对象
Process process = new Process();

// 指定要执行的命令和参数
process.StartInfo.FileName = "es.exe";
process.StartInfo.Arguments = "devops ppt"; // 这里演示执行 "es.exe search keyword" 命令

// 配置进程启动选项
process.StartInfo.UseShellExecute = false;
process.StartInfo.RedirectStandardOutput = true;
process.StartInfo.CreateNoWindow = true;

// 启动进程
process.Start();

// 读取输出结果
string output = process.StandardOutput.ReadToEnd();

// 等待命令执行完毕
process.WaitForExit();

// 输出结果
Console.WriteLine(output);