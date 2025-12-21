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
        /// 初始化急停提醒窗口，配置文本内容、窗口样式、右下角位置与置顶行为。
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
            WindowStartupLocation = WindowStartupLocation.Manual;
            Owner = owner;
            Topmost = true;

            Loaded += OnLoaded;
        }

        /// <summary>
        /// 窗口加载完成后定位到右下角并置顶，确保提示不会被覆盖。
        /// </summary>
        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            MoveToBottomRight();
            EnsureOnTop();
        }

        /// <summary>
        /// 将窗口移动到屏幕右下角，保留安全边距避免贴边遮挡。
        /// </summary>
        private void MoveToBottomRight()
        {
            const double margin = 16; // 右下角安全边距
            var workArea = SystemParameters.WorkArea; // 可用屏幕区域
            Left = Math.Max(workArea.Left + margin, workArea.Right - ActualWidth - margin);
            Top = Math.Max(workArea.Top + margin, workArea.Bottom - ActualHeight - margin);
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