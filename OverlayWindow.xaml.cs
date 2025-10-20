using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WpfVideoPet
{
    /// <summary>
    /// 负责在主界面上叠加提示信息、天气信息和语音识别状态的窗体。
    /// </summary>
    public partial class OverlayWindow : Window
    {
        private const int GwlExstyle = -20; // Win32 窗口扩展样式索引
        private const int WsExTransparent = 0x20; // 允许鼠标穿透样式值
        private const int WsExNoActivate = 0x8000000; // 禁止窗口激活样式值


        public OverlayWindow()
        {
            InitializeComponent();
         }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e); var handle = new WindowInteropHelper(this).Handle;
            var styles = GetWindowLong(handle, GwlExstyle);
            SetWindowLong(handle, GwlExstyle, styles | WsExTransparent | WsExNoActivate);
        }

        public void UpdateWeather(string city, string weather, string temperature)
        {
            TxtCity.Text = city;
            TxtWeather.Text = weather;
            TxtTemp.Text = temperature;
        }

        public void UpdateTime(string timeText)
        {
            TxtTime.Text = timeText;
        }

        public void UpdatePetRotation(double angle)
        {
            CubeRotation.Angle = angle;
        }

        /// <summary>
        /// 展示虚拟人通知的占位方法，当前仅输出日志以提示调用来源。
        /// </summary>
        /// <param name="message">通知正文内容。</param>
        /// <param name="duration">展示时长参数，已无实际意义。</param>
        /// <param name="title">通知标题参数，已无实际意义。</param>
        public void ShowNotification(string message, TimeSpan? duration = null, string? title = null)
        {
            var safeMessage = string.IsNullOrWhiteSpace(message) ? "<空>" : message.Trim();
            var safeTitle = string.IsNullOrWhiteSpace(title) ? "<默认标题>" : title.Trim();
            AppLogger.Info($"收到通知展示请求，但气泡区域已移除。标题：{safeTitle}，内容：{safeMessage}，原计划时长：{(duration ?? TimeSpan.Zero).TotalMilliseconds} ms。");
        }

        /// <summary>
        /// 隐藏通知的占位方法，当前仅记录日志确保流程可追踪。
        /// </summary>
        public void HideNotification()
        {
            AppLogger.Info("收到通知隐藏请求，但气泡区域已移除，无需额外处理。");
        }

        /// <summary>
        /// 展示语音识别内容的占位方法，当前仅记录日志。
        /// </summary>
        /// <param name="title">语音识别标题。</param>
        /// <param name="content">语音识别文本内容。</param>
        public void ShowTranscription(string title, string content)
        {
            var safeTitle = string.IsNullOrWhiteSpace(title) ? "<默认标题>" : title.Trim();
            var safeContent = string.IsNullOrWhiteSpace(content) ? "<空>" : content.Trim();
            AppLogger.Info($"收到语音识别展示请求，但气泡区域已移除。标题：{safeTitle}，内容：{safeContent}。");
        }

        /// 隐藏语音识别内容的占位方法，当前仅记录日志。
        /// </summary>
        public void HideTranscription()
        {
            AppLogger.Info("收到语音识别隐藏请求，但气泡区域已移除，无需额外处理。");
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
