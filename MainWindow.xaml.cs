using LibVLCSharp.Shared;
using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using WpfVideoPet.doubao;
using WpfVideoPet.mqtt;
using WpfVideoPet.xunfei;

namespace WpfVideoPet
{
    public partial class MainWindow : Window
    {
        private readonly LibVLC _libVlc;
        private readonly LibVLCSharp.Shared.MediaPlayer _player;
        private readonly DispatcherTimer _clockTimer;
        private readonly DispatcherTimer _petTimer; // 宠物动画计时器
        private double _angle; // 宠物旋转角度
        private OverlayWindow? _overlay; // 叠加层窗口实例
        private VideoCallWindow? _videoCallWindow; // 视频通话窗口实例
        private readonly DispatcherTimer _volumeRestoreTimer; // 音量恢复计时器
        /// <summary>
        /// 控制语音识别提示自动隐藏的计时器，避免识别文本长时间停留。
        /// </summary>
        private readonly DispatcherTimer _transcriptionResetTimer;
        /// <summary>
        /// 标记程序是否在内部更新音量滑块，防止重复触发事件。
        /// </summary>
        private bool _suppressSliderCallback;
        /// <summary>
        /// 语音识别区域在最后一次更新后保持展示的时长。
        /// </summary>
        private static readonly TimeSpan TranscriptionAutoHideDelay = TimeSpan.FromSeconds(2);
        private bool _isDuckingAudio;
        private int _userPreferredVolume;
        private readonly AppConfig _appConfig;
        private readonly AudioDuckingConfig _audioDuckingConfig;
        private MqttTaskService? _mqttService;
        private readonly HttpClient _httpClient = new();
        private readonly string _mediaCacheDirectory;
        private readonly ConcurrentDictionary<string, Task<string?>> _mediaDownloadTasks = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _playbackStateLock = new(); // 播放状态锁
        private string? _currentLocalMediaPath; // 当前本地媒体路径
        private string? _currentRemoteMediaUrl; // 当前远程媒体地址
        private readonly AikitWakeService _wakeService;
        /// <summary>
        /// 语音播报服务实例，用于进行实时转写。
        /// </summary>
        private readonly AikitLIstenService? _bobaoService;
        /// <summary>
        /// 控制语音识别任务状态的锁对象。
        /// </summary>
        private readonly object _speechRecognitionStateLock = new();
        /// <summary>
        /// 当前语音识别任务对应的取消标记。
        /// </summary>
        private CancellationTokenSource? _speechRecognitionCts;
        private readonly doubao_service_chat _doubaoService; // 豆包问答服务实例
        private readonly object _aiChatLock = new(); // AI 问答流程同步锁
        private CancellationTokenSource? _aiAnswerCts; // 当前豆包回答任务取消源
        private AiChatPopupWindow? _aiChatWindow; // AI 问答弹窗引用
        private bool _expectAiAnswer; // 标记是否需要触发豆包回答
        private readonly DispatcherTimer _aiChatInactivityTimer; // AI 弹窗无语音关闭计时器
        private static readonly TimeSpan AiChatInactivityTimeout = TimeSpan.FromSeconds(5); // AI 弹窗监听超时
        private readonly object _audioDuckingLock = new(); // 音量压制状态锁
        private readonly HashSet<string> _activeAudioDuckingReasons = new(); // 当前音量压制原因
        private const string DuckingReasonAiChat = "AI_CHAT_WINDOW"; // AI 弹窗压制标识
        private const string DuckingReasonTts = "AI_CHAT_TTS"; // TTS 播报压制标识
        private const string DuckingReasonVideoCall = "VIDEO_CALL_WINDOW"; // 视频通话压制标识

        // 构造函数
        public MainWindow()
        {
            InitializeComponent();

            _doubaoService = new doubao_service_chat();
            AppLogger.Info("豆包知识库服务初始化完成，等待语音问题输入。");

            // 测试文字转语音
            // 【一次性测试：不保留实例，不影响现有结构】
            _ = Task.Run(() =>
            {
                try
                {
                    var svc = new AikitSayService();
                    svc.Init();
                    svc.Speak("这是一次测试播报，系统就绪。"); // 想换文案可改这里
                    svc.Uninit();
                }
                catch (Exception ex)
                {
                    // 可选：如果你有 AppLogger
                    AppLogger.Info($"TTS 测试失败：{ex.Message}");
                }


            });


            // 其他
            _appConfig = AppConfig.Load(null);
            _audioDuckingConfig = _appConfig.AudioDucking;

            var startupDirectory = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(startupDirectory))
            {
                startupDirectory = Directory.GetCurrentDirectory();
            }

            _mediaCacheDirectory = EnsureMediaCacheDirectory(startupDirectory, out var cacheInitWarning);

            var logDirectory = Path.Combine(startupDirectory, "Logs");
            AppLogger.Initialize(logDirectory);
            AppLogger.Info($"应用启动，媒体缓存目录: {_mediaCacheDirectory}");
            if (!string.IsNullOrWhiteSpace(cacheInitWarning))
            {
                AppLogger.Warn(cacheInitWarning);
            }

            Core.Initialize();

            _libVlc = new LibVLC();
            _player = new LibVLCSharp.Shared.MediaPlayer(_libVlc);
            _player.EndReached += OnPlayerEndReached;

            _player.Volume = 60;
            _userPreferredVolume = _player.Volume;
            SetPlayerVolume(_player.Volume);

            VideoView.MediaPlayer = _player;

            _player.Volume = 60;
            SldVolume.Value = _player.Volume;

            LocationChanged += (_, __) => UpdateOverlayBounds();
            SizeChanged += (_, __) => UpdateOverlayBounds();
            StateChanged += (_, __) => UpdateOverlayBounds();
            Loaded += OnMainWindowLoaded;
            Closed += OnMainWindowClosed;

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (_, __) => _overlay?.UpdateTime($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _clockTimer.Start();

            _petTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _petTimer.Tick += (_, __) =>
            {
                _angle = (_angle + 1) % 360;
                _overlay?.UpdatePetRotation(_angle);
            };
            _petTimer.Start();

            _volumeRestoreTimer = new DispatcherTimer
            {
                Interval = _audioDuckingConfig.RestoreDelay
            };
            _volumeRestoreTimer.Tick += (_, __) => RestorePlayerVolume();

            _transcriptionResetTimer = new DispatcherTimer
            {
                Interval = TranscriptionAutoHideDelay
            };
            _transcriptionResetTimer.Tick += (_, __) => ResetTranscriptionDisplay();

            _aiChatInactivityTimer = new DispatcherTimer
            {
                Interval = AiChatInactivityTimeout
            };
            _aiChatInactivityTimer.Tick += OnAiChatInactivityTimeout;

            AppLogger.Info("已移除 System.Speech 蓝猫一号的 唤醒逻辑，避免与讯飞唤醒功能冲突。");
            InitializeMqttService();
            _bobaoService = BuildBobaoService(startupDirectory);
            if (_bobaoService == null)
            {
                AppLogger.Warn("语音播报服务未初始化，后续语音转写功能将不可用。");
            }

            _wakeService = new AikitWakeService(_appConfig.Wake.SdkDirectory);
            InitializeWakeService();




        }


        private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnMainWindowLoaded;

            // [修复] 去除残留 diff 标记与错位的大括号；将初始化 Overlay 的逻辑包在 BeginInvoke 中正确闭合
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_overlay == null)
                {
                    _overlay = new OverlayWindow
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual
                    };
                }

                if (_overlay.Owner != this)
                {
                    _overlay.Owner = this;
                }

                if (!_overlay.IsVisible)
                {
                    _overlay.Show();
                }

                // 显示后更新一次位置与尺寸（你原逻辑已有绑定事件）
                UpdateOverlayBounds();

                // 如需首次展示天气的示例数据
                LoadWeatherMock();
            }));
        }

        // [修复] 将被截断的“打开文件选择”片段补入到点击事件中，保持原有 PlayFile 调用不变
        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择视频文件",
                Filter = "所有文件|*.*|MP4 文件|*.mp4|MKV 文件|*.mkv|AVI 文件|*.avi"
            };
            if (dlg.ShowDialog() == true)
            {
                PlayFile(dlg.FileName);
            }
        }

        public void PlayFile(string path)
        {
            if (!File.Exists(path))
            {
                AppLogger.Warn($"尝试播放的本地文件不存在: {path}");
                return;
            }

            lock (_playbackStateLock)
            {
                // 记录当前正在播放的本地文件路径，EndReached 时优先重播该文件
                _currentLocalMediaPath = path;
                _currentRemoteMediaUrl = null;
            }

            AppLogger.Info($"开始播放本地文件: {path}");
            // 修改为循环播放（优先本地文件）
            using var media = new Media(_libVlc, new Uri(path));
            media.AddOption(":input-repeat=-1");
            _player.Play(media);
        }

        /// <summary>
        /// 播放远程媒体资源，使用 LibVLC 直接拉取流数据。
        /// </summary>
        /// <param name="url">媒体的 HTTP(s) 地址。</param>
        private void PlayRemoteMedia(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                AppLogger.Warn("PlayRemoteMedia 收到空的 URL。");
                return;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                AppLogger.Warn($"无法解析的远程媒体地址: {url}");
                return;
            }

            lock (_playbackStateLock)
            {
                // 记录当前远程流地址，若本地缓存不可用则在播放结束后重新拉流
                _currentRemoteMediaUrl = url;
                _currentLocalMediaPath = null;
            }

            AppLogger.Info($"开始在线播放远程媒体: {url}");
            // 修改为循环播放（优先本地文件）
            using var media = new Media(_libVlc, uri);
            media.AddOption(":input-repeat=-1");
            _player.Play(media);
        }
        private void BtnPlay_Click(object sender, RoutedEventArgs e) => _player.SetPause(false);

        private void BtnPause_Click(object sender, RoutedEventArgs e) => _player.Pause();

        private void BtnStop_Click(object sender, RoutedEventArgs e) => _player.Stop();

        private void SldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSliderCallback)
            {
                return;
            }

            var desiredVolume = (int)e.NewValue;
            _userPreferredVolume = desiredVolume;

            if (_isDuckingAudio)
            {
                return;
            }

            SetPlayerVolume(desiredVolume, updateSlider: false);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }

            if (e.Key == Key.F1)
            {
                // F1 快捷键：立即显示视频通话
                ShowVideoCallWindow();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Space)
            {
                if (_player.IsPlaying)
                {
                    _player.Pause();
                }
                else
                {
                    _player.SetPause(false);
                }
            }

            if (e.Key == Key.M)
            {
                _player.Mute = !_player.Mute;
            }
        }
  
        private void LoadWeatherMock()
        {
            _overlay?.UpdateWeather("城市: 示例市", "天气: 多云转晴，西北风3级", "气温: 22℃");
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _overlay?.Close();
            if (_videoCallWindow != null)
            {
                _videoCallWindow.Close();
                _videoCallWindow = null;
            }
            _player?.Stop();
            if (_player != null)
            {
                // 解除事件订阅，避免窗口销毁后仍收到回调
                _player.EndReached -= OnPlayerEndReached;
            }
            _player?.Dispose();
            _libVlc?.Dispose();

            EndAudioDucking(DuckingReasonAiChat);
            EndAudioDucking(DuckingReasonTts);
            EndAudioDucking(DuckingReasonVideoCall);
            _volumeRestoreTimer.Stop();
            _transcriptionResetTimer.Stop();
            _aiChatInactivityTimer.Stop();
        }

        // 说明：此方法在你的构造函数事件绑定中被频繁调用，确保存在
        private void UpdateOverlayBounds()
        {
            if (_overlay == null) return;
            // 根据主窗体位置和大小更新叠加窗体（保持你原有的布局策略，如果已有实现可用你自己的）
            _overlay.Left = this.Left;
            _overlay.Top = this.Top;
            _overlay.Width = this.ActualWidth;
            _overlay.Height = this.ActualHeight;
        }


        // 识别到声音 压低音乐  备用
        //private void BeginAudioDucking()
        //{
        //    Dispatcher.BeginInvoke(new Action(() =>
        //    {
        //        _volumeRestoreTimer.Stop();

        //        if (!_isDuckingAudio)
        //        {
        //            _isDuckingAudio = true;
        //            var duckedVolume = Math.Max(5, _userPreferredVolume / 4);
        //            SetPlayerVolume(duckedVolume, updateUserPreferred: false);
        //        }

        //        ScheduleVolumeRestore();
        //    }));
        //}


        /// <summary>
        /// 恢复播放器音量到用户设定值并记录恢复动作，防止压制状态残留。
        /// </summary>
        private void RestorePlayerVolume()
        {
            _volumeRestoreTimer.Stop();

            if (!_isDuckingAudio)
            {
                return;
            }

            _isDuckingAudio = false;
            SetPlayerVolume(_userPreferredVolume);
        }
        /// <summary>
        /// 调整播放器音量，支持同时刷新滑块与用户偏好值，确保界面状态同步。
        /// </summary>
        /// <param name="volume">目标音量（0-100）。</param>
        /// <param name="updateSlider">指示是否同步更新 UI 滑块。</param>
        /// <param name="updateUserPreferred">指示是否重写用户偏好音量。</param>
        private void SetPlayerVolume(int volume, bool updateSlider = true, bool updateUserPreferred = true)
        {
            volume = Math.Clamp(volume, 0, 100);

            if (_player.Volume != volume)
            {
                AppLogger.Info($"准备调整播放器音量，当前值: {_player.Volume}，目标值: {volume}。");
                _player.Volume = volume;
            }

            if (updateSlider)
            {
                _suppressSliderCallback = true;
                SldVolume.Value = volume;
                _suppressSliderCallback = false;
            }

            if (updateUserPreferred)
            {
                _userPreferredVolume = volume;
            }
        }
        /// <summary>
        /// 启动音量压制逻辑，根据原因标记确保背景音乐被压低。
        /// </summary>
        /// <param name="reason">触发压制的原因标识。</param>
        private void BeginAudioDucking(string reason)
        {
            if (!_audioDuckingConfig.Enabled)
            {
                AppLogger.Info($"音量压制已禁用，忽略原因: {reason}");
                return;
            }

            lock (_audioDuckingLock)
            {
                if (_activeAudioDuckingReasons.Add(reason))
                {
                    AppLogger.Info($"添加音量压制原因: {reason}。");
                }
                else
                {
                    AppLogger.Info($"音量压制原因已存在: {reason}，保持当前状态。");
                }

                ApplyAudioDuckingStateLocked();
            }
        }

        /// <summary>
        /// 取消指定的音量压制原因，必要时恢复原始音量。
        /// </summary>
        /// <param name="reason">需要移除的压制原因。</param>
        private void EndAudioDucking(string reason)
        {
            lock (_audioDuckingLock)
            {
                if (_activeAudioDuckingReasons.Remove(reason))
                {
                    AppLogger.Info($"移除音量压制原因: {reason}。");
                }
                else
                {
                    AppLogger.Info($"尝试移除不存在的音量压制原因: {reason}。");
                }

                ApplyAudioDuckingStateLocked();
            }
        }

        /// <summary>
        /// 根据当前压制原因数量切换音量压制状态，内部需在锁内调用。
        /// </summary>
        private void ApplyAudioDuckingStateLocked()
        {
            var shouldDuck = _activeAudioDuckingReasons.Count > 0;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _volumeRestoreTimer.Stop();

                if (shouldDuck)
                {
                    if (!_isDuckingAudio)
                    {
                        _isDuckingAudio = true;
                        var targetPercentage = Math.Clamp(_audioDuckingConfig.TargetVolumePercentage, 0, 100);
                        var duckedVolume = CalculateDuckedVolume(targetPercentage);
                        AppLogger.Info($"进入音量压制状态，原音量: {_userPreferredVolume}，目标百分比: {targetPercentage}%，压制后音量: {duckedVolume}。");
                        SetPlayerVolume(duckedVolume, updateUserPreferred: false);
                    }
                    else
                    {
                        AppLogger.Info("保持音量压制状态，等待相关流程结束。");
                    }
                }
                else if (_isDuckingAudio)
                {
                    _isDuckingAudio = false;
                    AppLogger.Info($"音量压制原因清空，恢复用户设定音量: {_userPreferredVolume}。");
                    SetPlayerVolume(_userPreferredVolume);
                }
            }));
        }

        /// <summary>
        /// 根据配置的百分比计算压制后的音量，避免结果高于当前用户音量。
        /// </summary>
        /// <param name="targetPercentage">配置的音量百分比。</param>
        /// <returns>压制后的音量值。</returns>
        private int CalculateDuckedVolume(int targetPercentage)
        {
            if (_userPreferredVolume <= 0 || targetPercentage <= 0)
            {
                AppLogger.Info("压制百分比为 0 或用户音量为 0，直接静音处理。");
                return 0;
            }

            var scaledVolume = (int)Math.Round(_userPreferredVolume * targetPercentage / 100.0);
            scaledVolume = Math.Clamp(scaledVolume, 0, _userPreferredVolume);
            AppLogger.Info($"根据压制百分比计算音量，百分比: {targetPercentage}%，结果: {scaledVolume}。");
            return scaledVolume;
        }

        /// <summary>
        /// 启动 AI 问答弹窗的语音空闲计时，若超时将关闭弹窗。
        /// </summary>
        private void StartAiChatInactivityCountdown()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _aiChatInactivityTimer.Stop();
                _aiChatInactivityTimer.Interval = AiChatInactivityTimeout;
                _aiChatInactivityTimer.Start();
                AppLogger.Info($"AI 问答弹窗监听阶段开始计时，{AiChatInactivityTimeout.TotalSeconds} 秒内无语音将自动关闭。");
            }));
        }

        /// <summary>
        /// 停止 AI 问答弹窗的语音空闲计时，防止误触自动关闭。
        /// </summary>
        private void StopAiChatInactivityCountdown()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_aiChatInactivityTimer.IsEnabled)
                {
                    _aiChatInactivityTimer.Stop();
                    AppLogger.Info("AI 问答弹窗监听计时器已停止。");
                }
            }));
        }

        /// <summary>
        /// AI 问答弹窗在监听阶段超时未检测到语音时的回调。
        /// </summary>
        private void OnAiChatInactivityTimeout(object? sender, EventArgs e)
        {
            _aiChatInactivityTimer.Stop();
            AppLogger.Warn("AI 问答弹窗监听阶段 5 秒内未检测到语音输入，将自动关闭。");
            ResetAiChatExpectation();
            EndAudioDucking(DuckingReasonAiChat);
            EndAudioDucking(DuckingReasonTts);
            _aiChatWindow?.Close();
        }

        /// <summary>
        /// 初始化 MQTT 服务，订阅任务下发主题。
        /// </summary>
        private void InitializeMqttService()
        {
            if (!_appConfig.Mqtt.Enabled)
            {
                AppLogger.Info("MQTT 功能未启用，跳过初始化。");
                return;
            }

            if (string.IsNullOrWhiteSpace(_appConfig.Mqtt.ServerUri))
            {
                AppLogger.Warn("MQTT ServerUri 未配置，跳过初始化。");
                return;
            }

            _mqttService = new MqttTaskService(_appConfig.Mqtt);
            _mqttService.RemoteMediaTaskReceived += OnRemoteMediaTaskReceived;

            _ = _mqttService.StartAsync().ContinueWith(task =>
            {
                if (task.Exception != null)
                {
                    var message = task.Exception.GetBaseException().Message;
                    AppLogger.Error(task.Exception, $"MQTT 连接失败: {message}");
                }
                else
                {
                    AppLogger.Info("MQTT 服务启动完成，等待远程媒体任务。");
                }
            }, TaskScheduler.Default);
        }

        /// <summary>
        /// MQTT 收到远程媒体任务后的第一时间响应。
        /// </summary>
        private void OnRemoteMediaTaskReceived(object? sender, RemoteMediaTask task)
        {
            AppLogger.Info($"后台线程接收到远程媒体任务: {task.JobId ?? task.Media?.MediaId ?? "未知"}");
            _ = Task.Run(() => HandleRemoteMediaTaskAsync(task));
        }

        /// <summary>
        /// 处理单个远程媒体任务：优先播放本地缓存，再回落到在线播放并触发缓存。
        /// </summary>
        private async Task HandleRemoteMediaTaskAsync(RemoteMediaTask task)
        {
            try
            {
                AppLogger.Info($"开始处理远程媒体任务，JobId: {task.JobId}, MediaId: {task.Media?.MediaId}");
                var media = task.Media;

                await Dispatcher.InvokeAsync(() => ShowIncomingMediaNotification(task));

                var cached = await TryGetCachedMediaAsync(media).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(cached))
                {
                    AppLogger.Info($"找到可用的本地缓存，直接播放: {cached}");
                    await Dispatcher.InvokeAsync(() => PlayFile(cached));
                    return;
                }

                var playbackUrl = !string.IsNullOrWhiteSpace(media.AccessibleUrl)
                    ? media.AccessibleUrl
                    : media.DownloadUrl;

                if (!string.IsNullOrWhiteSpace(playbackUrl))
                {
                    AppLogger.Info($"未命中缓存，先在线播放: {playbackUrl}");
                    await Dispatcher.InvokeAsync(() => PlayRemoteMedia(playbackUrl!));
                }
                else
                {
                    AppLogger.Warn("任务中缺失可用的在线播放地址。");
                }

                AppLogger.Info("开始后台缓存远程媒体文件。");
                var cacheTask = CacheMediaAsync(task);
                _ = cacheTask.ContinueWith(async t =>
                {
                    if (t.IsFaulted)
                    {
                        AppLogger.Error(t.Exception!.GetBaseException(), "后台缓存任务失败。");
                        return;
                    }

                    var cachedPath = t.Result;
                    if (string.IsNullOrEmpty(cachedPath))
                    {
                        AppLogger.Warn("后台缓存任务完成但未生成有效文件。");
                        return;
                    }

                    AppLogger.Info($"缓存完成，切换为本地播放: {cachedPath}");
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
                        {
                            PlayFile(cachedPath);
                        }
                    });
                }, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "处理远程媒体任务失败。");
            }
        }

        private void OnPlayerEndReached(object? sender, EventArgs e)
        {
            string? localPath;
            string? remoteUrl;

            lock (_playbackStateLock)
            {
                // 拷贝一次播放状态快照，避免事件线程与主线程竞争
                localPath = _currentLocalMediaPath;
                remoteUrl = _currentRemoteMediaUrl;
            }

            if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
            {
                AppLogger.Info($"播放结束，检测到本地缓存可用，重新播放: {localPath}");
                // 如果本地缓存已就绪，则重新调用 PlayFile 形成稳定的循环
                Dispatcher.InvokeAsync(() => PlayFile(localPath));
                return;
            }

            if (!string.IsNullOrEmpty(remoteUrl))
            {
                AppLogger.Info($"播放结束，本地缓存不可用，重新拉流: {remoteUrl}");
                // 若本地缓存暂不可用，则回退到重新拉流，保持播放不断档
                Dispatcher.InvokeAsync(() => PlayRemoteMedia(remoteUrl));
            }
        }

        private void ShowIncomingMediaNotification(RemoteMediaTask task)
        {
            var media = task.Media;
            string? displayName = null;

            if (!string.IsNullOrWhiteSpace(media?.MediaId))
            {
                displayName = media!.MediaId;
            }
            else if (!string.IsNullOrWhiteSpace(media?.JobId))
            {
                displayName = media!.JobId;
            }
            else if (!string.IsNullOrWhiteSpace(task.JobId))
            {
                displayName = task.JobId;
            }

            var message = string.IsNullOrWhiteSpace(displayName)
                ? "收到新的远程视频任务，即将开始播放。"
                : $"收到新的远程视频任务：{displayName}，即将开始播放。";

            if (_overlay != null)
            {
                _overlay.ShowNotification(message, TimeSpan.FromSeconds(6));
            }
            else
            {
                MessageBox.Show(message, "远程视频", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// 检查并验证缓存文件是否可用。
        /// </summary>
        private async Task<string?> TryGetCachedMediaAsync(RemoteMediaInfo media)
        {
            var cachePath = BuildCachePath(media);
            if (!File.Exists(cachePath))
            {
                AppLogger.Info($"缓存文件不存在: {cachePath}");
                return null;
            }

            if (media.FileSize.HasValue)
            {
                var length = new FileInfo(cachePath).Length;
                if (length != media.FileSize.Value)
                {
                    AppLogger.Warn($"缓存文件大小不匹配，删除重下。期望: {media.FileSize.Value}, 实际: {length}");
                    try
                    {
                        File.Delete(cachePath);
                    }
                    catch (IOException)
                    {
                    }

                    return null;
                }
            }

            if (!string.IsNullOrWhiteSpace(media.FileHash))
            {
                var valid = await VerifyFileHashAsync(cachePath, media.FileHash!).ConfigureAwait(false);
                if (!valid)
                {
                    AppLogger.Warn("缓存文件哈希校验失败，将删除并重新下载。");
                    try
                    {
                        File.Delete(cachePath);
                    }
                    catch (IOException)
                    {
                    }

                    return null;
                }
            }

            return cachePath;
        }

        /// <summary>
        /// 后台下载媒体文件并写入本地缓存。
        /// </summary>
        private async Task<string?> CacheMediaAsync(RemoteMediaTask task)
        {
            var media = task.Media;
            var downloadUrl = !string.IsNullOrWhiteSpace(media.DownloadUrl)
                ? media.DownloadUrl
                : media.AccessibleUrl;

            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                AppLogger.Warn("缓存流程中缺失下载地址。");
                return null;
            }

            Directory.CreateDirectory(_mediaCacheDirectory);

            var existing = await TryGetCachedMediaAsync(media).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(existing))
            {
                AppLogger.Info($"缓存检查时发现已有文件: {existing}");
                return existing;
            }

            var targetPath = BuildCachePath(media);
            var cacheKey = targetPath;

            if (_mediaDownloadTasks.TryGetValue(cacheKey, out var existingTask))
            {
                AppLogger.Info($"已有正在运行的缓存任务，等待完成: {cacheKey}");
                return await existingTask.ConfigureAwait(false);
            }

            var downloadTask = DownloadAndStoreAsync(downloadUrl!, targetPath, media);
            if (!_mediaDownloadTasks.TryAdd(cacheKey, downloadTask))
            {
                downloadTask = _mediaDownloadTasks[cacheKey];
            }

            try
            {
                var successPath = await downloadTask.ConfigureAwait(false);
                if (!string.IsNullOrEmpty(successPath))
                {
                    AppLogger.Info($"媒体缓存写入完成: {successPath}");
                    return successPath;
                }
                else
                {
                    AppLogger.Warn("媒体缓存写入任务结束但未返回文件路径。");
                }
            }
            finally
            {
                _mediaDownloadTasks.TryRemove(cacheKey, out _);
            }

            return await TryGetCachedMediaAsync(media).ConfigureAwait(false);
        }

        /// <summary>
        /// 拉取远程媒体并在校验后保存到目标路径。
        /// </summary>
        private async Task<string?> DownloadAndStoreAsync(string url, string targetPath, RemoteMediaInfo media)
        {
            var tempPath = targetPath + ".downloading";

            try
            {
                AppLogger.Info($"开始下载媒体缓存: {url} -> {tempPath}");
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                AppLogger.Info($"媒体缓存 HTTP 响应: {(int)response.StatusCode} {response.StatusCode}; Content-Length: {response.Content.Headers.ContentLength?.ToString() ?? "未知"}; 临时路径: {tempPath}");

                await using var remoteStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    await remoteStream.CopyToAsync(fileStream).ConfigureAwait(false);
                    await fileStream.FlushAsync().ConfigureAwait(false);
                }

                var writtenLength = new FileInfo(tempPath).Length;
                AppLogger.Info($"媒体缓存写入完成，实际写入 {writtenLength} 字节: {tempPath}");

                if (media.FileSize.HasValue)
                {
                    var actualLength = new FileInfo(tempPath).Length;
                    if (actualLength != media.FileSize.Value)
                    {
                        throw new InvalidOperationException($"文件大小与描述不一致（期望 {media.FileSize.Value}，实际 {actualLength}）。");
                    }
                }

                if (!string.IsNullOrWhiteSpace(media.FileHash))
                {
                    var valid = await VerifyFileHashAsync(tempPath, media.FileHash!).ConfigureAwait(false);
                    if (!valid)
                    {
                        throw new InvalidOperationException("文件哈希校验失败。");
                    }
                }

                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                File.Move(tempPath, targetPath);
                AppLogger.Info($"媒体缓存文件已落盘: {targetPath}");
                return targetPath;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"缓存媒体文件失败: {ex.Message}");
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (IOException)
                {
                }
            }

            return null;
        }

        /// <summary>
        /// 根据媒体标识生成稳定的缓存路径。
        /// </summary>
        private string BuildCachePath(RemoteMediaInfo media)
        {
            var key = !string.IsNullOrWhiteSpace(media.FileHash)
                ? media.FileHash
                : (!string.IsNullOrWhiteSpace(media.MediaId) ? media.MediaId : null);

            if (string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(media.JobId))
            {
                key = media.JobId;
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                var source = media.DownloadUrl ?? media.AccessibleUrl ?? media.JobId ?? "remote_media";
                key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
            }

            key = SanitizeFileName(key);
            var extension = ResolveMediaExtension(media);
            return Path.Combine(_mediaCacheDirectory, $"{key}{extension}");
        }

        /// <summary>
        /// 从媒体下载地址推断文件扩展名，默认使用 mp4。
        /// </summary>
        private static string ResolveMediaExtension(RemoteMediaInfo media)
        {
            static string? TryGetExtension(string? url)
            {
                if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    return null;
                }

                var ext = Path.GetExtension(uri.AbsolutePath);
                return string.IsNullOrWhiteSpace(ext) ? null : ext;
            }

            return TryGetExtension(media.DownloadUrl) ?? TryGetExtension(media.AccessibleUrl) ?? ".mp4";
        }

        /// <summary>
        /// 将非法文件名字符替换为下划线。
        /// </summary>
        private static string SanitizeFileName(string value)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            return value;
        }

        private static string EnsureMediaCacheDirectory(string startupDirectory, out string? warningMessage)
        {
            warningMessage = null;
            var failures = new List<string>();
            var normalizedStartup = string.IsNullOrWhiteSpace(startupDirectory)
                ? Directory.GetCurrentDirectory()
                : startupDirectory;

            var startupCachePath = Path.Combine(normalizedStartup, "MediaCache");

            try
            {
                Directory.CreateDirectory(startupCachePath);
                return startupCachePath;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                failures.Add($"{startupCachePath} ({ex.Message})");
            }

            var candidates = new List<string?>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                AppDomain.CurrentDomain.BaseDirectory
            };

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var cachePath = Path.Combine(candidate, "WpfVideoPet", "MediaCache");

                try
                {
                    Directory.CreateDirectory(cachePath);

                    if (failures.Count > 0)
                    {
                        warningMessage = $"无法在预期目录创建媒体缓存，已回退至 {cachePath}。详细信息: {string.Join("; ", failures)}";
                    }

                    return cachePath;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    failures.Add($"{cachePath} ({ex.Message})");
                }
            }

            if (OperatingSystem.IsWindows())
            {
                const string windowsFallback = @"C:\\WpfVideoPet\\MediaCache";
                try
                {
                    Directory.CreateDirectory(windowsFallback);

                    warningMessage = failures.Count > 0
                        ? $"媒体缓存目录最终回退到 {windowsFallback}。无法创建的位置: {string.Join("; ", failures)}"
                        : $"无法确定本地应用数据目录，媒体缓存目录已设置为 {windowsFallback}。";

                    return windowsFallback;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    failures.Add($"{windowsFallback} ({ex.Message})");
                }
            }

            var tempCache = Path.Combine(Path.GetTempPath(), "WpfVideoPet", "MediaCache");
            Directory.CreateDirectory(tempCache);

            warningMessage = failures.Count > 0
                ? $"媒体缓存目录最终回退到临时目录 {tempCache}。无法创建的位置: {string.Join("; ", failures)}"
                : $"无法确定本地应用数据目录，媒体缓存目录已设置为临时路径 {tempCache}。";

            return tempCache;
        }

        /// <summary>
        /// 校验文件的哈希是否与预期一致。
        /// </summary>
        private static async Task<bool> VerifyFileHashAsync(string filePath, string expectedHash)
        {
            if (string.IsNullOrWhiteSpace(expectedHash))
            {
                return true;
            }

            var trimmed = expectedHash.Trim();
            var algorithmHint = ExtractAlgorithmName(trimmed, out var remaining);
            var hexCandidate = RemoveCommonSeparators(remaining);
            var useBase64 = !IsHexString(hexCandidate);
            var normalizedExpected = useBase64 ? NormalizeBase64(remaining) : hexCandidate;

            using var algorithm = ResolveHashAlgorithm(algorithmHint, normalizedExpected.Length, useBase64) ?? SHA256.Create();

            try
            {
                await using var stream = File.OpenRead(filePath);
                var hash = await algorithm.ComputeHashAsync(stream).ConfigureAwait(false);
                var actual = useBase64 ? Convert.ToBase64String(hash) : Convert.ToHexString(hash);
                var comparison = useBase64 ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                var match = string.Equals(actual, normalizedExpected, comparison);

                AppLogger.Info($"哈希校验: 文件={filePath}, 算法={(string.IsNullOrWhiteSpace(algorithmHint) ? algorithm.GetType().Name : algorithmHint)}, 格式={(useBase64 ? "Base64" : "Hex")}, 期望={normalizedExpected}, 实际={actual}, 结果={(match ? "通过" : "失败")}");

                return match;
            }
            catch (IOException ex)
            {
                AppLogger.Error(ex, $"读取文件进行哈希校验时发生 IO 异常: {filePath}");
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLogger.Error(ex, $"访问文件进行哈希校验时被拒绝: {filePath}");
                return false;
            }
        }
        private static string ExtractAlgorithmName(string value, out string expected)
        {
            var separatorIndex = value.IndexOf(':');
            if (separatorIndex > 0 && separatorIndex < value.Length - 1)
            {
                var name = value[..separatorIndex].Trim();
                expected = value[(separatorIndex + 1)..].Trim();
                return name;
            }

            expected = value;
            return string.Empty;
        }

        private static string RemoveCommonSeparators(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            var builder = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                if (ch == '-' || ch == ' ' || ch == '_')
                {
                    continue;
                }

                builder.Append(ch);
            }

            return builder.ToString();
        }

        private static bool IsHexString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            if ((value.Length & 1) != 0)
            {
                return false;
            }

            foreach (var ch in value)
            {
                if (!Uri.IsHexDigit(ch))
                {
                    return false;
                }
            }

            return true;
        }

        private static string NormalizeBase64(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim();
            var builder = new StringBuilder(trimmed.Length + 2);
            foreach (var ch in trimmed)
            {
                if (char.IsWhiteSpace(ch))
                {
                    continue;
                }

                builder.Append(ch switch
                {
                    '-' => '+',
                    '_' => '/',
                    _ => ch,
                });
            }

            var normalized = builder.ToString();
            var padding = normalized.Length % 4;
            if (padding > 0)
            {
                normalized = normalized.PadRight(normalized.Length + (4 - padding), '=');
            }

            return normalized;
        }

        private static HashAlgorithm? ResolveHashAlgorithm(string algorithmHint, int expectedLength, bool base64)
        {
            var algorithm = CreateHashAlgorithm(algorithmHint);
            if (algorithm != null)
            {
                return algorithm;
            }

            return (base64, expectedLength) switch
            {
                (false, 32) => MD5.Create(),
                (false, 40) => SHA1.Create(),
                (false, 64) => SHA256.Create(),
                (false, 96) => SHA384.Create(),
                (false, 128) => SHA512.Create(),
                (true, 24) => MD5.Create(),
                (true, 28) => SHA1.Create(),
                (true, 44) => SHA256.Create(),
                (true, 64) => SHA384.Create(),
                (true, 88) => SHA512.Create(),
                _ => null,
            };
        }

        private static HashAlgorithm? CreateHashAlgorithm(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var normalized = name.Trim().ToUpperInvariant().Replace("-", string.Empty);
            return normalized switch
            {
                "MD5" => MD5.Create(),
                "SHA1" => SHA1.Create(),
                "SHA256" => SHA256.Create(),
                "SHA384" => SHA384.Create(),
                "SHA512" => SHA512.Create(),
                _ => null,
            };
        }

        /// <summary>
        /// 主窗口关闭时释放 MQTT 与网络资源。
        /// </summary>
        private async void OnMainWindowClosed(object? sender, EventArgs e)
        {
            Closed -= OnMainWindowClosed;

            _wakeService.WakeTriggered -= OnWakeTriggered;
            _wakeService.WakeKeywordRecognized -= OnWakeKeywordRecognized;
            _wakeService.SpeechRecognitionRequested -= OnSpeechRecognitionRequested;
            _wakeService.Dispose();

            if (_bobaoService != null)
            {
                _bobaoService.InterimResultReceived -= OnBobaoInterimResult;
                _bobaoService.RecognitionCompleted -= OnBobaoRecognitionCompleted;
                _bobaoService.RecognitionFailed -= OnBobaoRecognitionFailed;
                lock (_speechRecognitionStateLock)
                {
                    _speechRecognitionCts?.Cancel();
                }

                _bobaoService.Dispose();
            }

            if (_mqttService != null)
            {
                Closed -= OnMainWindowClosed;

                if (_mqttService != null)
                {
                    _mqttService.RemoteMediaTaskReceived -= OnRemoteMediaTaskReceived;

                    try
                    {
                        await _mqttService.DisposeAsync().ConfigureAwait(false);
                        AppLogger.Info("MQTT 服务已正常释放。");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error(ex, $"释放 MQTT 服务时发生异常: {ex.Message}");
                    }
                }

                _httpClient.Dispose();
                AppLogger.Info("主窗口关闭，HTTP 客户端资源已释放。");

            }
        }

        private void InitializeWakeService()
        {
            _wakeService.WakeTriggered += OnWakeTriggered;
            _wakeService.WakeKeywordRecognized += OnWakeKeywordRecognized;
            _wakeService.SpeechRecognitionRequested += OnSpeechRecognitionRequested;

            if (!_wakeService.Start())
            {
                AppLogger.Warn("Aikit 唤醒服务未能成功启动，语音唤醒将不可用。");
            }
        }

        /// <summary>
        /// 构建语音播报服务并挂载事件，失败时返回 null。
        /// </summary>
        /// <param name="baseDirectory">应用根目录，用于定位配置文件。</param>
        /// <returns>初始化完成的服务实例或 null。</returns>
        private AikitLIstenService? BuildBobaoService(string baseDirectory)
        {
            try
            {
                var configPath = Path.Combine(baseDirectory, "config", "aikitbobao.appsettings.json");
                AppLogger.Info($"准备从配置路径加载讯飞播报设置: {configPath}");
                var settings = AikitListenSettings.Load(configPath);
                var service = new AikitLIstenService(settings);
                service.InterimResultReceived += OnBobaoInterimResult;
                service.RecognitionCompleted += OnBobaoRecognitionCompleted;
                service.RecognitionFailed += OnBobaoRecognitionFailed;
                AppLogger.Info("讯飞播报服务初始化完成，已准备好语音转写能力。");
                return service;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"初始化讯飞播报服务失败: {ex.Message}");
                return null;
            }
        }

        private void OnWakeKeywordRecognized(object? sender, WakeKeywordEventArgs e)
        {
            var isAiKeyword = string.Equals(e.Keyword, "小黄小黄", StringComparison.Ordinal);
            if (isAiKeyword)
            {
                AppLogger.Info("识别到“小黄小黄”唤醒词，准备调度 AI 问答流程。");
            }
            else if (_expectAiAnswer)
            {
                AppLogger.Info("收到非“小黄小黄”唤醒词，重置 AI 问答等待状态。");
            }

            _expectAiAnswer = isAiKeyword;

            void Show()
            {
                if (isAiKeyword)
                {
                    PrepareAiChatWindowForListening();
                }

                if (string.IsNullOrWhiteSpace(e.NotificationMessage))
                {
                    return;
                }

                if (_overlay != null)
                {
                    _overlay.ShowNotification(e.NotificationMessage!, TimeSpan.FromSeconds(5));
                }
                else
                {
                    MessageBox.Show(this,
                        e.NotificationMessage!,
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }

            if (Dispatcher.CheckAccess())
            {
                Show();
            }
            else
            {
                Dispatcher.BeginInvoke((Action)Show);
            }
        }


        /// <summary>
        /// 唤醒服务请求启动语音识别时的回调。
        /// </summary>
        private void OnSpeechRecognitionRequested(object? sender, EventArgs e)
        {
            AppLogger.Info("收到唤醒服务的语音识别请求，开始调度播报服务。");
            StartSpeechRecognitionWorkflow();
        }

        /// <summary>
        /// 启动一次语音识别任务，若存在历史任务则先取消后重启。
        /// </summary>
        private void StartSpeechRecognitionWorkflow()
        {
            if (_bobaoService == null)
            {
                AppLogger.Warn("语音播报服务不可用，无法执行语音识别任务。");
                return;
            }

            CancellationTokenSource localCts;
            CancellationTokenSource? previousCts = null;
            lock (_speechRecognitionStateLock)
            {
                if (_speechRecognitionCts != null)
                {
                    AppLogger.Warn("检测到已有语音识别任务正在进行，将取消后重新启动新的识别流程。");
                    previousCts = _speechRecognitionCts;
                }

                _speechRecognitionCts = new CancellationTokenSource();
                localCts = _speechRecognitionCts;
            }

            if (previousCts != null)
            {
                AppLogger.Info("开始取消上一轮语音识别任务，确保新的任务可以顺利启动。");
                try
                {
                    previousCts.Cancel();
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, $"取消历史语音识别任务时发生异常: {ex.Message}");
                }
                finally
                {
                    previousCts.Dispose();
                }
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _transcriptionResetTimer.Stop();
                AppLogger.Info("已重置语音识别界面隐藏定时器，准备展示最新状态。");
            }));

            UpdateTranscriptionDisplay("语音识别中...", "正在倾听，请稍候。");

            _ = Task.Run(async () =>
            {
                try
                {
                    AppLogger.Info("开始调用讯飞播报服务进行实时语音识别。");
                    var result = await _bobaoService.TranscribeOnceAsync(localCts.Token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(result))
                    {
                        UpdateTranscriptionDisplay("识别结束", "未识别到有效语音内容。");
                        ScheduleTranscriptionReset();
                    }
                }
                catch (OperationCanceledException)
                {
                    AppLogger.Warn("语音识别任务在完成前被取消。");
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, $"语音识别流程执行异常: {ex.Message}");
                    UpdateTranscriptionDisplay("识别失败", $"发生异常: {ex.Message}");
                }
                finally
                {
                    lock (_speechRecognitionStateLock)
                    {
                        if (ReferenceEquals(_speechRecognitionCts, localCts))
                        {
                            _speechRecognitionCts?.Dispose();
                            _speechRecognitionCts = null;
                        }
                    }
                }
            });
        }
        /// <summary>
        /// 播报服务推送中间结果时更新界面。
        /// </summary>
        private void OnBobaoInterimResult(object? sender, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            UpdateTranscriptionDisplay("语音识别中...", text);
            StopAiChatInactivityCountdown();

            if (_expectAiAnswer)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    EnsureAiChatWindow();
                    _aiChatWindow?.SetQuestion(text);
                }));
            }
        }

        /// <summary>
        /// 播报服务完成识别后展示最终结果。
        /// </summary>
        private void OnBobaoRecognitionCompleted(object? sender, string text)
        {
            var finalText = text?.Trim(); // 去除识别结果首尾空白，提升展示效果。

            if (string.IsNullOrWhiteSpace(finalText))
            {
                AppLogger.Info("语音识别完成但未返回有效文本，保持默认提示。");
                UpdateTranscriptionDisplay("识别结束", "未识别到有效语音内容。");
                StopAiChatInactivityCountdown();

                if (_expectAiAnswer)
                {
                    AppLogger.Warn("AI 问答流程因识别结果为空而终止。");
                    ResetAiChatExpectation();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        EnsureAiChatWindow();
                        _aiChatWindow?.ShowError("未识别到有效语音内容。");
                    }));
                }
            }
            else
            {
                AppLogger.Info($"语音识别完成，最终文本: {finalText}");
                StopAiChatInactivityCountdown();
                DisplayFinalTranscriptAndMaybeStartAi(finalText!);
            }
        }
        /// <summary>
        /// 在语音识别完成后同步展示最终文本，并在需要时立即启动豆包 AI 的流式回答。
        /// </summary>
        /// <param name="finalText">已经过首尾空白裁剪的识别文本。</param>
        private void DisplayFinalTranscriptAndMaybeStartAi(string finalText)
        {
            UpdateTranscriptionDisplay("识别结果", finalText);
            AppLogger.Info("语音识别文本已展示，准备根据等待状态触发豆包回答流程。");

            if (_expectAiAnswer)
            {
                AppLogger.Info("检测到当前需调度豆包回答，立即进入流式问答。");
                StartDoubaoAnswerWorkflow(finalText);
            }
            else
            {
                AppLogger.Info("当前无需豆包回答，仅展示语音识别结果。");
            }
        }

        /// <summary>
        /// 播报服务发生异常时通知用户。
        /// </summary>
        private void OnBobaoRecognitionFailed(object? sender, string message)
        {
            AppLogger.Warn($"语音识别回调失败，返回消息: {message}");
            var content = string.IsNullOrWhiteSpace(message) ? "语音识别失败，请稍后重试。" : message;
            UpdateTranscriptionDisplay("识别失败", content);
            StopAiChatInactivityCountdown();

            if (_expectAiAnswer)
            {
                ResetAiChatExpectation();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    EnsureAiChatWindow();
                    _aiChatWindow?.ShowError(content);
                }));
            }
        }
        /// <summary>
        /// 为语音识别阶段准备 AI 问答弹窗，提示用户可以提问。
        /// </summary>
        private void PrepareAiChatWindowForListening()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                EnsureAiChatWindow();
                _aiChatWindow?.ClearAnswer();
                _aiChatWindow?.SetQuestion("蓝猫正在倾听,可以咨询法律法规相关问题 ~ ");  //初始标题
                _aiChatWindow?.ActivateWindow();
                BeginAudioDucking(DuckingReasonAiChat);
                StartAiChatInactivityCountdown();
            }));
        }
        /// <summary>
        /// 创建或重新绑定 AI 问答弹窗实例，确保其生命周期受主窗口托管。
        /// </summary>
        private void EnsureAiChatWindow()
        {
            if (_aiChatWindow != null)
            {
                BeginAudioDucking(DuckingReasonAiChat);
                return;
            }

            _aiChatWindow = new AiChatPopupWindow
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            _aiChatWindow.TtsPlaybackStarted += OnAiChatWindowTtsPlaybackStarted;
            _aiChatWindow.TtsPlaybackCompleted += OnAiChatWindowTtsPlaybackCompleted;

            _aiChatWindow.Closed += (_, _) =>
            {
                AppLogger.Info("AI 问答弹窗已关闭并释放引用。");
                StopAiChatInactivityCountdown();
                EndAudioDucking(DuckingReasonAiChat);
                EndAudioDucking(DuckingReasonTts);
                _aiChatWindow = null;
            };

            BeginAudioDucking(DuckingReasonAiChat);
            AppLogger.Info("AI 问答弹窗实例已创建并与主窗口绑定。");
        }
        /// <summary>
        /// 调用豆包知识库服务处理指定问题，并在弹窗中实时展示回答内容。
        /// </summary>
        /// <param name="question">语音识别得到的问题文本。</param>
        private void StartDoubaoAnswerWorkflow(string question)
        {
            if (string.IsNullOrWhiteSpace(question))
            {
                AppLogger.Warn("豆包回答流程收到空问题，终止执行。");
                ResetAiChatExpectation();
                return;
            }

            var normalizedQuestion = question.Trim();
            AppLogger.Info($"启动豆包回答流程，问题: {normalizedQuestion}");

            CancellationTokenSource localCts;
            CancellationTokenSource? previousCts = null;

            lock (_aiChatLock)
            {
                if (_aiAnswerCts != null)
                {
                    previousCts = _aiAnswerCts;
                }

                _aiAnswerCts = new CancellationTokenSource();
                localCts = _aiAnswerCts;
            }

            if (previousCts != null)
            {
                AppLogger.Info("检测到历史豆包回答任务，正在取消以启动新的任务。");
                try
                {
                    previousCts.Cancel();
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, $"取消历史豆包回答任务时发生异常: {ex.Message}");
                }
                finally
                {
                    previousCts.Dispose();
                }
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                EnsureAiChatWindow();
                _aiChatWindow?.ClearAnswer();
                _aiChatWindow?.SetQuestion(normalizedQuestion);
                _aiChatWindow?.ActivateWindow();
            }));

            ResetAiChatExpectation();

            _ = Task.Run(async () =>
            {
                try
                {
                    var answer = await _doubaoService.AskAsync(normalizedQuestion, delta =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            EnsureAiChatWindow();
                            _aiChatWindow?.AppendAnswer(delta);
                        }));
                    }, localCts.Token).ConfigureAwait(false);

                    Dispatcher.BeginInvoke(async () =>
                    {
                        if (!string.IsNullOrWhiteSpace(answer))
                        {
                            AppLogger.Info($"豆包回答流程完成，返回文本长度: {answer.Length}");
                        }
                        else
                        {
                            AppLogger.Warn("豆包回答流程完成，但返回文本为空。");
                        }

                        EnsureAiChatWindow();
                        if (_aiChatWindow != null)
                        {
                            await _aiChatWindow.HandleFinalAnswerAsync(answer);
                        }
                        else
                        {
                            AppLogger.Warn("AI 问答弹窗实例不存在，无法执行TTS播报流程。");
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    AppLogger.Warn("豆包回答任务被取消。");
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, $"豆包回答流程发生异常: {ex.Message}");
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        EnsureAiChatWindow();
                        _aiChatWindow?.ShowError(ex.Message);
                    }));
                }
                finally
                {
                    lock (_aiChatLock)
                    {
                        if (ReferenceEquals(_aiAnswerCts, localCts))
                        {
                            _aiAnswerCts.Dispose();
                            _aiAnswerCts = null;
                        }
                    }
                }
            });
        }

        /// <summary>
        /// 重置 AI 问答状态，避免后续流程误判当前会话仍在进行。
        /// </summary>
        private void ResetAiChatExpectation()
        {
            _expectAiAnswer = false;
            AppLogger.Info("AI 问答等待状态已重置。");
        }

        /// <summary>
        /// AI 问答弹窗通知 TTS 播报开始时的回调，压低背景音乐。
        /// </summary>
        private void OnAiChatWindowTtsPlaybackStarted(object? sender, EventArgs e)
        {
            AppLogger.Info("AI 问答弹窗开始进行 TTS 播报，准备压低背景音乐。");
            BeginAudioDucking(DuckingReasonTts);
        }

        /// <summary>
        /// AI 问答弹窗通知 TTS 播报结束时的回调，恢复音量。
        /// </summary>
        private void OnAiChatWindowTtsPlaybackCompleted(object? sender, EventArgs e)
        {
            AppLogger.Info("AI 问答弹窗完成 TTS 播报，尝试恢复背景音乐音量。");
            EndAudioDucking(DuckingReasonTts);
        }
        /// <summary>
        /// 将语音识别文案同步到界面层，自动切换到 UI 线程。
        /// </summary>
        /// <param name="title">标题文本。</param>
        /// <param name="content">正文内容。</param>
        private void UpdateTranscriptionDisplay(string title, string content)
        {
            void Update()
            {
                if (_overlay != null)
                {
                    AppLogger.Info($"语音识别文案更新，标题: {title}，内容: {content}");
                    _overlay.ShowTranscription(title, content);
                    ScheduleTranscriptionReset(TranscriptionAutoHideDelay);
                }
            }

            if (Dispatcher.CheckAccess())
            {
                Update();
            }
            else
            {
                Dispatcher.BeginInvoke((Action)Update);
            }
        }


        private void OnWakeTriggered(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ShowVideoCallWindow));
        }
        private void ShowVideoCallWindow()
        {
            if (_videoCallWindow != null)
            {
                if (_videoCallWindow.IsVisible)
                {
                    if (_videoCallWindow.WindowState == WindowState.Minimized)
                    {
                        _videoCallWindow.WindowState = WindowState.Normal;
                    }

                    _videoCallWindow.Activate();
                    return;
                }

                _videoCallWindow.Close();
                _videoCallWindow = null;
            }

            _videoCallWindow = new VideoCallWindow
            {
                Owner = this
            };
            _videoCallWindow.Closed += (_, _) =>
            {
                AppLogger.Info("视频通话窗口已关闭，准备恢复背景音乐音量。");
                _videoCallWindow = null;
                EndAudioDucking(DuckingReasonVideoCall);
            };
            BeginAudioDucking(DuckingReasonVideoCall);
            _videoCallWindow.Show();
        }


        /// <summary>
        /// 调度隐藏语音识别界面，避免旧内容在下一次唤醒前残留。
        /// </summary>
        /// <param name="delay">自定义延迟，默认延迟为 2 秒。</param>
        private void ScheduleTranscriptionReset(TimeSpan? delay = null)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var actualDelay = delay ?? TranscriptionAutoHideDelay;
            _transcriptionResetTimer.Stop();
                _transcriptionResetTimer.Interval = actualDelay;
                _transcriptionResetTimer.Start();
                AppLogger.Info($"已计划在 {actualDelay.TotalMilliseconds} ms 后隐藏语音识别界面。");
        }));
        }

        /// <summary>
        /// 隐藏语音识别界面并记录日志。
        /// </summary>
        private void ResetTranscriptionDisplay()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ResetTranscriptionDisplay));
                return;
            }

            _transcriptionResetTimer.Stop();
            _overlay?.HideTranscription();
            AppLogger.Info("语音识别界面已隐藏，等待下一次唤醒触发。");
        }

    }
}