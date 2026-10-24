using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AvaloniaAssistant.UI.Views;

/// <summary>
/// 主题自带的 light dismiss 不生效（点窗口别处不收起），这里自己接管：
/// 窗口里任何一次按下、或者按 Esc，都把弹层收起来。弹层关掉时解绑，避免越挂越多。
/// 侧栏菜单、输入框里的工作区浮层都走这一份。
/// </summary>
internal static class FlyoutDismiss
{
    public static void Attach(Flyout flyout, Visual owner)
    {
        if (TopLevel.GetTopLevel(owner) is not { } topLevel)
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

            if (e.Source is Visual source)
            {
                // 弹层自己也挂在另一个 TopLevel 上：它的点击不该把弹层关掉
                if (TopLevel.GetTopLevel(source) is { } sourceTop && !ReferenceEquals(sourceTop, topLevel))
                {
                    return;
                }

                // 点在自己的触发按钮上交给 Click 处理，不然会先收起再弹出，看着像没反应
                if (IsWithin(source, owner))
                {
                    return;
                }
            }

            flyout.Hide();
        }

        void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                flyout.Hide();
            }
        }

        topLevel.AddHandler(
            InputElement.PointerPressedEvent,
            OnPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        topLevel.AddHandler(
            InputElement.KeyDownEvent,
            OnKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        flyout.Closed += (_, _) =>
        {
            topLevel.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            topLevel.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        };
    }

    private static bool IsWithin(Visual source, Visual? owner)
        => owner is not null && (source == owner || source.GetVisualAncestors().Contains(owner));
}
