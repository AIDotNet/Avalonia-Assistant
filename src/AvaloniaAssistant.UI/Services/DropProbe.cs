using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using AvaloniaAssistant.UI.ViewModels;
using AvaloniaAssistant.UI.Views;

namespace AvaloniaAssistant.UI.Services;

/// <summary>
/// 开发用：把一次「已经拖到输入框上」的事件直接投给界面，验证接线。
///
/// 真实拖拽要真人按着鼠标走 OLE 那条路，命令行里没法可靠模拟；Avalonia 12 也把
/// 平台输入管线关起来了，灌不进 RawDragEvent。这里退一步：事件投在输入框元素上，
/// 剩下的路由、窗口上的处理器、提示层、加文件/加图片都是真的。
/// 唯一没覆盖到的是 Avalonia 自己对 AllowDrop/命中测试的判断（见 MainWindow.Drop.cs 的说明）。
/// </summary>
internal static class DropProbe
{
    public static async Task RunAsync(Window window, IReadOnlyList<string> paths)
    {
        var composer = window.GetVisualDescendants().OfType<ComposerView>().FirstOrDefault();
        if (composer?.DataContext is not ComposerViewModel viewModel)
        {
            Console.WriteLine("[drop-probe] 当前页面没有输入框");
            return;
        }

        if (composer.TranslatePoint(
                new Point(composer.Bounds.Width / 2, composer.Bounds.Height / 2),
                window) is not { } point)
        {
            Console.WriteLine("[drop-probe] 输入框不在可视树里");
            return;
        }

        Console.WriteLine($"[drop-probe] 落下前：引用={viewModel.References.Count} 图片={viewModel.Images.Count}");
        Console.WriteLine($"[drop-probe] 落点（窗口坐标）：{point.X:0},{point.Y:0}");

        await DropOnceAsync(window, composer, viewModel, point, paths, "第一次");

        // 同一批文件再拖一次：引用是去重的，应该一个都不加，并且给一句说明
        await DropOnceAsync(window, composer, viewModel, point, paths, "第二次（重复）");
    }

    private static async Task DropOnceAsync(
        Window window,
        ComposerView composer,
        ComposerViewModel viewModel,
        Point point,
        IReadOnlyList<string> paths,
        string round)
    {
        var transfer = new DataTransfer();
        foreach (var path in paths)
        {
            var file = await window.StorageProvider.TryGetFileFromPathAsync(path);
            if (file is null)
            {
                Console.WriteLine($"[drop-probe] 找不到文件：{path}");
                continue;
            }

            transfer.Add(DataTransferItem.CreateFile(file));
        }

        if (transfer.Items.Count == 0)
        {
            Console.WriteLine("[drop-probe] 没有可拖的文件");
            return;
        }

        Raise(composer, DragDrop.DragEnterEvent, point, transfer);
        Raise(composer, DragDrop.DragOverEvent, point, transfer);
        Console.WriteLine($"[drop-probe] {round} · 悬停：提示层={composer.IsDropHintVisible} 文案='{composer.DropHintTitle}'");

        Raise(composer, DragDrop.DropEvent, point, transfer);
        await Task.Delay(1000);

        Console.WriteLine(
            $"[drop-probe] {round} · 落下：引用={viewModel.References.Count} 图片={viewModel.Images.Count} "
            + $"提示层={composer.IsDropHintVisible} 提示='{viewModel.DropNotice}'");
    }

    private static void Raise(
        Interactive target,
        RoutedEvent<DragEventArgs> routedEvent,
        Point point,
        IDataTransfer transfer)
    {
        var args = new DragEventArgs(routedEvent, transfer, target, point, KeyModifiers.None)
        {
            RoutedEvent = routedEvent,
            DragEffects = DragDropEffects.Copy,
        };

        target.RaiseEvent(args);
    }
}
