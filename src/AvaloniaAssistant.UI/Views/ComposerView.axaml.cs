using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.UI.ViewModels;

namespace AvaloniaAssistant.UI.Views;

public partial class ComposerView : UserControl
{
    public ComposerView()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(
            () => this.FindControl<Control>("Input")?.Focus(),
            DispatcherPriority.Input);
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
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        // 中文输入法组词时按 Enter 是在确认候选，不是发送；这里让输入法自己处理。
        if (Services.InputState.IsComposing(this))
        {
            return;
        }

        // 录音中按 Enter 等于点麦克风：停止并转写，不让人以为键坏了。
        if (viewModel.IsRecording)
        {
            e.Handled = true;
            viewModel.ToggleVoiceCommand.Execute(null);
            return;
        }

        // 只有真的要发送时才吞掉 Enter。运行中 / 转写中把它吞了，
        // 输入框里既没有换行也没有发送，表现出来就是「键盘没反应」。
        if (!viewModel.SendCommand.CanExecute(null))
        {
            return;
        }

        e.Handled = true;
        viewModel.SendCommand.Execute(null);
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

    private void OnWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || sender is not Control target)
        {
            return;
        }

        var flyout = new MenuFlyout();
        foreach (var path in viewModel.RecentWorkspaces)
        {
            var item = new MenuItem { Header = path };
            item.Click += async (_, _) => await viewModel.SetWorkspaceAsync(path);
            flyout.Items.Add(item);
        }

        if (flyout.Items.Count > 0)
        {
            flyout.Items.Add(new Separator());
        }

        var pick = new MenuItem { Header = "选择文件夹…" };
        pick.Click += async (_, _) => await viewModel.PickWorkspaceCommand.ExecuteAsync(null);
        flyout.Items.Add(pick);
        flyout.ShowAt(target);
    }
}
