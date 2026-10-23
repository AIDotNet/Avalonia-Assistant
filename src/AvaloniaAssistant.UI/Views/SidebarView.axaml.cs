using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaAssistant.UI.ViewModels;
using ArcGlyph = TKCode.Arc.ArcGlyph;
using ArcIcons = TKCode.Arc.ArcIcons;

namespace AvaloniaAssistant.UI.Views;

public partial class SidebarView : UserControl
{
    private Flyout? _menuFlyout;

    public SidebarView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// 工作区右边那个「⋯」。这里用 Flyout 自己排内容，不用 MenuFlyout：
    /// 当前主题下 Avalonia 原生的 MenuFlyoutPresenter 量出来是 0×0，菜单会「打开」但什么都不显示。
    /// </summary>
    private void OnWorkspaceMenuClick(object? sender, RoutedEventArgs e)
    {
        Diag.Log("click 进入 OnWorkspaceMenuClick");
        if (sender is not Control target || target.DataContext is not WorkspaceItemViewModel item)
        {
            Diag.Log("click 未命中 dataContext");
            return;
        }

        CloseFlyouts();

        var flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
        };

        // 重命名是在同一个弹层里换内容，不再另外开一个：换窗口容易丢掉锚点，弹到屏幕左上角去
        flyout.Content = BuildMenu(flyout, item);

        // 打开之后再挂「点外面收起」：刚开始展开时，打开它的那一下点击还在路上，
        // 提前挂上去会把菜单自己关掉，看着像点了没反应。
        flyout.Opened += (_, _) => { Diag.Log("opened"); AttachDismiss(flyout, target); };
        flyout.Closed += (_, _) => Diag.Log("closed");

        _menuFlyout = flyout;
        flyout.ShowAt(target);
        Diag.Log($"ShowAt 完成 IsOpen={flyout.IsOpen}");
    }

    private Control BuildMenu(Flyout flyout, WorkspaceItemViewModel item)
    {
        var rows = new StackPanel { Spacing = 1 };

        rows.Children.Add(MenuRow("打开文件夹", ArcIcons.FolderOpen, () =>
        {
            CloseFlyouts();
            item.OpenFolder();
        }));

        rows.Children.Add(MenuRow("重新选择目录…", ArcIcons.Folder, () =>
        {
            CloseFlyouts();
            item.RepickCommand.Execute(null);
        }));

        rows.Children.Add(MenuRow("重命名", ArcIcons.Pencil, () => flyout.Content = BuildRename(flyout, item)));

        rows.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(6, 4),
            Background = ThemeBrush("BorderBrush", Brushes.Gainsboro),
        });

        // 移除当前工作区等于不再带工作区，用词把后果说清楚
        rows.Children.Add(MenuRow(
            item.IsActive ? "移除，改为普通任务" : "从列表中移除",
            ArcIcons.Trash2,
            () =>
            {
                CloseFlyouts();
                item.RemoveCommand.Execute(null);
            },
            ThemeBrush("DangerBrush", Brushes.IndianRed)));

        return new Border
        {
            Padding = new Thickness(5),
            CornerRadius = new CornerRadius(12),
            Background = ThemeBrush("SurfaceBrush", Brushes.White),
            BorderBrush = ThemeBrush("BorderStrongBrush", Brushes.Gainsboro),
            BorderThickness = new Thickness(1),
            BoxShadow = ThemeShadow("ComposerShadow"),
            Child = rows,
        };
    }

    /// <summary>重命名用的小卡片：一个输入框 + 确定/取消，出错就把原因写在下面。换掉菜单内容，弹层不重新定位。</summary>
    private Control BuildRename(Flyout flyout, WorkspaceItemViewModel item)
    {
        var input = new TextBox
        {
            Text = item.Name,
            Width = 214,
            FontSize = 12.5,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var error = new TextBlock
        {
            FontSize = 11,
            IsVisible = false,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush("DangerBrush", Brushes.IndianRed),
        };

        var cancel = new Button
        {
            Content = "取消",
            Height = 30,
            Padding = new Thickness(12, 0),
            FontSize = 12,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = ThemeBrush("TextSecondaryBrush", Brushes.Gray),
        };

        var confirm = new Button
        {
            Content = "确定",
            Height = 30,
            Padding = new Thickness(14, 0),
            FontSize = 12,
            CornerRadius = new CornerRadius(8),
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Children = { cancel, confirm },
        };

        var card = new StackPanel
        {
            Width = 230,
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "重命名目录",
                    FontSize = 12.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeBrush("TextPrimaryBrush", Brushes.Black),
                },
                input,
                error,
                actions,
            },
        };

        var border = new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(12),
            Background = ThemeBrush("SurfaceBrush", Brushes.White),
            BorderBrush = ThemeBrush("BorderStrongBrush", Brushes.Gainsboro),
            BorderThickness = new Thickness(1),
            BoxShadow = ThemeShadow("ComposerShadow"),
            Child = card,
        };

        cancel.Click += (_, _) => flyout.Hide();

        confirm.Click += async (_, _) =>
        {
            var message = await item.RenameAsync(input.Text ?? string.Empty);
            if (message is null)
            {
                flyout.Hide();
                return;
            }

            error.Text = message;
            error.IsVisible = true;
        };

        input.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                args.Handled = true;
                confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else if (args.Key == Key.Escape)
            {
                args.Handled = true;
                flyout.Hide();
            }
        };

        // 内容换进已打开的弹层里，聚焦放到下一帧，等它真正挂上视觉树
        Dispatcher.UIThread.Post(() =>
        {
            input.Focus();
            input.SelectAll();
        }, DispatcherPriority.Background);

        return border;
    }
    /// <summary>
    /// 当前主题下弹层自带的 light dismiss 不生效（点窗口别处不收起），这里自己接管：
    /// 窗口里任何一次按下、或者按 Esc，都把弹层收起来。弹层关掉时解绑，避免越挂越多。
    /// </summary>
    private void AttachDismiss(Flyout flyout, Control owner)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var openedAt = DateTime.UtcNow;

        void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // 打开弹层的那一下点击可能还在路上，别把它当「点外面」
            if ((DateTime.UtcNow - openedAt).TotalMilliseconds < 200)
            {
                return;
            }

            Diag.Log($"press source={e.Source?.GetType().Name}");
            if (e.Source is Visual source)
            {
                // 弹层自己也挂在另一个 TopLevel 上：它的点击不该把弹层关掉
                if (TopLevel.GetTopLevel(source) is { } sourceTop && !ReferenceEquals(sourceTop, topLevel))
                {
                    return;
                }

                // 点在自己的「⋯」上交给 Click 处理，不然会先收起再弹出，看着像没反应
                if (IsWithin(source, owner))
                {
                    return;
                }
            }

            Diag.Log("press -> hide");
            flyout.Hide();
        }

        void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                flyout.Hide();
            }
        }

        topLevel.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        flyout.Closed += (_, _) =>
        {
            topLevel.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            topLevel.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        };
    }

    private static class Diag
    {
        public static void Log(string message)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aa-menu.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}
");
            }
            catch
            {
                // 诊断用，失败无所谓
            }
        }
    }

    private static bool IsWithin(Visual source, Visual? owner)
        => owner is not null && (source == owner || source.GetVisualAncestors().Contains(owner));

    private Button MenuRow(string text, Geometry icon, Action invoke, IBrush? accent = null)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            Children =
            {
                new ArcGlyph
                {
                    Data = icon,
                    Size = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = text,
                    FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        var button = new Button
        {
            Content = content,
            Height = 32,
            Padding = new Thickness(9, 0),
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Foreground = accent ?? ThemeBrush("TextPrimaryBrush", Brushes.Black),
        };

        button.Click += (_, _) => invoke();
        return button;
    }

    private void CloseFlyouts()
    {
        _menuFlyout?.Hide();
        _menuFlyout = null;
    }

    private static IBrush ThemeBrush(string key, IBrush fallback)
        => Application.Current?.FindResource(key) as IBrush ?? fallback;

    private static BoxShadows ThemeShadow(string key)
        => Application.Current?.FindResource(key) is BoxShadows shadow ? shadow : default;
}
