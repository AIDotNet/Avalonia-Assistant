using System.Collections.ObjectModel;
using Avalonia.Media;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Arc;

namespace AvaloniaAssistant.UI.ViewModels;

public sealed partial class FileItemViewModel
{
    public FileItemViewModel(string name, string fullPath, bool isDirectory, string detail)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Detail = detail;
    }

    public string Name { get; }

    public string FullPath { get; }

    public bool IsDirectory { get; }

    public string Detail { get; }

    public Geometry Icon => IsDirectory ? ArcIcons.FolderOpen : ArcIcons.FileText;

    [RelayCommand]
    private void Open() => Ui.OpenExternal(FullPath);
}

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly MainWindowViewModel _host;

    public LibraryViewModel(MainWindowViewModel host)
    {
        _host = host;
        _ = RefreshAsync();
    }

    public ObservableCollection<FileItemViewModel> Items { get; } = [];

    public string WorkspacePath => _host.Settings.WorkspacePath;

    /// <summary>标题栏里显示的路径，用户目录缩写为 ~，避免长路径挤满一行。</summary>
    public string WorkspacePathDisplay
    {
        get
        {
            var path = WorkspacePath;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home) || string.IsNullOrEmpty(path))
            {
                return path;
            }

            return path.StartsWith(home, StringComparison.OrdinalIgnoreCase)
                ? "~" + path[home.Length..]
                : path;
        }
    }

    public string WorkspaceName
    {
        get
        {
            var path = WorkspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
    }

    [ObservableProperty]
    private bool isEmpty;

    [ObservableProperty]
    private string statusText = "正在读取…";

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Items.Clear();
        StatusText = "正在读取…";

        var workspace = WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            IsEmpty = true;
            StatusText = "还没有工作区";
            return;
        }

        try
        {
            var entries = new DirectoryInfo(workspace)
                .EnumerateFileSystemInfos()
                .OrderByDescending(info => info is DirectoryInfo)
                .ThenBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .ToList();

            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo directory)
                {
                    Items.Add(new FileItemViewModel(
                        directory.Name,
                        directory.FullName,
                        true,
                        $"文件夹 · 修改于 {directory.LastWriteTime:MM-dd HH:mm}"));
                }
                else if (entry is FileInfo file)
                {
                    Items.Add(new FileItemViewModel(
                        file.Name,
                        file.FullName,
                        false,
                        $"{FormatSize(file.Length)} · {file.LastWriteTime:MM-dd HH:mm}"));
                }
            }

            IsEmpty = Items.Count == 0;
            StatusText = IsEmpty ? "这个文件夹是空的" : $"{Items.Count} 个项目";
        }
        catch (Exception ex)
        {
            IsEmpty = true;
            StatusText = "读取失败：" + ex.Message;
        }

        await Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenWorkspace() => Ui.OpenExternal(WorkspacePath);

    [RelayCommand]
    private async Task PickWorkspaceAsync()
    {
        await _host.AddWorkspaceAsync();
        OnPropertyChanged(nameof(WorkspacePath));
        OnPropertyChanged(nameof(WorkspacePathDisplay));
        OnPropertyChanged(nameof(WorkspaceName));
        await RefreshAsync();
    }

    [RelayCommand]
    private void Back() => _host.OpenHome();

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }
}
