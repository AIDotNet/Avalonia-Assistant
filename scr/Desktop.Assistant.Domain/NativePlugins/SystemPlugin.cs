using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace Desktop.Assistant.Domain.NativePlugins
{
    /// <summary>
    /// 系统相关插件
    /// </summary>
    [OS(OSType.All)]
    public class SystemPlugin
    {
        [KernelFunction, Description("关闭进程")]
        public string StopProcess([Description(@"应用程序名，例如： WeChat ")] string processesName)
        {
            try
            {
                // 查找微信进程
                Process[] processes = Process.GetProcessesByName(processesName);

                // 关闭找到的微信进程
                foreach (Process process in processes)
                {
                    process.CloseMainWindow();
                    process.WaitForExit();    
                }
                return $"关闭成功:{processesName}";
            }
            catch (Exception ex)
            {
                return $"无法关闭：{ex.Message}";
            }
        }
    }
}
