using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using AvaloniaAssistant.UI.ViewModels;

namespace AvaloniaAssistant.UI.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private LibraryViewModel? ViewModel => DataContext as LibraryViewModel;

    /// <summary>
    /// 只接文件、不接文件夹：资料库收的是「能被引用的东西」，
    /// 整个目录拖进来会变成一堆看不出关系的条目。
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = LocalFiles(e).Count > 0;

        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (hasFiles && ViewModel is { } viewModel)
        {
            viewModel.IsDragging = true;
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.IsDragging = false;
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var target = ViewModel;
        if (target is null)
        {
            return;
        }

        target.IsDragging = false;

        var paths = LocalFiles(e);
        if (paths.Count == 0)
        {
            return;
        }

        await target.ImportPathsAsync(paths);
    }

    private static List<string> LocalFiles(DragEventArgs e)
    {
        // Avalonia 12 换了拖拽数据模型：DragEventArgs.Data 不再存在，改成了 IDataTransfer
        var items = e.DataTransfer?.TryGetFiles();
        if (items is null)
        {
            return [];
        }

        var paths = new List<string>();
        foreach (var item in items.OfType<IStorageFile>())
        {
            var path = item.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }
}
