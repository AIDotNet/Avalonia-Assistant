using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using Desktop.Assistant.ViewModels;
using ReactiveUI;
using System.Diagnostics.Metrics;

namespace Desktop.Assistant.Views
{
    public partial class ChatView : ReactiveUserControl<ChatViewModel>
    {
        public ChatView()
        {
            ViewModel = new ChatViewModel(new RoutingState());
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
