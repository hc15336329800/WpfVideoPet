using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WpfVideoPet.xunfei;

namespace WpfVideoPet.doubao
{
    /// <summary>
    /// 负责展示语音转写与豆包回答内容的轻量弹窗。
    /// </summary>
    public partial class AiChatPopupWindow : Window
    {
        private readonly DispatcherTimer _autoCloseTimer; // 弹窗自动关闭计时器
        private readonly AikitSayService _ttsService; // 讯飞TTS服务实例
        private readonly MediaPlayer _ttsPlayer; // 音频播放器
        private bool _ttsInitialized; // TTS初始化标记
        private readonly string _ttsOutputPath; // TTS音频路径
        private bool _playbackNotified; // 是否已经对外广播过播放完成事件，避免重复通知

        /// <summary>
        /// 在 TTS 播放开始时触发，便于外部执行音量压制。
        /// </summary>
        public event EventHandler? TtsPlaybackStarted;

        /// <summary>
        /// 在 TTS 播放结束时触发，便于外部恢复音量。
        /// </summary>
        public event EventHandler? TtsPlaybackCompleted;

        /// <summary>
        /// 初始化弹窗并配置自动关闭计时器。
        /// </summary>
        public AiChatPopupWindow()
        {
            InitializeComponent();
            _autoCloseTimer = new DispatcherTimer();
            _autoCloseTimer.Tick += OnAutoCloseTimerTick;
            _ttsService = new AikitSayService();
            _ttsPlayer = new MediaPlayer();
            _ttsOutputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SayDLL", "OutPut.wav");
            InitializeTtsService();
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
        /// 从外部强制终止当前 TTS 播放并关闭弹窗，用于用户发出“退出”指令时的兜底处理。
        /// </summary>
        public void StopTtsPlaybackAndClose()
        {
            StopAutoCloseCountdown();

            try
            {
                _ttsPlayer.Stop();
                AppLogger.Info("收到强制停止请求，已终止TTS播放。");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"停止TTS播放时发生异常: {ex.Message}");
            }

            // 为了保证外部的音频压制状态及时恢复，这里主动补发播放完成事件。
            if (!_playbackNotified)
            {
                TtsPlaybackCompleted?.Invoke(this, EventArgs.Empty);
                _playbackNotified = true;
                AppLogger.Info("已补发TTS播放完成事件以便恢复音量。");
            }

            if (IsVisible)
            {
                AppLogger.Info("强制关闭AI 问答弹窗。");
                Close();
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
        /// <summary>
        /// 触发讯飞文字转语音服务并在音频播放完成后启动关闭流程。
        /// </summary>
        /// <param name="answer">豆包返回的完整回答文本。</param>
        public async Task HandleFinalAnswerAsync(string answer)
        {
            StopAutoCloseCountdown();
            var sanitizedAnswer = string.IsNullOrWhiteSpace(answer) ? string.Empty : answer.Trim();
            AppLogger.Info("AI 问答弹窗收到完整回答，准备执行TTS播报流程。");

            if (string.IsNullOrEmpty(sanitizedAnswer))
            {
                AppLogger.Warn("豆包回答为空，跳过TTS播报并直接启动关闭计时。");
                BeginAutoCloseCountdown(TimeSpan.FromSeconds(2));
                return;
            }

            EnsureTtsService();

            if (!_ttsInitialized)
            {
                AppLogger.Warn("讯飞TTS服务未初始化成功，无法播报，将直接进入关闭计时。");
                BeginAutoCloseCountdown(TimeSpan.FromSeconds(2));
                return;
            }

            try
            {
                await Task.Run(() =>
                {
                    AppLogger.Info("开始调用讯飞TTS生成音频文件。");
                    _ttsService.Speak(sanitizedAnswer);
                });

                await PlayTtsAudioAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"TTS播报流程出现异常: {ex.Message}");
            }
            finally
            {
                BeginAutoCloseCountdown(TimeSpan.FromSeconds(2));
            }
        }

        /// <summary>
        /// 初始化讯飞文字转语音服务，确保依赖库与回调预先绑定。
        /// </summary>
        private void InitializeTtsService()
        {
            try
            {
                _ttsService.Init();
                _ttsInitialized = true;
                AppLogger.Info("讯飞TTS服务初始化成功。");
            }
            catch (Exception ex)
            {
                _ttsInitialized = false;
                AppLogger.Error(ex, $"讯飞TTS服务初始化失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 在需要播报时再次确认TTS服务状态，必要时尝试重新初始化。
        /// </summary>
        private void EnsureTtsService()
        {
            if (_ttsInitialized)
            {
                return;
            }

            AppLogger.Warn("检测到TTS服务未就绪，尝试重新初始化。");
            InitializeTtsService();
        }

        /// <summary>
        /// 负责播放由讯飞TTS生成的音频文件，并在播放完成后返回。
        /// </summary>
        private async Task PlayTtsAudioAsync()
        {
            if (!File.Exists(_ttsOutputPath))
            {
                AppLogger.Warn($"未找到TTS输出文件: {_ttsOutputPath}");
                return;
            }

            var tcs = new TaskCompletionSource<bool>();
            _playbackNotified = false; // 播放状态通知标记

            void CleanupHandlers()
            {
                _ttsPlayer.MediaEnded -= OnMediaEnded;
                _ttsPlayer.MediaFailed -= OnMediaFailed;
            }

            void OnMediaEnded(object? _, EventArgs __)
            {
                CleanupHandlers();
                tcs.TrySetResult(true);
            }

            void OnMediaFailed(object? _, ExceptionEventArgs args)
            {
                CleanupHandlers();
                AppLogger.Error(args.ErrorException, $"播放TTS音频时发生错误: {args.ErrorException?.Message}");
                tcs.TrySetResult(false);
            }

            _ttsPlayer.Stop();
            _ttsPlayer.MediaEnded += OnMediaEnded;
            _ttsPlayer.MediaFailed += OnMediaFailed;

            try
            {
                _ttsPlayer.Open(new Uri(_ttsOutputPath));
                _ttsPlayer.Play();
                TtsPlaybackStarted?.Invoke(this, EventArgs.Empty);
                _playbackNotified = true;
                AppLogger.Info($"开始播放讯飞TTS音频: {_ttsOutputPath}");
                await tcs.Task;
                AppLogger.Info("讯飞TTS音频播放完成。");
            }
            finally
            {
                CleanupHandlers();
                if (_playbackNotified)
                {
                    TtsPlaybackCompleted?.Invoke(this, EventArgs.Empty);
                    AppLogger.Info("已广播TTS播放完成事件，通知外部恢复音量。");
                }
            }
        }
    }
}
