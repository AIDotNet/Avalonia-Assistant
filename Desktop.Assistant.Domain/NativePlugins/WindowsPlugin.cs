﻿using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Desktop.Assistant.Domain.NativePlugins
{
    /// <summary>
    /// windows相关插件
    /// </summary>
    public class WindowsPlugin
    {
        private readonly Kernel _kernel;
      
        public WindowsPlugin(Kernel kernel)
        {
            _kernel = kernel;
        }

        [KernelFunction, Description("清理垃圾文件")]
        public void ClearJunkFile( )
        {
            // 设定你想要创建和运行的BAT文件的路径和名称
            string batFileName = $"batfile.bat";

            // 创建一些简单的批处理命令
            string[] batchCommands = {
          @"@echo off",
@"echo 正在清除系统垃圾文件，请稍等。。。。。。",
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
@"echo 清除系统LJ完成！",

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
    }
}