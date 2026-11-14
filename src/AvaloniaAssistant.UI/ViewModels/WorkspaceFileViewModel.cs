using Avalonia.Media;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Arc;

namespace AvaloniaAssistant.UI.ViewModels;

/// <summary>
/// 结果区「全部文件」里的一行。照 WorkBuddy 的三区布局：产物说这次改了什么，
/// 全部文件说工作区里到底有什么，点一下就能打开或预览。
/// 只读列举，不写文件；目录行只负责在资源管理器里定位。
/// </summary>
public sealed partial class WorkspaceFileViewModel : ObservableObject
{
    private readonly Action<string> _report;

    public WorkspaceFileViewModel(string fullPath, bool isDirectory, Action<string> report)
    {
        FullPath = fullPath;
        IsDirectory = isDirectory;
        _report = report;

        if (!isDirectory)
        {
            try
            {
                SizeBytes = new FileInfo(fullPath).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SizeBytes = 0;
            }
        }
    }

    public string FullPath { get; }

    public bool IsDirectory { get; }

    public long SizeBytes { get; }

    public string Name => Path.GetFileName(FullPath);

    public string Folder { get; init; } = string.Empty;

    public bool HasFolder => Folder.Length > 0;

    public Geometry Icon => IsDirectory ? ArcIcons.Folder : ArcIcons.FileText;

    public string SizeText
    {
        get
        {
            if (IsDirectory || SizeBytes <= 0)
            {
                return string.Empty;
            }

            return SizeBytes switch
            {
                < 1024 => $"{SizeBytes} B",
                < 1024 * 1024 => $"{SizeBytes / 1024.0:0.#} KB",
                < 1024L * 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024):0.#} MB",
                _ => $"{SizeBytes / (1024.0 * 1024 * 1024):0.#} GB",
            };
        }
    }

    public bool CanPreview => !IsDirectory && Ui.CanPreview(FullPath);

    public string Detail => IsDirectory
        ? $"{FullPath}{Environment.NewLine}文件夹"
        : $"{FullPath}{Environment.NewLine}{SizeText}";

    [RelayCommand]
    private void Open()
    {
        if (!Ui.OpenExternal(FullPath))
        {
            _report($"打不开：{FullPath}");
        }
    }

    [RelayCommand]
    private async Task PreviewAsync()
    {
        if (File.Exists(FullPath) && await Ui.PreviewDocumentAsync(FullPath))
        {
            return;
        }

        _report(File.Exists(FullPath)
            ? $"这个文件没法在应用内预览，试试「打开」：{Name}"
            : $"文件已经不在了：{FullPath}");
    }

    [RelayCommand]
    private void Reveal()
    {
        if (!Ui.RevealInExplorer(FullPath))
        {
            _report($"打不开所在目录：{FullPath}");
        }
    }
}
