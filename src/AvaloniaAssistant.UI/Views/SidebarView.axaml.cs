using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AvaloniaAssistant.UI.Views;

public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
