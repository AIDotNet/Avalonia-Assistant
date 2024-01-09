using System;
using System.Management;

// 查询计算机系统信息
ManagementObjectSearcher osSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem");
foreach (ManagementObject os in osSearcher.Get())
{
    Console.WriteLine("操作系统版本: " + os["Caption"]);
    Console.WriteLine("操作系统位数: " + os["OSArchitecture"]);
    Console.WriteLine("注册人: " + os["RegisteredUser"]);
    Console.WriteLine("安装日期: " + ManagementDateTimeConverter.ToDateTime(os["InstallDate"].ToString()).ToString("yyyy-MM-dd HH:mm:ss"));
}

// 查询处理器信息
ManagementObjectSearcher processorSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor");
foreach (ManagementObject processor in processorSearcher.Get())
{
    Console.WriteLine("处理器名称: " + processor["Name"]);
    Console.WriteLine("处理器核心数: " + processor["NumberOfCores"]);
    Console.WriteLine("处理器线程数: " + processor["NumberOfLogicalProcessors"]);
    Console.WriteLine("处理器制造商: " + processor["Manufacturer"]);
}

// 查询物理内存信息
ManagementObjectSearcher memorySearcher = new ManagementObjectSearcher("SELECT * FROM Win32_PhysicalMemory");
foreach (ManagementObject memory in memorySearcher.Get())
{
    Console.WriteLine("物理内存容量: " + Math.Round(Convert.ToDouble(memory["Capacity"]) / (1024 * 1024 * 1024), 2) + " GB");
    Console.WriteLine("制造商: " + memory["Manufacturer"]);
    Console.WriteLine("型号: " + memory["PartNumber"]);
    Console.WriteLine("速度: " + memory["Speed"] + " MHz");
}

// 查询磁盘驱动器信息
ManagementObjectSearcher driveSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_LogicalDisk");
foreach (ManagementObject drive in driveSearcher.Get())
{
    Console.WriteLine("磁盘驱动器: " + drive["Name"]);
    Console.WriteLine("磁盘空间总量: " + Math.Round(Convert.ToDouble(drive["Size"]) / (1024 * 1024 * 1024), 2) + " GB");
    Console.WriteLine("磁盘空闲空间: " + Math.Round(Convert.ToDouble(drive["FreeSpace"]) / (1024 * 1024 * 1024), 2) + " GB");
}

Console.ReadLine();