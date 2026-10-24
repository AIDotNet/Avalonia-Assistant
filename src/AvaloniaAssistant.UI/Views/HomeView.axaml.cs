using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AvaloniaAssistant.UI.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
