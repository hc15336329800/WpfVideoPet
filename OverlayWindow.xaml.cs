
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

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
        private CancellationTokenSource? _notificationCts; // 控制通知隐藏逻辑的取消令牌，确保并发通知不会互相干扰。


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
            CancelPendingNotificationHide();

            var cts = new CancellationTokenSource();
            _notificationCts = cts;

            AppLogger.Info($"叠加层通知更新，内容：{TxtNotification.Text}，计划在 {interval.TotalSeconds:F1} 秒后隐藏。");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(interval, cts.Token).ConfigureAwait(false);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!ReferenceEquals(_notificationCts, cts))
                        {
                            AppLogger.Info("检测到更新后的通知已经接管隐藏流程，本次定时任务忽略。");
                            return;
                        }

                        AppLogger.Info("通知展示时长已到，由后台任务触发自动隐藏。");
                        HideNotification();
                    });
                }
                catch (TaskCanceledException)
                {
                    AppLogger.Info("通知隐藏任务已被取消，通常是新的通知到来或手动隐藏触发。");
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, $"执行通知隐藏任务时出现异常: {ex.Message}");
                }
                finally
                {
                    if (ReferenceEquals(_notificationCts, cts))
                    {
                        CancelPendingNotificationHide();
                    }
                    cts.Dispose();
                }
            });
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
            var previousCts = Interlocked.Exchange(ref _notificationCts, null);
            if (previousCts == null)
            {
                return;
            }

            if (!previousCts.IsCancellationRequested)
            {
                previousCts.Cancel();
            }

            previousCts.Dispose();
            AppLogger.Info("已取消历史通知隐藏任务，避免旧任务误删新通知。");
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
