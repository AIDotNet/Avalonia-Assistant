using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using AvaloniaAssistant.UI.ViewModels;
using TKCode.Arc;

namespace AvaloniaAssistant.UI.Views;

public partial class PluginsView : UserControl
{
    public PluginsView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// 搜索框回车即搜。Arc 的 SearchField 只抛事件、没有命令属性，
    /// 这里转一次手；按钮那条路仍然走 VM 的命令。
    /// </summary>
    private void OnMarketSearchSubmitted(object? sender, SearchSubmittedEventArgs e)
    {
        if (DataContext is PluginsViewModel viewModel)
        {
            viewModel.SearchText = e.Query;
            viewModel.SearchCommand.Execute(null);
        }
    }
}
