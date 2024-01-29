using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using Desktop.Assistant.ViewModels;
using ReactiveUI;
using System;

namespace Desktop.Assistant.Views
{
    public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
    {
        public MainWindow()
        {
            InitializeComponent();
            this.AttachDevTools();
            // 订阅窗体打开事件
            this.Opened += OnOpened;
        }

        private void InitializeComponent()
        {
            this.WhenActivated(disposables => { /* Handle view activation etc. */ });
            AvaloniaXamlLoader.Load(this);
            //this.FindControl<ContentControl>("ContentArea").Content = new MainView();
        }
        private void OnOpened(object sender, EventArgs e)
        {
            // 获取主屏幕的工作区域大小
            var screen = Screens.Primary;
            var workingArea = screen.WorkingArea;

            // 计算窗体横坐标，使窗体靠右显示
            var xPosition = workingArea.Width - this.Width - 150;

            // 可选：计算窗体纵坐标，如果你想要窗体在屏幕垂直方向上也有特定的位置
            // 例如，如果你想要窗体出现在屏幕顶部，你可以设为0；出现在屏幕中央可以设为屏幕高度减去窗体高度的一半
            var yPosition = 50; // 或者其他你所希望的值

            // 应用计算出的位置
            this.Position = new PixelPoint((int)xPosition, yPosition);
        }
    }
}