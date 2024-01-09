using HandlebarsDotNet.StringUtils;
using Microsoft.SemanticKernel;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading.Tasks;

namespace Desktop.Assistant.Domain.NativePlugins
{
    [OS(OSType.Windows)]
    public class ComputerPlugin
    {
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

        [KernelFunction, Description("查询处理器信息")]
        public string QueryCPUInformation()
        {
            StringBuilder info = new StringBuilder(); 
            ManagementObjectSearcher processorSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor");
            foreach (ManagementObject processor in processorSearcher.Get())
            {
                info.AppendLine("处理器名称: " + processor["Name"]);
                info.AppendLine("处理器核心数: " + processor["NumberOfCores"]);
                info.AppendLine("处理器线程数: " + processor["NumberOfLogicalProcessors"]);
                info.AppendLine("处理器制造商: " + processor["Manufacturer"]);
            }
            return info.ToString();
        }

        [KernelFunction, Description("查询物理内存信息")]
        public string QueryMemroyInformation()
        {
            StringBuilder info = new StringBuilder();
            ManagementObjectSearcher memorySearcher = new ManagementObjectSearcher("SELECT * FROM Win32_PhysicalMemory");
            foreach (ManagementObject memory in memorySearcher.Get())
            {
                info.AppendLine("物理内存容量: " + Math.Round(Convert.ToDouble(memory["Capacity"]) / (1024 * 1024 * 1024), 2) + " GB");
                info.AppendLine("制造商: " + memory["Manufacturer"]);
                info.AppendLine("型号: " + memory["PartNumber"]);
                info.AppendLine("速度: " + memory["Speed"] + " MHz");
            }
            return info.ToString();
        }

        [KernelFunction, Description("查询磁盘驱动器信息")]
        public string QueryDiskInformation()
        {
            StringBuilder info = new StringBuilder();
            ManagementObjectSearcher driveSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_LogicalDisk");
            foreach (ManagementObject drive in driveSearcher.Get())
            {
                info.AppendLine("磁盘驱动器: " + drive["Name"]);
                info.AppendLine("磁盘空间总量: " + Math.Round(Convert.ToDouble(drive["Size"]) / (1024 * 1024 * 1024), 2) + " GB");
                info.AppendLine("磁盘空闲空间: " + Math.Round(Convert.ToDouble(drive["FreeSpace"]) / (1024 * 1024 * 1024), 2) + " GB");
            }

            return info.ToString();
        }
    }
}
