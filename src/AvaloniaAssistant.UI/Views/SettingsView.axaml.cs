using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using AvaloniaAssistant.UI.ViewModels;

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

    /// <summary>切到完全访问要先过一次风险确认，和其它入口保持一致。</summary>
    private async void OnFullAccessClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel || viewModel.IsFullAccessSelected)
        {
            return;
        }

        if (!await PermissionConfirmDialog.ShowAsync(this))
        {
            return;
        }

        viewModel.SetPermissionCommand.Execute("full");
    }
}
