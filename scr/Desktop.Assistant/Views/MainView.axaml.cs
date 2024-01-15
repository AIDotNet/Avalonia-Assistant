using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using Desktop.Assistant.ViewModels;
using ReactiveUI;

namespace Desktop.Assistant.Views
{
    public partial class MainView : ReactiveUserControl<MainViewModel>
    {
        public MainView()
        {
            ViewModel=new MainViewModel(new RoutingState());
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.WhenActivated(disposables => { /* Handle view activation etc. */ });
            AvaloniaXamlLoader.Load(this);
        }

        public void ClickHandler(object sender, RoutedEventArgs args)
        {
            this.Content = new WelcomeView();
        }
    }
}
