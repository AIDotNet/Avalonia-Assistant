using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaAssistant.UI.ViewModels;

namespace AvaloniaAssistant.UI.Views;

/// <summary>
/// 工作区浮层：搜索 + 列表 + 新建 / 打开 / 不使用。
/// 结构照着常用的那套来——先搜、再选、最后才是「造一个新的」，动作放在分隔线下面，
/// 免得手快的时候把「新建」当列表项点。
/// </summary>
public partial class WorkspacePicker : UserControl
{
    private WorkspacePickerViewModel? _bound;

    public WorkspacePicker()
    {
        AvaloniaXamlLoader.Load(this);

        // 用 Tunnel：搜索框自己也要吃 Enter（SearchField 会抛 SearchSubmitted），
        // 先在这里截住，键盘才不会有两套行为。
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(
            () => this.FindControl<Control>("SearchBox")?.Focus(),
            DispatcherPriority.Input);
    }

    private WorkspacePickerViewModel? ViewModel => DataContext as WorkspacePickerViewModel;

    /// <summary>
    /// 开发用：把自己所在的弹层根（含 Flyout 外壳）整块渲染成 PNG。
    /// 弹层是另一个 TopLevel，截主窗口看不到它，所以要它自己出图。
    /// </summary>
    public void CaptureSelf(string path)
    {
        if (TopLevel.GetTopLevel(this) is not Visual root)
        {
            return;
        }

        var size = new PixelSize(
            Math.Max(1, (int)Math.Round(root.Bounds.Width)),
            Math.Max(1, (int)Math.Round(root.Bounds.Height)));

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(root);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_bound is not null)
        {
            _bound.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _bound = ViewModel;

        if (_bound is not null)
        {
            _bound.PropertyChanged += OnViewModelPropertyChanged;
        }

        base.OnDataContextChanged(e);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_bound is not null)
        {
            _bound.PropertyChanged -= OnViewModelPropertyChanged;
            _bound = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>进出「新建」时把焦点交给该拿的那个输入框，别让人再点一下。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkspacePickerViewModel.IsCreating) || ViewModel is not { } viewModel)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => this.FindControl<Control>(viewModel.IsCreating ? "CreateInput" : "SearchBox")?.Focus(),
            DispatcherPriority.Input);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        if (viewModel.IsCreating)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    viewModel.ConfirmCreateCommand.Execute(null);
                    e.Handled = true;
                    return;

                case Key.Escape:
                    viewModel.CancelCreateCommand.Execute(null);
                    e.Handled = true;
                    return;
            }

            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                viewModel.MoveSelection(1);
                e.Handled = true;
                break;

            case Key.Up:
                viewModel.MoveSelection(-1);
                e.Handled = true;
                break;

            case Key.Enter:
                viewModel.ActivateSelection();
                e.Handled = true;
                break;

            case Key.Escape:
                viewModel.Close();
                e.Handled = true;
                break;
        }
    }
}

/// <summary>
/// 把工作区浮层挂到按钮上。首页右上角的「工作区」和输入框下面那一条都用它，
/// 免得两处各写一份弹层装配，改一处忘一处。
/// </summary>
internal static class WorkspacePickerPopup
{
    public static Flyout Show(Control anchor, MainWindowViewModel host, PlacementMode placement)
    {
        var picker = new WorkspacePickerViewModel(host);
        var flyout = new Flyout
        {
            Placement = placement,
            Content = new WorkspacePicker { DataContext = picker },
        };

        picker.CloseRequested += (_, _) => flyout.Hide();
        flyout.Opened += (_, _) => FlyoutDismiss.Attach(flyout, anchor);
        flyout.ShowAt(anchor);

        return flyout;
    }
}
