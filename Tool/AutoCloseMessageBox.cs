using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WpfVideoPet
{
    /// <summary>
    /// 轻量级自动关闭提示窗体，用于显示短暂信息且不阻塞主界面操作。
    /// </summary>
    public sealed class AutoCloseMessageBox : Window
    {
        private readonly DispatcherTimer _autoCloseTimer; // 自动关闭计时器
        private readonly TextBlock _messageText; // 提示文本
        private readonly TimeSpan _autoCloseDelay; // 自动关闭延迟

        /// <summary>
        /// 初始化自动关闭提示窗体，完成基础 UI 结构、样式与事件绑定。
        /// </summary>
        /// <param name="message">提示正文内容。</param>
        /// <param name="title">提示标题内容。</param>
        /// <param name="owner">所属的父窗口。</param>
        private AutoCloseMessageBox(string message, string title, Window? owner)
        {
            _autoCloseDelay = TimeSpan.FromSeconds(5);
            _messageText = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20)
            };
            _autoCloseTimer = new DispatcherTimer
            {
                Interval = _autoCloseDelay
            };
            _autoCloseTimer.Tick += OnAutoCloseTimerTick;

            Title = title;
            Content = _messageText;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.ToolWindow;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            Owner = owner;

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        /// <summary>
        /// 显示自动关闭提示窗体，适用于非阻塞提示场景。
        /// </summary>
        /// <param name="message">提示正文内容。</param>
        /// <param name="title">提示标题内容。</param>
        /// <param name="owner">所属的父窗口。</param>
        public static void Show(string message, string title, Window? owner = null)
        {
            var window = new AutoCloseMessageBox(message, title, owner);
            window.Show();
        }

        /// <summary>
        /// 在窗口加载后启动自动关闭计时器。
        /// </summary>
        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            _autoCloseTimer.Start();
        }

        /// <summary>
        /// 定时触发关闭窗口，保证提示自动消失。
        /// </summary>
        private void OnAutoCloseTimerTick(object? sender, EventArgs e)
        {
            Close();
        }

        /// <summary>
        /// 窗口关闭时停止计时器，释放定时资源。
        /// </summary>
        private void OnClosed(object? sender, EventArgs e)
        {
            _autoCloseTimer.Stop();
        }
    }
}