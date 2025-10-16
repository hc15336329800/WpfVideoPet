
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
        /// <summary>
        /// 默认的通知展示时长，满足“输出文字 2 秒后自动消失”的需求。
        /// </summary>
        public static readonly TimeSpan DefaultNotificationDuration = TimeSpan.FromSeconds(2);

        private const int GwlExstyle = -20;
        private const int WsExTransparent = 0x20;
        private const int WsExNoActivate = 0x8000000;
        private readonly DispatcherTimer _notificationTimer; // 控制通知自动隐藏的调度器。
        private DateTime? _notificationExpiresAtUtc; // 记录当前通知预计结束时间，避免旧定时器误删新通知。

        public OverlayWindow()
        {
            InitializeComponent();
            _notificationTimer = new DispatcherTimer
            {
                Interval = DefaultNotificationDuration
            };
            _notificationTimer.Tick += NotificationTimerOnTick;
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

        public void ShowNotification(string message, TimeSpan? duration = null)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                AppLogger.Info("收到空通知内容，忽略展示请求。");
                return;
            }

            TxtNotification.Text = message.Trim();
            NotificationBorder.Visibility = Visibility.Visible;

            var interval = duration ?? DefaultNotificationDuration;
            if (interval <= TimeSpan.Zero)
            {
                AppLogger.Warn($"通知展示时长 {interval} 无效，回退至默认值 {DefaultNotificationDuration}。");
                interval = DefaultNotificationDuration;
            }

            _notificationExpiresAtUtc = DateTime.UtcNow.Add(interval);
            AppLogger.Info($"叠加层通知更新，内容：{TxtNotification.Text}，计划在 {interval.TotalSeconds:F1} 秒后隐藏。");

            _notificationTimer.Stop();
            _notificationTimer.Interval = interval;
            _notificationTimer.Start();
        }


        public void HideNotification()
        {
            _notificationTimer.Stop();
            NotificationBorder.Visibility = Visibility.Collapsed;
            TxtNotification.Text = string.Empty;
            _notificationExpiresAtUtc = null;
            AppLogger.Info("叠加层通知已隐藏并清空文案。");
        }

        /// <summary>
        /// 在叠加层上展示语音识别状态或结果。
        /// </summary>
        /// <param name="title">状态标题，如“识别中”或“识别结果”。</param>
        /// <param name="content">识别文本内容。</param>
        public void ShowTranscription(string title, string content)
        {
            TxtTranscriptionTitle.Text = string.IsNullOrWhiteSpace(title) ? "语音识别" : title.Trim();
            TxtTranscriptionContent.Text = content?.Trim() ?? string.Empty;
            TranscriptionBorder.Visibility = string.IsNullOrWhiteSpace(TxtTranscriptionContent.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        /// <summary>
        /// 隐藏语音识别区域并清空文案。
        /// </summary>
        public void HideTranscription()
        {
            TranscriptionBorder.Visibility = Visibility.Collapsed;
            TxtTranscriptionTitle.Text = "语音识别";
            TxtTranscriptionContent.Text = string.Empty;
        }

        protected override void OnClosed(EventArgs e)
        {
            _notificationTimer.Stop();
            _notificationTimer.Tick -= NotificationTimerOnTick;
            base.OnClosed(e);
        }

        private void NotificationTimerOnTick(object? sender, EventArgs e)
        {
            if (_notificationExpiresAtUtc.HasValue && DateTime.UtcNow < _notificationExpiresAtUtc.Value)
            {
                AppLogger.Info("检测到仍在展示期的通知，忽略本次自动隐藏信号。");
                return;
            }

            AppLogger.Info("通知展示时长已到，自动收起叠加层通知。");
            HideNotification();
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
