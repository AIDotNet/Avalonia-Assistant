using System.Diagnostics;

try
{
    string folderPath = @"D:\"; // 要打开的文件夹路径
    Process.Start("explorer.exe", folderPath);
}
catch (Exception ex)
{
    Console.WriteLine("无法打开文件夹：{0}", ex.Message);
}