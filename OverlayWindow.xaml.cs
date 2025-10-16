
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WpfVideoPet
{
    public partial class OverlayWindow : Window
    {
        private const int GwlExstyle = -20;
        private const int WsExTransparent = 0x20;
        private const int WsExNoActivate = 0x8000000;
        private readonly DispatcherTimer _notificationTimer;

        public OverlayWindow()
        {
            InitializeComponent();
            _notificationTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
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
                return;
            }

            TxtNotification.Text = message.Trim();
            NotificationBorder.Visibility = Visibility.Visible;

            var interval = duration ?? TimeSpan.FromSeconds(5);
            _notificationTimer.Stop();
            _notificationTimer.Interval = interval;
            _notificationTimer.Start();
        }


        public void HideNotification()
        {
            _notificationTimer.Stop();
            NotificationBorder.Visibility = Visibility.Collapsed;
            TxtNotification.Text = string.Empty;
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
            HideNotification();
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
