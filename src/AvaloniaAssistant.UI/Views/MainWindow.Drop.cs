using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaAssistant.UI.Services;
using AvaloniaAssistant.UI.ViewModels;

namespace AvaloniaAssistant.UI.Views;

/// <summary>
/// 拖放接线。
///
/// 之前是输入框自己开着 AllowDrop 接拖放，问题是 Avalonia 的拖放设备只会把事件发给
/// 「鼠标命中的最近一个 Interactive 元素」：落在文字上就是 TextPresenter、落在按钮上就是按钮，
/// 事件从那个元素往上冒泡。只要中间任何一层把事件吃掉，输入框就什么都收不到，
/// 表现就是「拖上去一点反应都没有」。而这一层是谁、什么版本会吃，控制不了。
///
/// 现在改成整个窗口当落点：窗口的 AllowDrop 会向下继承，命中任何子元素都算允许，
/// 处理器挂在窗口上（冒泡末尾 + handledEventsToo），内部控件先吃掉也照样轮到我们，
/// 再由我们自己判断落点在不在当前页面里。
/// </summary>
public partial class MainWindow
{
    private DispatcherTimer? _dropHintTimer;

    /// <summary>这一轮拖拽落在哪个输入框上。拖拽过程中 DragOver 一秒来几十次，别每次都去走一遍可视树。</summary>
    private (ComposerView View, ComposerViewModel Composer)? _dropTarget;

    private void InitializeDragDrop()
    {
        DragDrop.SetAllowDrop(this, true);

        AddHandler(DragDrop.DragEnterEvent, OnDropDragEnter, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnDropDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnDropDragLeave, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDropDropped, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnDropDragEnter(object? sender, DragEventArgs e)
    {
        CancelDropHintTimer();
        _dropTarget = FindComposer();
    }

    /// <summary>
    /// 鼠标每跨过一个子控件，拖放设备就会先发一次 DragLeave 再发 DragEnter，
    /// 收到 DragLeave 立刻收起提示会让提示在输入框里一路闪。压后一点收，中间来了新的拖拽就取消。
    /// </summary>
    private void OnDropDragLeave(object? sender, DragEventArgs e)
    {
        _dropHintTimer ??= CreateDropHintTimer();
        _dropHintTimer.Stop();
        _dropHintTimer.Start();
    }

    private DispatcherTimer CreateDropHintTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _dropTarget?.View.HideDropHint();
            _dropTarget = null;
        };
        return timer;
    }

    private void CancelDropHintTimer() => _dropHintTimer?.Stop();

    private void OnDropDragOver(object? sender, DragEventArgs e)
    {
        CancelDropHintTimer();

        // DragEnter 没收到（切页、浮层刚关）时补一次，别让第一下 DragOver 白跑
        _dropTarget ??= FindComposer();
        if (_dropTarget is not { } target)
        {
            return;
        }

        // 拖的是别处复制的文字：不接管，输入框自己的拖放还要用
        var payload = ComposerDropPayload.Read(e.DataTransfer);
        if (!payload.Accepts && payload.Folders == 0)
        {
            target.View.HideDropHint();
            return;
        }

        if (!IsInsidePage(e))
        {
            target.View.HideDropHint();
            return;
        }

        if (payload.Accepts)
        {
            var (title, hint) = DescribeDrop(payload);
            e.DragEffects = DragDropEffects.Copy;
            target.View.ShowDropHint(title, hint);
        }
        else
        {
            // 只拖了文件夹：明说为什么不行，别让它落下来再报错
            e.DragEffects = DragDropEffects.None;
            target.View.ShowDropHint("文件夹不能直接拖进来", "把工作区切到那个目录，再用 @ 引用里面的文件");
        }

        e.Handled = true;
    }

    private async void OnDropDropped(object? sender, DragEventArgs e)
    {
        CancelDropHintTimer();

        var target = _dropTarget ?? FindComposer();
        _dropTarget = null;

        if (target is not { } composer)
        {
            return;
        }

        composer.View.HideDropHint();

        var payload = ComposerDropPayload.Read(e.DataTransfer);
        if (!payload.Accepts || !IsInsidePage(e))
        {
            return;
        }

        e.Handled = true;
        e.DragEffects = DragDropEffects.Copy;

        // 拖放数据在事件返回后就被释放：要读的东西必须在这里同步读完，
        // 不能让下面那个 await 之后再去碰 e.DataTransfer。
        var bitmap = payload.HasBitmap ? ComposerDropPayload.TryTakeBitmap(e.DataTransfer) : null;

        var result = await composer.Composer.AcceptDropAsync(payload.Paths, bitmap);
        if (!result.Added)
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    /// <summary>当前页面上的输入框。设置页、MCP 页没有输入框，拖上去就不该有反应。</summary>
    private (ComposerView View, ComposerViewModel Composer)? FindComposer()
    {
        var composer = _viewModel.CurrentPage switch
        {
            ChatViewModel chat => chat.Composer,
            HomeViewModel home => home.Composer,
            _ => null,
        };

        if (composer is null)
        {
            return null;
        }

        var view = this.GetVisualDescendants()
            .OfType<ComposerView>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.DataContext, composer));

        return view is null ? null : (view, composer);
    }

    /// <summary>落点在不在内容区里。侧栏、底部状态条不算，免得拖文件到侧栏也在输入框里加一笔。</summary>
    private bool IsInsidePage(DragEventArgs e)
    {
        if (this.FindControl<ContentControl>("PageHost") is not { } host)
        {
            return true;
        }

        var point = e.GetPosition(host);
        return point.X >= 0
            && point.Y >= 0
            && point.X <= host.Bounds.Width
            && point.Y <= host.Bounds.Height;
    }

    /// <summary>提示文案：图片、普通文件、混着拖，说法不一样，用户才知道松开会发生什么。</summary>
    private static (string Title, string Hint) DescribeDrop(ComposerDropPayload payload)
    {
        if (!payload.HasPaths)
        {
            return ("松开即添加这张图片", "和 Ctrl+V 贴图一样，发送时一起交给模型");
        }

        var images = payload.Images;
        var total = payload.Paths.Count;

        if (images == total)
        {
            return (
                images == 1 ? "松开即添加这张图片" : $"松开即添加这 {images} 张图片",
                "和 Ctrl+V 贴图一样，发送时一起交给模型");
        }

        return total switch
        {
            1 => ("松开即引用这个文件", images > 0 ? "图片会作为图片发送" : "和 @ 引用走同一条路，发送时才读正文"),
            _ => (
                $"松开即引用这 {total} 个文件",
                images > 0
                    ? $"其中 {images} 张图片会作为图片发送，其余发送时才读正文"
                    : "和 @ 引用走同一条路，发送时才读正文"),
        };
    }
}
