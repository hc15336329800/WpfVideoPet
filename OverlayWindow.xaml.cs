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
        private DispatcherTimer? _notificationTimer; // 负责调度通知隐藏的 UI 线程计时器，避免跨线程隐藏导致状态错乱。


        public OverlayWindow()
        {
            InitializeComponent();
            InitializeNotificationTimer();
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
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => ShowNotification(message, duration));
                return;
            }

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

            AppLogger.Info($"叠加层通知更新，内容：{TxtNotification.Text}，计划在 {interval.TotalSeconds:F1} 秒后隐藏。");

            PrepareNotificationTimer(interval);
        }


        public void HideNotification()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(HideNotification);
                return;
            }

            CancelPendingNotificationHide();

            NotificationBorder.Visibility = Visibility.Collapsed;
            TxtNotification.Text = string.Empty;
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
            base.OnClosed(e);
        }

        /// <summary>
        /// 取消已有的通知隐藏任务，确保新通知展示时不会被旧的定时任务误触发隐藏。
        /// </summary>
        private void CancelPendingNotificationHide()
        {
            if (_notificationTimer == null)
            {
                return;
            }

            if (_notificationTimer.IsEnabled)
            {
                _notificationTimer.Stop();
                AppLogger.Info("已停止历史通知隐藏计时，避免旧任务误删新通知。");
            }
        }

        /// <summary>
        /// 初始化通知隐藏计时器，确保所有隐藏逻辑在 UI 线程执行，避免跨线程状态紊乱。
        /// </summary>
        private void InitializeNotificationTimer()
        {
            _notificationTimer = new DispatcherTimer
            {
                Interval = DefaultNotificationDuration
            };
            _notificationTimer.Tick += OnNotificationTimerTick;
            AppLogger.Info("通知隐藏计时器已初始化，默认时长为 " + DefaultNotificationDuration.TotalSeconds + " 秒。");
        }

        /// <summary>
        /// 在展示通知前根据业务需求重新配置计时器并启动。
        /// </summary>
        /// <param name="interval">通知展示时长。</param>
        private void PrepareNotificationTimer(TimeSpan interval)
        {
            if (_notificationTimer == null)
            {
                InitializeNotificationTimer();
            }

            if (_notificationTimer == null)
            {
                AppLogger.Warn("通知隐藏计时器初始化失败，无法自动隐藏通知。");
                return;
            }

            CancelPendingNotificationHide();
            _notificationTimer.Interval = interval;
            _notificationTimer.Start();
            AppLogger.Info($"通知隐藏计时器已重新启动，将在 {interval.TotalMilliseconds} ms 后自动隐藏。");
        }

        /// <summary>
        /// 计时器到期时触发通知隐藏，确保交互体验一致。
        /// </summary>
        private void OnNotificationTimerTick(object? sender, EventArgs e)
        {
            AppLogger.Info("通知展示时长已到，由 DispatcherTimer 触发自动隐藏。");
            HideNotification();
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}