using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Desktop.Assistant.Domain.NativePlugins
{
    [OS(OSType.Windows)]
    public class FilePlugin
    {
        [KernelFunction, Description("清理垃圾文件")]
        public void ClearJunkFile()
        {
            // 设定你想要创建和运行的BAT文件的路径和名称
            string batFileName = $"batfile.bat";

            // 创建一些简单的批处理命令
            string[] batchCommands = {
          @"@echo off",
@"echo Clearing system junk files, please wait......",
@"del /f /s /q %systemdrive%\*.tmp",
@"del /f /s /q %systemdrive%\*._mp",
@"del /f /s /q %systemdrive%\*.log",
@"del /f /s /q %systemdrive%\*.gid",
@"del /f /s /q %systemdrive%\*.chk",
@"del /f /s /q %systemdrive%\*.old",
@"del /f /s /q %systemdrive%\recycled\.",
@"del /f /s /q %windir%\*.bak",
@"del /f /s /q %windir%\prefetch\.",
@"rd /s /q %windir%\temp & md %windir%\temp",
@"del /f /q %userprofile%\cookies\.",
@"del /f /q %userprofile%\recent\.",
@"del /f /s /q “%userprofile%\Local Settings\Temporary Internet Files\.”",
@"del /f /s /q “%userprofile%\Local Settings\Temp\.”",
@"del /f /s /q “%userprofile%\recent\.”",
@"echo Clear completed!",

        };

            // 将命令写入到BAT文件中
            File.WriteAllLines(batFileName, batchCommands);

            // 初始化新的ProcessStartInfo实例
            ProcessStartInfo processInfo = new ProcessStartInfo(batFileName)
            {
                // 这里可以设置运行BAT文件时的一些参数
                UseShellExecute = true, // 使用shell执行
                CreateNoWindow = false, // 是否应用程序窗口可见
                Verb = "runas" // 提供管理员权限，如果需要的话
            };

            // 创建Process实例并执行
            try
            {
                Process batProcess = Process.Start(processInfo);
                batProcess.WaitForExit(); // 等待BAT文件执行完成
            }
            catch (Exception exc)
            {
                Console.WriteLine($"An error occurred: {exc.Message}");
            }
        }

        [KernelFunction, Description("打开文件夹")]
        public string OpenDir([Description(@"文件夹路径，例如：D:\ ")] string folderPath)
        {
            try
            {

                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start("open", folderPath);
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Process.Start("explorer.exe", folderPath);
                }

                return $"打开成功:{folderPath}";
            }
            catch (Exception ex)
            {
                return $"打开失败：{ex.Message}";
            }
        }



        [KernelFunction, Description("打开文件")]
        public string OpenFile([Description("文件路径，例如：D:\\file.txt ")] string filePath)
        {
            try 
            {
                Process.Start(filePath);
                return $"打开成功:{filePath}";
            }
            catch (Exception ex)
            {
                return $"打开失败：{ex.Message}";
            }
        }

        [KernelFunction, Description("搜索文件")]
        public string SearchFile([Description("搜索关键字")] string key)
        {
            try 
            {

               string files=  RunCmd(key);
               return files;
            }
            catch (Exception ex)
            {
                return $"搜索异常:{ex.Message}";
            }
        }

        private string RunCmd(string name)
        {

            using (Process process = new Process())
            {
                // 指定要执行的命令和参数
                process.StartInfo.FileName = "es\\es.exe";
                process.StartInfo.Arguments = name; // 这里演示执行"dir"命令

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

                return output;
            }
        }
    }
}
