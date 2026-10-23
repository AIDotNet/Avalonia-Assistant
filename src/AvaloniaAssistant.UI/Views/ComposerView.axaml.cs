using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.UI.ViewModels;

namespace AvaloniaAssistant.UI.Views;

public partial class ComposerView : UserControl
{
    public ComposerView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private ComposerViewModel? ViewModel => DataContext as ComposerViewModel;

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        e.Handled = true;
        var viewModel = ViewModel;
        if (viewModel?.SendCommand.CanExecute(null) == true)
        {
            viewModel.SendCommand.Execute(null);
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

        var flyout = new MenuFlyout();
        foreach (var (mode, label) in new[]
                 {
                     (PermissionMode.ReadOnly, "只读：不改文件、不执行命令"),
                     (PermissionMode.Workspace, "工作区：可读写工作区，命令需确认"),
                     (PermissionMode.FullAccess, "完全访问：所有工具直接执行"),
                 })
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await viewModel.SetPermissionAsync(mode);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(target);
    }

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
