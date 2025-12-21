using System.Windows;
using System.Windows.Controls;

namespace WpfVideoPet
{
    /// <summary>
    /// 急停提醒窗口，保持置顶显示，用于提示急停状态并在解除时关闭。
    /// </summary>
    public sealed class EmergencyStopWindow : Window
    {
        private readonly TextBlock _messageText; // 提示文本

        /// <summary>
        /// 初始化急停提醒窗口，配置文本内容、窗口样式与置顶行为。
        /// </summary>
        /// <param name="message">急停提示正文。</param>
        /// <param name="owner">归属的父窗口。</param>
        public EmergencyStopWindow(string message, Window? owner)
        {
            _messageText = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20)
            };

            Title = "急停提醒";
            Content = _messageText;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.ToolWindow;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            Owner = owner;
            Topmost = true;

            Loaded += OnLoaded;
        }

        /// <summary>
        /// 窗口加载完成后激活并再次置顶，确保提示不会被覆盖。
        /// </summary>
        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            EnsureOnTop();
        }

        /// <summary>
        /// 主动将窗口激活并置顶显示，适配被视频页面遮挡的场景。
        /// </summary>
        public void EnsureOnTop()
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Topmost = true;
            Activate();
        }
    }
}