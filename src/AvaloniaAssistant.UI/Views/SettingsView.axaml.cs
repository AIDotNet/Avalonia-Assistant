using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AvaloniaAssistant.UI.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnRevealKeyClick(object? sender, RoutedEventArgs e)
    {
        var box = this.FindControl<TextBox>("KeyBox");
        if (box is null)
        {
            return;
        }

        box.PasswordChar = box.PasswordChar == '\0' ? '●' : '\0';
    }
}
