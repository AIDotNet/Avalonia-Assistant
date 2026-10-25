using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using AvaloniaAssistant.UI.Services;
using AvaloniaAssistant.UI.ViewModels;

namespace AvaloniaAssistant.UI.Views;

/// <summary>
/// 技能正文。技能页以前只能看到名字和两行描述，想确认它触发条件、步骤、用哪些工具，
/// 只能去资源管理器翻目录。这里直接读 SKILL.md 原文，只读、可选中复制。
/// </summary>
public partial class SkillPreviewDialog : Window
{
    private string _path = string.Empty;

    public SkillPreviewDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public static async Task ShowAsync(Visual anchor, SkillPreview preview)
    {
        if (TopLevel.GetTopLevel(anchor) is not Window owner)
        {
            return;
        }

        var dialog = new SkillPreviewDialog();
        dialog.Apply(preview);

        // 和确认弹窗同一套：铺满父窗口，遮罩才像遮罩
        var origin = owner.PointToScreen(new Point(0, 0));
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Position = origin;
        dialog.Width = owner.ClientSize.Width;
        dialog.Height = owner.ClientSize.Height;

        await dialog.ShowDialog(owner);
    }

    private void Apply(SkillPreview preview)
    {
        _path = preview.Path;

        if (this.FindControl<TextBlock>("TitleText") is { } title)
        {
            title.Text = preview.Name;
        }

        if (this.FindControl<TextBlock>("PathText") is { } path)
        {
            path.Text = Ui.ShortenPath(preview.Path);
        }

        if (this.FindControl<SelectableTextBlock>("ContentText") is { } content)
        {
            content.Text = preview.Content;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }

        base.OnKeyDown(e);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnRevealClick(object? sender, RoutedEventArgs e) => Ui.RevealInExplorer(_path);
}
