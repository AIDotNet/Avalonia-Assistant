using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AvaloniaAssistant.UI.Views;

public partial class WorkbenchView : UserControl
{
    public WorkbenchView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
