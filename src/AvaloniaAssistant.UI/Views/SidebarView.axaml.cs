using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using AvaloniaAssistant.UI.ViewModels;

namespace AvaloniaAssistant.UI.Views;

public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>工作区右侧那个「⋯」：换个目录，或者把这条记录去掉。</summary>
    private void OnWorkspaceMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control target || target.DataContext is not WorkspaceItemViewModel item)
        {
            return;
        }

        var flyout = new MenuFlyout();

        var repick = new MenuItem { Header = "重新选择目录…" };
        repick.Click += async (_, _) => await item.RepickCommand.ExecuteAsync(null);
        flyout.Items.Add(repick);

        flyout.Items.Add(new Separator());

        // 去掉当前工作区就等于不再带工作区，用词直接说清后果
        var remove = new MenuItem
        {
            Header = item.IsActive ? "移除，改为普通任务" : "从列表移除",
        };
        remove.Click += async (_, _) => await item.RemoveCommand.ExecuteAsync(null);
        flyout.Items.Add(remove);

        flyout.ShowAt(target);
    }
}
