using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using Desktop.Assistant.ViewModels;

namespace Desktop.Assistant.Views
{
    public partial class ChatView : ReactiveUserControl<ChatViewModel>
    {
        public ChatView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
