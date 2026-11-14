using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using AvaloniaAssistant.UI.ViewModels;

namespace AvaloniaAssistant.UI.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private HomeViewModel? ViewModel => DataContext as HomeViewModel;

    /// <summary>
    /// 悬停模板卡：把整段提示词交给输入框做灰字预览（只是预览，不进草稿、不发送）。
    /// 照 WorkBuddy 首页的做法；移开鼠标就还原占位文案。
    /// </summary>
    private void OnCardPointerEntered(object? sender, PointerEventArgs e)
    {
        if (ViewModel is { } vm && sender is Control { DataContext: SuggestionCardViewModel card })
        {
            vm.PreviewCard(card);
        }
    }

    private void OnCardPointerExited(object? sender, PointerEventArgs e)
    {
        ViewModel?.ClearPreview();
    }
}
