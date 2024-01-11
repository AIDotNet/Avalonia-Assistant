using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ReactiveUI;
using Avalonia.ReactiveUI;
using Desktop.Assistant.ViewModels;
using Avalonia.Interactivity;

namespace Desktop.Assistant.Views
{
    public partial class WelcomeView : ReactiveUserControl<WelcomeViewModel>
    {
        public WelcomeView()
        {
            InitializeComponent();

        }

        private void InitializeComponent()
        {
            this.WhenActivated(disposables => { /* Handle view activation etc. */ });
            AvaloniaXamlLoader.Load(this);
        }

        public void ClickHandler(object sender, RoutedEventArgs args)
        {
            this.Content = new ChatView();
        }
    }
}
