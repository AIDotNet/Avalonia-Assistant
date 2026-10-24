using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using AvaloniaAssistant.UI.ViewModels;

namespace AvaloniaAssistant.UI.Views;

public partial class HomeView : UserControl
{
    private Flyout? _workspaceFlyout;

    public HomeView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private HomeViewModel? ViewModel => DataContext as HomeViewModel;

    /// <summary>首页右上角的「工作区」：和输入框下面那条挂同一个浮层，只是弹在按钮下方。</summary>
    private void OnWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not Control target)
        {
            return;
        }

        if (_workspaceFlyout is { IsOpen: true })
        {
            _workspaceFlyout.Hide();
            return;
        }

        _workspaceFlyout?.Hide();
        _workspaceFlyout = WorkspacePickerPopup.Show(target, ViewModel.Host, PlacementMode.BottomEdgeAlignedRight);
    }
}
