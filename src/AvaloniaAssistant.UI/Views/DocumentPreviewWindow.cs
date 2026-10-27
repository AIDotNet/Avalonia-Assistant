using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace AvaloniaAssistant.UI.Views;

/// <summary>
/// 应用内预览文档内容。
/// <para>
/// 显示的是「读出来的结构」而不是版式：docx 的标题层级与表格、xlsx 的每张工作表、
/// pdf 的按页正文都会保留，但字体、分页、图片位置还原不了。
/// 像素级预览要引 PDFium 或 WebView2，为一个「先看一眼对不对」的诉求不划算，
/// 所以窗口上直接写清楚这是文字预览。
/// </para>
/// </summary>
public sealed class DocumentPreviewWindow : Window
{
    private static readonly IBrush PageBrush = new SolidColorBrush(Color.Parse("#FBFBF9"));
    private static readonly IBrush SurfaceBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#1D1D1B"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#6F6F69"));
    private static readonly IBrush LineBrush = new SolidColorBrush(Color.Parse("#E5E5DF"));

    public DocumentPreviewWindow(string filePath, string content, bool truncated)
    {
        var name = Path.GetFileName(filePath);

        Title = "预览 · " + name;
        Width = 940;
        Height = 760;
        MinWidth = 560;
        MinHeight = 420;
        Background = PageBrush;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var header = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(22, 18, 22, 12),
        };
        header.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 14.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        header.Children.Add(new TextBlock
        {
            Text = "文字预览 · 版式和排版请打开原文件",
            FontSize = 11,
            Foreground = MutedBrush,
        });

        var body = new SelectableTextBlock
        {
            Text = content,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 21,
            Foreground = TextBrush,
            Margin = new Thickness(22, 0, 22, 18),
        };

        var scroller = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var footerText = truncated
            ? "内容较长，只显示了前面一部分；完整内容请打开原文件。"
            : "内容已完整显示。";

        var openButton = new Button
        {
            Content = "打开原文件",
            Padding = new Thickness(14, 0),
            Height = 30,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse("#1D1D1B")),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        openButton.Click += (_, _) => Services.Ui.OpenExternal(filePath);

        var closeButton = new Button
        {
            Content = "关闭",
            Padding = new Thickness(14, 0),
            Height = 30,
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Foreground = TextBrush,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        closeButton.Click += (_, _) => Close();

        var footerRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        footerRight.Children.Add(closeButton);
        footerRight.Children.Add(openButton);

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(22, 10, 22, 12),
        };
        footer.Children.Add(new TextBlock
        {
            Text = footerText,
            FontSize = 11,
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 16, 0),
        });
        Grid.SetColumn(footerRight, 1);
        footer.Children.Add(footerRight);

        var footerBorder = new Border
        {
            Background = SurfaceBrush,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = footer,
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
        };
        grid.Children.Add(header);
        Grid.SetRow(scroller, 1);
        grid.Children.Add(scroller);
        Grid.SetRow(footerBorder, 2);
        grid.Children.Add(footerBorder);

        Content = grid;
    }
}
