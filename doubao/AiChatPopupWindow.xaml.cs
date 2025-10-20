using System;
using System.Windows;
using System.Windows.Threading;

namespace WpfVideoPet.doubao
{
    /// <summary>
    /// 负责展示语音转写与豆包回答内容的轻量弹窗。
    /// </summary>
    public partial class AiChatPopupWindow : Window
    {
        private readonly DispatcherTimer _autoCloseTimer; // 弹窗自动关闭计时器

        /// <summary>
        /// 初始化弹窗并配置自动关闭计时器。
        /// </summary>
        public AiChatPopupWindow()
        {
            InitializeComponent();
            _autoCloseTimer = new DispatcherTimer();
            _autoCloseTimer.Tick += OnAutoCloseTimerTick;
            AppLogger.Info("AI 问答弹窗已初始化，等待语音内容。");
        }

        /// <summary>
        /// 更新问题区域的文本，确保展示最新的语音转写结果。
        /// </summary>
        /// <param name="question">语音识别得到的问题文本。</param>
        public void SetQuestion(string question)
        {
            TxtQuestion.Text = string.IsNullOrWhiteSpace(question) ? string.Empty : question.Trim();
            AppLogger.Info($"AI 问答弹窗更新问题内容: {TxtQuestion.Text}");
        }

        /// <summary>
        /// 清空回答区域，为新的豆包回复做准备。
        /// </summary>
        public void ClearAnswer()
        {
            StopAutoCloseCountdown();
            TxtAnswer.Text = string.Empty;
            AppLogger.Info("AI 问答弹窗回答区域已清空。");
        }

        /// <summary>
        /// 将豆包服务返回的增量文本附加到回答区域。
        /// </summary>
        /// <param name="delta">豆包流式接口返回的增量文本。</param>
        public void AppendAnswer(string delta)
        {
            if (string.IsNullOrEmpty(delta))
            {
                return;
            }

            TxtAnswer.Text += delta;
            AnswerScroll.ScrollToEnd();
            AppLogger.Info($"AI 问答弹窗追加回答片段: {delta}");
        }

        /// <summary>
        /// 在弹窗底部显示错误提示，并启动自动关闭倒计时。
        /// </summary>
        /// <param name="message">错误描述。</param>
        public void ShowError(string message)
        {
            StopAutoCloseCountdown();
            var content = string.IsNullOrWhiteSpace(message) ? "未知错误" : message.Trim();
            TxtAnswer.Text = $"[错误] {content}";
            BeginAutoCloseCountdown(TimeSpan.FromSeconds(2));
            AppLogger.Warn($"AI 问答弹窗显示错误: {content}");
        }

        /// <summary>
        /// 启动自动关闭计时器，在指定时长后关闭弹窗。
        /// </summary>
        /// <param name="delay">倒计时持续时长。</param>
        public void BeginAutoCloseCountdown(TimeSpan delay)
        {
            StopAutoCloseCountdown();
            _autoCloseTimer.Interval = delay;
            _autoCloseTimer.Start();
            AppLogger.Info($"AI 问答弹窗将在 {delay.TotalMilliseconds}ms 后自动关闭。");
        }

        /// <summary>
        /// 停止自动关闭计时器，避免误触关闭。
        /// </summary>
        public void StopAutoCloseCountdown()
        {
            if (_autoCloseTimer.IsEnabled)
            {
                _autoCloseTimer.Stop();
                AppLogger.Info("AI 问答弹窗自动关闭计时器已停止。");
            }
        }

        /// <summary>
        /// 激活弹窗窗口，确保其显示在最前面。
        /// </summary>
        public void ActivateWindow()
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            if (!IsVisible)
            {
                Show();
            }

            Activate();
            AppLogger.Info("AI 问答弹窗已激活并置顶显示。");
        }

        /// <summary>
        /// 自动关闭计时器触发时关闭弹窗。
        /// </summary>
        private void OnAutoCloseTimerTick(object? sender, EventArgs e)
        {
            _autoCloseTimer.Stop();
            AppLogger.Info("AI 问答弹窗自动关闭计时到期，关闭窗口。");
            Close();
        }
    }
}