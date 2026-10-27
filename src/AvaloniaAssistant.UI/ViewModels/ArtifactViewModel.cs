using System.Collections.ObjectModel;
using Avalonia.Media;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaAssistant.UI.ViewModels;

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    Hunk,
}

/// <summary>
/// diff 里的一行。颜色在构造时定好，模板里直接绑，免得每行都跑一遍转换器。
/// </summary>
public sealed class DiffLineViewModel
{
    private static readonly IBrush AddedBackground = new SolidColorBrush(Color.Parse("#EAF4EA"));
    private static readonly IBrush RemovedBackground = new SolidColorBrush(Color.Parse("#FBEAE8"));
    private static readonly IBrush AddedForeground = new SolidColorBrush(Color.Parse("#2E7D32"));
    private static readonly IBrush RemovedForeground = new SolidColorBrush(Color.Parse("#B3261E"));
    private static readonly IBrush ContextForeground = new SolidColorBrush(Color.Parse("#6F6F69"));
    private static readonly IBrush HunkForeground = new SolidColorBrush(Color.Parse("#6F6F69"));

    public DiffLineViewModel(string text, DiffLineKind kind)
    {
        Text = text;
        Kind = kind;
        Foreground = kind switch
        {
            DiffLineKind.Added => AddedForeground,
            DiffLineKind.Removed => RemovedForeground,
            DiffLineKind.Hunk => HunkForeground,
            _ => ContextForeground,
        };
        Background = kind switch
        {
            DiffLineKind.Added => AddedBackground,
            DiffLineKind.Removed => RemovedBackground,
            _ => Brushes.Transparent,
        };
    }

    public string Text { get; }

    public DiffLineKind Kind { get; }

    public IBrush Foreground { get; }

    public IBrush Background { get; }

    public bool IsHunk => Kind == DiffLineKind.Hunk;
}

/// <summary>
/// 产物面板里的一行：哪个文件、怎么改的、点开看逐行差异。
/// 它替用户回答的是「这次到底动了什么」，而不是把工具调用再列一遍。
/// </summary>
public sealed partial class ArtifactViewModel : ObservableObject
{
    private readonly Action<string> _report;

    public ArtifactViewModel(FileChangeRecord record, Action<string> report)
    {
        Record = record;
        _report = report;
        Lines = BuildLines(record.Diff);
    }

    public FileChangeRecord Record { get; }

    public string FileName => Record.FileName;

    public string FullPath => Record.FullPath;

    public string DisplayPath => Record.Path;

    public string Folder => Record.Folder;

    public bool HasFolder => Folder.Length > 0;

    public string KindText => Record.KindText;

    public string ChangeText => Record.ChangeText;

    public string SizeText => Record.SizeText;

    public string Tool => Record.Tool;

    public bool HasDiff => Record.HasDiff;

    public bool HasSize => Record.SizeText.Length > 0;

    /// <summary>悬停提示：完整路径 + 是哪个工具写的。</summary>
    public string Detail => Tool.Length == 0
        ? Record.FullPath
        : $"{Record.FullPath}{Environment.NewLine}由 {Tool} 写入";

    /// <summary>创建、修改、删除三种用不同的颜色角标，扫一眼就能分出来。</summary>
    public IBrush KindBrush => Record.Kind switch
    {
        FileChangeKind.Created => new SolidColorBrush(Color.Parse("#2E7D32")),
        FileChangeKind.Deleted => new SolidColorBrush(Color.Parse("#B3261E")),
        _ => new SolidColorBrush(Color.Parse("#6F6F69")),
    };

    public ObservableCollection<DiffLineViewModel> Lines { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandHint))]
    private bool isExpanded;

    public string ExpandHint => IsExpanded ? "收起差异" : "查看逐行差异";

    [RelayCommand]
    private void Toggle()
    {
        if (!HasDiff)
        {
            return;
        }

        IsExpanded = !IsExpanded;
    }

    /// <summary>
    /// 能不能在应用内看一眼内容。office 文档、PDF 和纯文本可以；
    /// 图片和二进制走「打开」，塞进文本框只会是一堆乱码。
    /// </summary>
    public bool CanPreview => Ui.CanPreview(FullPath);

    [RelayCommand]
    private async Task PreviewAsync()
    {
        if (File.Exists(FullPath) && await Ui.PreviewDocumentAsync(FullPath))
        {
            return;
        }

        _report(File.Exists(FullPath)
            ? $"这个文件没法在应用内预览，试试「打开」：{DisplayPath}"
            : $"文件已经不在了：{FullPath}");
    }

    [RelayCommand]
    private void Open()
    {
        if (File.Exists(FullPath))
        {
            Ui.OpenExternal(FullPath);
            return;
        }

        _report($"文件已经不在了：{FullPath}");
    }

    [RelayCommand]
    private void Reveal()
    {
        if (!Ui.RevealInExplorer(FullPath))
        {
            _report($"打不开所在目录：{FullPath}");
        }
    }

    [RelayCommand]
    private async Task CopyPathAsync() => await Ui.CopyTextAsync(FullPath);

    private static ObservableCollection<DiffLineViewModel> BuildLines(string? diff)
    {
        var lines = new ObservableCollection<DiffLineViewModel>();
        if (string.IsNullOrWhiteSpace(diff))
        {
            return lines;
        }

        foreach (var raw in diff.Split('\n'))
        {
            var text = raw.TrimEnd('\r');

            // 空行是两段差异之间的分隔，给个占位高度，不然会挤成一条缝
            if (text.Length == 0)
            {
                lines.Add(new DiffLineViewModel(" ", DiffLineKind.Context));
                continue;
            }

            var kind = text[0] switch
            {
                '+' => DiffLineKind.Added,
                '-' => DiffLineKind.Removed,
                '@' => DiffLineKind.Hunk,
                _ => DiffLineKind.Context,
            };

            lines.Add(new DiffLineViewModel(text, kind));
        }

        return lines;
    }
}
