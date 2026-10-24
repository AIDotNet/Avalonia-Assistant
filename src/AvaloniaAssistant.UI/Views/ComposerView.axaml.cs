using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.UI.ViewModels;

namespace AvaloniaAssistant.UI.Views;

public partial class ComposerView : UserControl
{
    public ComposerView()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

        // 拖文件进来就当成引用。AllowDrop 挂在整个输入框上，
        // 因为拖放设备会从命中的子控件往上找第一个开着 AllowDrop 的元素。
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        AttachedToVisualTree += (_, _) =>
        {
            // 拖放设备是从命中的元素往上找 AllowDrop 的；不同版本行为有差别，
            // 干脆把输入框和容器也各开一份，保证落在文字区、工具条上都能接住。
            foreach (var name in new[] { "ComposerFrame", "Input" })
            {
                if (this.FindControl<Control>(name) is { } target)
                {
                    DragDrop.SetAllowDrop(target, true);
                }
            }

            Dispatcher.UIThread.Post(
                () => this.FindControl<Control>("Input")?.Focus(),
                DispatcherPriority.Input);
        };
    }

    private ComposerViewModel? ViewModel => DataContext as ComposerViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (oldViewModel is not null)
        {
            oldViewModel.FocusRequested -= OnFocusRequested;
        }

        oldViewModel = ViewModel;
        if (oldViewModel is not null)
        {
            oldViewModel.FocusRequested += OnFocusRequested;
        }

        base.OnDataContextChanged(e);
    }

    private ComposerViewModel? oldViewModel;

    private void OnFocusRequested(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => this.FindControl<Control>("Input")?.Focus(), DispatcherPriority.Input);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (oldViewModel is not null)
        {
            oldViewModel.FocusRequested -= OnFocusRequested;
            oldViewModel = null;
        }

        ViewModel?.CancelRecording();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        // 录音中按 Esc：放弃这段录音，别让它继续占着麦克风
        if (e.Key == Key.Escape && viewModel.IsRecording)
        {
            viewModel.CancelRecording();
            e.Handled = true;
            return;
        }

        // Ctrl+V 顺手看看剪贴板里有没有图：有就收进待发送列表。
        // 文本仍然交给 Textarea 自己粘，不抢它的活。
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = TryPasteImageAsync(viewModel);
        }

        // 输入法还在组词时（拼音没上屏），回车是给候选词用的，不能拿来发送，
        // 否则打一句中文、按回车选词的那一下就把半句话发出去了。
        var composing = IsComposing();

        // @ 浮层开着的时候，上下键和 Enter 归浮层：先选资料，再把光标交回输入框，
        // 别让用户按 Enter 想选一条却把半截问题发出去了。
        if (viewModel.ShowMention)
        {
            if (viewModel.HasMentionItems)
            {
                switch (e.Key)
                {
                    case Key.Down when !composing:
                        viewModel.MoveMention(1);
                        e.Handled = true;
                        return;

                    case Key.Up when !composing:
                        viewModel.MoveMention(-1);
                        e.Handled = true;
                        return;

                    case Key.Enter when !composing && !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                        viewModel.AcceptCurrentMention();
                        e.Handled = true;
                        return;

                    case Key.Escape:
                        viewModel.CloseMentionCommand.Execute(null);
                        e.Handled = true;
                        return;
                }
            }
            else if (e.Key == Key.Escape)
            {
                viewModel.CloseMentionCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        if (e.Key != Key.Enter || composing || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        e.Handled = true;
        if (viewModel.SendCommand.CanExecute(null))
        {
            viewModel.SendCommand.Execute(null);
        }
    }

    /// <summary>
    /// 输入法是不是正在组词。模板里的 TextPresenter 会把没上屏的拼音放在 PreeditText，
    /// 非空就说明这一下回车属于输入法，不属于我们。
    /// </summary>
    private bool IsComposing()
    {
        foreach (var presenter in this.GetVisualDescendants().OfType<TextPresenter>())
        {
            if (!string.IsNullOrEmpty(presenter.PreeditText))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 读剪贴板里的位图。剪贴板里是文本或文件时什么都不做——
    /// 用户按 Ctrl+V 粘一段文字，不该被这里拦住。
    /// </summary>
    private async Task TryPasteImageAsync(ComposerViewModel viewModel)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        try
        {
            var bitmap = await clipboard.TryGetValueAsync(DataFormat.Bitmap);
            if (bitmap is not null)
            {
                viewModel.AddImage(bitmap);
            }
        }
        catch
        {
            // 剪贴板格式不认识，当普通粘贴处理
        }
    }

    /// <summary>
    /// 拖到输入框上：有文件就显示"松开即引用"，拖的是文件夹就说明为什么不接。
    /// 拦掉文件夹比让它落下来再报错省事。
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var (files, folders) = CountDropItems(e);

        if (files > 0)
        {
            e.DragEffects = DragDropEffects.Copy;
            ShowDropOverlay(
                files == 1 ? "松开即引用这个文件" : $"松开即引用这 {files} 个文件",
                "和 @ 引用走同一条路，发送时才读正文");
        }
        else if (folders > 0)
        {
            e.DragEffects = DragDropEffects.None;
            ShowDropOverlay("文件夹不能直接拖进来", "把工作区切到那个目录，再用 @ 引用里面的文件");
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
            HideDropOverlay();
        }

        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        HideDropOverlay();
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        HideDropOverlay();
        e.Handled = true;

        if (ViewModel is not { } viewModel)
        {
            return;
        }

        IStorageItem[]? items;
        try
        {
            items = e.DataTransfer.TryGetFiles();
        }
        catch
        {
            // 剪贴板 / 拖放里塞了不认识的格式，当没发生
            return;
        }

        if (items is null)
        {
            return;
        }

        var added = 0;
        foreach (var item in items)
        {
            if (item is IStorageFolder)
            {
                continue;
            }

            var path = item.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path) && viewModel.AddFileReference(path))
            {
                added++;
            }
        }

        if (added > 0)
        {
            e.DragEffects = DragDropEffects.Copy;
        }
    }

    /// <summary>拖进来的条目里，文件和文件夹各几个。读不出来就当没有。</summary>
    private static (int Files, int Folders) CountDropItems(DragEventArgs e)
    {
        IStorageItem[]? items;
        try
        {
            items = e.DataTransfer.TryGetFiles();
        }
        catch
        {
            return (0, 0);
        }

        if (items is null || items.Length == 0)
        {
            return (0, 0);
        }

        var folders = 0;
        foreach (var item in items)
        {
            if (item is IStorageFolder)
            {
                folders++;
            }
        }

        return (items.Length - folders, folders);
    }

    /// <summary>开发用：把拖放提示层摆出来截图。真实拖拽没法在命令行里模拟，只能单独验版式。</summary>
    public void ShowDropPreview()
        => ShowDropOverlay("松开即引用这 2 个文件", "和 @ 引用走同一条路，发送时才读正文");

    private void ShowDropOverlay(string title, string hint)
    {
        if (this.FindControl<Border>("DropOverlay") is not { } overlay)
        {
            return;
        }

        if (this.FindControl<TextBlock>("DropOverlayText") is { } titleBlock)
        {
            titleBlock.Text = title;
        }

        if (this.FindControl<TextBlock>("DropOverlayHint") is { } hintBlock)
        {
            hintBlock.Text = hint;
        }

        overlay.IsVisible = true;
    }

    private void HideDropOverlay()
    {
        if (this.FindControl<Border>("DropOverlay") is { } overlay)
        {
            overlay.IsVisible = false;
        }
    }

    private void OnModelClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || sender is not Control target)
        {
            return;
        }

        var flyout = new MenuFlyout();
        foreach (var model in viewModel.Models)
        {
            var item = new MenuItem { Header = model.Name };
            item.Click += async (_, _) => await viewModel.SetModelAsync(model.Id);
            flyout.Items.Add(item);
        }

        if (flyout.Items.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "先在设置中配置模型", IsEnabled = false });
        }

        flyout.ShowAt(target);
    }

    private void OnPermissionClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || sender is not Control target)
        {
            return;
        }

        _permissionFlyout?.Hide();
        _permissionFlyout = BuildPermissionFlyout(viewModel);
        _permissionFlyout.ShowAt(target);
    }

    private Flyout? _permissionFlyout;

    /// <summary>
    /// 权限浮层：一句当前说明 + 三个档位。完全访问是危险态，选中它之后所有工具直接执行。
    /// </summary>
    private Flyout BuildPermissionFlyout(ComposerViewModel viewModel)
    {
        var stack = new StackPanel { Width = 306, Spacing = 9 };

        stack.Children.Add(new TextBlock
        {
            Text = "工具权限",
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeBrush("TextPrimaryBrush", Brushes.Black),
        });

        stack.Children.Add(new TextBlock
        {
            Text = viewModel.PermissionDetail,
            FontSize = 11.5,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush("TextSecondaryBrush", Brushes.Gray),
        });

        // 放过「都允许」就把这件事摆出来：否则 pill 上写着默认权限，行为其实已经变了。
        if (viewModel.HasSessionAllowedTools)
        {
            var reset = new Button
            {
                Classes = { "ghost-action" },
                Content = "恢复逐次确认",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0),
                Foreground = ThemeBrush("WarningBrush", Brushes.DarkOrange),
            };
            reset.Click += (_, _) =>
            {
                _permissionFlyout?.Hide();
                viewModel.ResetSessionAllowedCommand.Execute(null);
            };

            var allowed = new StackPanel { Spacing = 3 };
            allowed.Children.Add(new TextBlock
            {
                Text = $"本次对话已放行 {viewModel.SessionAllowedCount} 个工具，不再逐次确认",
                FontSize = 11,
                LineHeight = 16,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrush("WarningBrush", Brushes.DarkOrange),
            });
            allowed.Children.Add(reset);

            stack.Children.Add(new Border
            {
                Background = ThemeBrush("WarningSubtleBrush", Brushes.Wheat),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 7),
                Child = allowed,
            });
        }

        stack.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 2),
            Background = ThemeBrush("BorderBrush", Brushes.Gainsboro),
        });

        stack.Children.Add(BuildPermissionOption(viewModel, PermissionMode.Workspace, "默认权限", "执行命令、调用 MCP 前先问你"));
        stack.Children.Add(BuildPermissionOption(viewModel, PermissionMode.FullAccess, "完全访问", "所有工具直接执行，不再逐次确认"));
        stack.Children.Add(BuildPermissionOption(viewModel, PermissionMode.ReadOnly, "只读", "不改文件、不执行命令"));

        return new Flyout
        {
            Placement = PlacementMode.TopEdgeAlignedRight,
            Content = new Border
            {
                Padding = new Thickness(14, 12),
                CornerRadius = new CornerRadius(14),
                Background = ThemeBrush("SurfaceBrush", Brushes.White),
                BorderBrush = ThemeBrush("BorderStrongBrush", Brushes.Gainsboro),
                BorderThickness = new Thickness(1),
                BoxShadow = ThemeShadow("ComposerShadow"),
                Child = stack,
            },
        };
    }

    private Button BuildPermissionOption(
        ComposerViewModel viewModel,
        PermissionMode mode,
        string title,
        string description)
    {
        var selected = viewModel.Permission == mode;
        var accent = mode == PermissionMode.FullAccess
            ? ThemeBrush("WarningBrush", Brushes.DarkOrange)
            : ThemeBrush("TextPrimaryBrush", Brushes.Black);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("16,*,Auto"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        grid.Children.Add(new TextBlock
        {
            Text = "✓",
            FontSize = 12,
            IsVisible = selected,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var texts = new StackPanel { Spacing = 1 };
        texts.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            Foreground = accent,
        });
        texts.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 11,
            Foreground = ThemeBrush("TextMutedBrush", Brushes.Gray),
        });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var button = new Button
        {
            Classes = { "permission-option" },
            Content = grid,
            Tag = mode,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        if (selected)
        {
            button.Classes.Add("active");
        }

        button.Click += OnPermissionOptionClick;
        return button;
    }

    private async void OnPermissionOptionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PermissionMode mode } || ViewModel is null)
        {
            return;
        }

        _permissionFlyout?.Hide();

        // 切到完全访问等于把确认步骤全关掉，先让用户明确点一次同意。
        if (mode == PermissionMode.FullAccess && !PermissionModeText.SkipsApproval(ViewModel.Permission))
        {
            if (!await PermissionConfirmDialog.ShowAsync(this))
            {
                return;
            }
        }

        await ViewModel.SetPermissionAsync(mode);
    }

    private static IBrush ThemeBrush(string key, IBrush fallback)
        => Application.Current?.FindResource(key) as IBrush ?? fallback;

    private static BoxShadows ThemeShadow(string key)
        => Application.Current?.FindResource(key) is BoxShadows shadow ? shadow : default;

    /// <summary>
    /// 输入框下面那条工作区：点开是「搜索 + 列表 + 新建 / 打开 / 不使用」，
    /// 不再是一个塞满整条路径的原生菜单（那套菜单在当前主题里量出来是 0×0，根本不显示）。
    /// </summary>
    private void OnWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not Control target)
        {
            return;
        }

        // 再点一次就收起，别在原地叠出第二层
        if (_workspaceFlyout is { IsOpen: true })
        {
            _workspaceFlyout.Hide();
            return;
        }

        _workspaceFlyout?.Hide();
        _workspaceFlyout = WorkspacePickerPopup.Show(target, ViewModel.Host, PlacementMode.TopEdgeAlignedLeft);
    }

    /// <summary>开发用：直接打开工作区浮层并把弹层交出去，供 --screenshot 截真实的弹层。</summary>
    public Flyout? ShowWorkspaceFlyout()
    {
        if (ViewModel is null || this.FindControl<Button>("WorkspaceButton") is not { } target)
        {
            return null;
        }

        _workspaceFlyout?.Hide();
        _workspaceFlyout = WorkspacePickerPopup.Show(target, ViewModel.Host, PlacementMode.TopEdgeAlignedLeft);
        return _workspaceFlyout;
    }

    private Flyout? _workspaceFlyout;
}
