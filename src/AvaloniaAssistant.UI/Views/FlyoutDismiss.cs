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
///
/// 浮层要什么内容只能各自装配，但「点外面收起」统一从这里走（<see cref="Show"/>）：
/// 少一次手动 Attach，就少一个点不掉的浮层——工具权限浮层就漏过。
/// </summary>
internal static class FlyoutDismiss
{
    /// <summary>挂上「点外面 / Esc 收起」再把浮层贴到锚点上。</summary>
    public static void Show(Flyout flyout, Control anchor)
    {
        flyout.Opened += (_, _) => Attach(flyout, anchor);
        flyout.ShowAt(anchor);
    }

    private static void Attach(Flyout flyout, Control owner)
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

                // 弹层被同层托管时（OverlayPopupHost）上面那条判不出来，按子树再判一次：
                // 否则点菜单项会先收起弹层，Click 就丢了，看着像点了没反应。
                if (IsInsideFlyout(flyout, source))
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

    /// <summary>这次按下是不是落在弹层自己的内容上。</summary>
    private static bool IsInsideFlyout(Flyout flyout, Visual source)
    {
        if (flyout.Content is not Visual content)
        {
            return false;
        }

        // 内容被套在模板产出的 FlyoutPresenter 里，它就是「层内 / 层外」的分界
        foreach (var ancestor in content.GetVisualAncestors())
        {
            if (ancestor is FlyoutPresenter presenter)
            {
                return IsWithin(source, presenter);
            }
        }

        return false;
    }

    private static bool IsWithin(Visual source, Visual? owner)
        => owner is not null && (source == owner || source.GetVisualAncestors().Contains(owner));
}
