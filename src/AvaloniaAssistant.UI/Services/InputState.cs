using Avalonia;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;

namespace AvaloniaAssistant.UI.Services;

/// <summary>
/// 输入法还在组词（候选没上屏）时，Enter 和 Esc 是给输入法的，不该被当成「发送」或「停止」。
/// 一旦误判，用户按回车确认候选，消息就带着半截拼音发出去了。
/// </summary>
internal static class InputState
{
    public static bool IsComposing(Visual? root)
        => root is not null
           && root.GetVisualDescendants()
               .OfType<TextPresenter>()
               .Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText));
}
