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
using ArcGlyph = TKCode.Arc.ArcGlyph;
using ArcIcons = TKCode.Arc.ArcIcons;

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

            // 引用失效的确认：Composer 不认识窗口，弹窗在这里接
            oldViewModel.ConfirmAsync = (title, message, confirmLabel, cancelLabel) =>
                ConfirmDialog.ShowAsync(this, title, message, confirmLabel, cancelLabel);
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
            if (bitmap is not null && !viewModel.AddImage(bitmap))
            {
                viewModel.NotifyImageError("这张图没能保存到数据目录，检查写入权限或磁盘空间。");
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
            // 拖的是文字：不接管，输入框自己的拖放还要用
            HideDropOverlay();
            return;
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

        if (!HasFiles(e) || ViewModel is not { } viewModel)
        {
            return;
        }

        e.Handled = true;
        if (AddDroppedFiles(e, viewModel))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
    }

    /// <summary>把拖进来的文件逐个变成引用，返回是否至少加进去一个。</summary>
    private static bool AddDroppedFiles(DragEventArgs e, ComposerViewModel viewModel)
    {
        IStorageItem[]? items;
        try
        {
            items = e.DataTransfer.TryGetFiles();
        }
        catch
        {
            // 剪贴板 / 拖放里塞了不认识的格式，当没发生
            return false;
        }

        if (items is null)
        {
            return false;
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

        return added > 0;
    }

    /// <summary>
    /// 把整个页面也变成落点：拖到消息区中间时，用户不会先去找输入框在哪。
    /// 落在输入框上仍由 ComposerView 自己处理（它先拿到事件并标记 Handled），这里兜外面的区域。
    /// </summary>
    public void AttachDropSurface(Control surface)
    {
        if (_dropSurface is not null || ReferenceEquals(surface, this))
        {
            return;
        }

        _dropSurface = surface;
        DragDrop.SetAllowDrop(surface, true);
        surface.AddHandler(DragDrop.DragEnterEvent, OnSurfaceDragOver);
        surface.AddHandler(DragDrop.DragOverEvent, OnSurfaceDragOver);
        surface.AddHandler(DragDrop.DragLeaveEvent, OnSurfaceDragLeave);
        surface.AddHandler(DragDrop.DropEvent, OnSurfaceDrop);
    }

    private Control? _dropSurface;

    private void OnSurfaceDragOver(object? sender, DragEventArgs e)
    {
        var (files, folders) = CountDropItems(e);

        // 拖的是文字或别的东西：不接管，交给控件自己处理
        if (files == 0 && folders == 0)
        {
            HideDropOverlay();
            return;
        }

        if (files > 0)
        {
            e.DragEffects = DragDropEffects.Copy;
            ShowDropOverlay(
                files == 1 ? "松开即引用这个文件" : $"松开即引用这 {files} 个文件",
                "会挂在输入框上方，发送时才读正文");
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
            ShowDropOverlay("文件夹不能直接拖进来", "把工作区切到那个目录，再用 @ 引用里面的文件");
        }

        e.Handled = true;
    }

    private void OnSurfaceDragLeave(object? sender, DragEventArgs e)
    {
        HideDropOverlay();
        e.Handled = true;
    }

    private void OnSurfaceDrop(object? sender, DragEventArgs e)
    {
        HideDropOverlay();

        if (ViewModel is not { } viewModel)
        {
            return;
        }

        // 不是文件就别拦：拖选中的文字回正文区时，那是控件自己的拖放
        if (!HasFiles(e))
        {
            return;
        }

        e.Handled = true;
        if (AddDroppedFiles(e, viewModel))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
    }

    private static bool HasFiles(DragEventArgs e)
    {
        try
        {
            return e.DataTransfer.TryGetFiles() is { Length: > 0 };
        }
        catch
        {
            return false;
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

        // 再点一次收起，别在原地叠第二层
        if (_modelFlyout is { IsOpen: true })
        {
            _modelFlyout.Hide();
            return;
        }

        _modelFlyout?.Hide();
        _modelFlyout = BuildModelFlyout(viewModel);
        _modelFlyout.ShowAt(target);
    }

    private Flyout? _modelFlyout;

    /// <summary>开发用：真的打开模型浮层并把弹层交出去，供 --screenshot 截真实版式。</summary>
    public Flyout? ShowModelFlyout()
    {
        if (ViewModel is null || this.FindControl<Button>("ModelButton") is not { } target)
        {
            return null;
        }

        _modelFlyout?.Hide();
        _modelFlyout = BuildModelFlyout(ViewModel);
        _modelFlyout.ShowAt(target);
        return _modelFlyout;
    }

    /// <summary>
    /// 模型浮层。以前用原生 MenuFlyout，只有一串名字：看不到当前选的是哪个，
    /// 也看不到服务商和说明；而侧栏早就因为原生菜单在当前主题量出 0×0 改成了自绘。
    /// 这里统一成同一套自绘浮层。
    /// </summary>
    private Flyout BuildModelFlyout(ComposerViewModel viewModel)
    {
        var list = new StackPanel { Spacing = 1 };

        var groups = viewModel.Models
            .GroupBy(model => string.IsNullOrWhiteSpace(model.ProviderName) ? "模型" : model.ProviderName)
            .ToList();

        if (groups.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "还没配模型服务：先去设置里填 API Key 和模型名。",
                FontSize = 11.5,
                LineHeight = 17,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(9, 8, 9, 9),
                Foreground = ThemeBrush("TextSecondaryBrush", Brushes.Gray),
            });
        }
        else
        {
            foreach (var group in groups)
            {
                list.Children.Add(new TextBlock
                {
                    Text = group.Key,
                    FontSize = 10.5,
                    Margin = new Thickness(9, 8, 9, 3),
                    Foreground = ThemeBrush("TextMutedBrush", Brushes.Gray),
                });

                foreach (var model in group)
                {
                    list.Children.Add(BuildModelRow(viewModel, model));
                }
            }
        }

        var content = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Text = "模型",
                    FontSize = 12.5,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(9, 3, 0, 3),
                    Foreground = ThemeBrush("TextPrimaryBrush", Brushes.Black),
                },
                new ScrollViewer
                {
                    MaxHeight = 340,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = list,
                },
            },
        };

        return new Flyout
        {
            Placement = PlacementMode.TopEdgeAlignedRight,
            Content = new Border
            {
                Width = 330,
                Padding = new Thickness(8, 7, 8, 8),
                CornerRadius = new CornerRadius(14),
                Background = ThemeBrush("SurfaceBrush", Brushes.White),
                BorderBrush = ThemeBrush("BorderStrongBrush", Brushes.Gainsboro),
                BorderThickness = new Thickness(1),
                BoxShadow = ThemeShadow("ComposerShadow"),
                Child = content,
            },
        };
    }

    private Button BuildModelRow(ComposerViewModel viewModel, ModelOption model)
    {
        var isCurrent = string.Equals(model.Id, viewModel.CurrentModelId, StringComparison.Ordinal);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("18,*,Auto"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        grid.Children.Add(new ArcGlyph
        {
            Data = ArcIcons.Check,
            Size = 12,
            IsVisible = isCurrent,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush("AccentBrush", Brushes.Black),
        });

        var texts = new StackPanel { Spacing = 1 };
        texts.Children.Add(new TextBlock
        {
            Text = model.Name,
            FontSize = 12.5,
            FontWeight = isCurrent ? FontWeight.SemiBold : FontWeight.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeBrush("TextPrimaryBrush", Brushes.Black),
        });

        if (!string.IsNullOrWhiteSpace(model.Description))
        {
            texts.Children.Add(new TextBlock
            {
                Text = model.Description,
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ThemeBrush("TextMutedBrush", Brushes.Gray),
            });
        }

        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        if (isCurrent)
        {
            var tag = new Border
            {
                Background = ThemeBrush("SurfaceMutedBrush", Brushes.Gainsboro),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "使用中",
                    FontSize = 9.5,
                    Foreground = ThemeBrush("TextMutedBrush", Brushes.Gray),
                },
            };
            Grid.SetColumn(tag, 2);
            grid.Children.Add(tag);
        }

        var button = new Button
        {
            Classes = { "model-row" },
            Content = grid,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(9, 6),
            CornerRadius = new CornerRadius(9),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };

        button.Click += async (_, _) =>
        {
            _modelFlyout?.Hide();
            await viewModel.SetModelAsync(model.Id);
        };

        return button;
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
