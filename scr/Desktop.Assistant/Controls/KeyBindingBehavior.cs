using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Xaml.Interactions.Custom;
using Avalonia.Xaml.Interactivity;
using System.Windows.Input;


namespace Desktop.Assistant.Controls
{
    public class KeyBindingBehavior : Behavior<Control>
    {
        public static readonly StyledProperty<Key> KeyProperty =
            AvaloniaProperty.Register<KeyBindingBehavior, Key>(nameof(Key));

        public Key Key
        {
            get => GetValue(KeyProperty);
            set => SetValue(KeyProperty, value);
        }

        public static readonly StyledProperty<ICommand> CommandProperty =
            AvaloniaProperty.Register<KeyBindingBehavior, ICommand>(nameof(Command));

        public ICommand Command
        {
            get => GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject.KeyUp += OnKeyUp;
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();
            AssociatedObject.KeyUp -= OnKeyUp;
        }

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key && Command != null && Command.CanExecute(null))
            {
                Command.Execute(null);
            }
        }
    }
}
