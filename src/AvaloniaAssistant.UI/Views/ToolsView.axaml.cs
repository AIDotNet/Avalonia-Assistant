using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using AvaloniaAssistant.UI.ViewModels;

namespace AvaloniaAssistant.UI.Views;

public partial class ToolsView : UserControl
{
    private ToolsViewModel? _viewModel;

    public ToolsView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PreviewRequested -= OnPreviewRequested;
        }

        _viewModel = DataContext as ToolsViewModel;
        if (_viewModel is not null)
        {
            // VM 读文件、视图弹窗：VM 不认识窗口，测试里也不会因为它去开窗口
            _viewModel.PreviewRequested += OnPreviewRequested;
        }

        base.OnDataContextChanged(e);
    }

    private void OnPreviewRequested(object? sender, SkillPreview preview)
        => _ = SkillPreviewDialog.ShowAsync(this, preview);
}
