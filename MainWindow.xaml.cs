using LibVLCSharp.Shared;
using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Speech.Recognition;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace WpfVideoPet
{
    public partial class MainWindow : Window
    {
        private readonly LibVLC _libVlc;
        private readonly LibVLCSharp.Shared.MediaPlayer _player;
        private readonly DispatcherTimer _clockTimer;
        private readonly DispatcherTimer _petTimer;
        private double _angle;
        private OverlayWindow? _overlay;
        private VideoCallWindow? _videoCallWindow;
        private SpeechRecognitionEngine? _speechRecognizer;
        private const double WakeWordConfidenceThreshold = 0.45;
        private readonly DispatcherTimer _volumeRestoreTimer;
        private bool _suppressSliderCallback;
        private bool _isDuckingAudio;
        private int _userPreferredVolume;
        private readonly AppConfig _appConfig;
        private readonly AudioDuckingConfig _audioDuckingConfig;
        private MqttTaskService? _mqttService;
        private readonly HttpClient _httpClient = new();
        private readonly string _mediaCacheDirectory;
        private readonly ConcurrentDictionary<string, Task<string?>> _mediaDownloadTasks = new(StringComparer.OrdinalIgnoreCase); private readonly object _playbackStateLock = new();
        private string? _currentLocalMediaPath;
        private string? _currentRemoteMediaUrl;

        public MainWindow()
        {
            InitializeComponent();
            _appConfig = AppConfig.Load(null);
            _audioDuckingConfig = _appConfig.AudioDucking;
            var cacheRoot = Path.Combine(
                 Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 "WpfVideoPet",
                 "MediaCache");
            _mediaCacheDirectory = cacheRoot;
            Directory.CreateDirectory(_mediaCacheDirectory);

            var logDirectory = Path.Combine(Path.GetDirectoryName(_mediaCacheDirectory) ?? _mediaCacheDirectory, "Logs");
            AppLogger.Initialize(logDirectory);
            AppLogger.Info($"应用启动，媒体缓存目录: {_mediaCacheDirectory}");

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

            InitializeSpeechRecognition();
            InitializeMqttService();
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

        private void InitializeSpeechRecognition()
        {
            try
            {
                _speechRecognizer = new SpeechRecognitionEngine(new CultureInfo("zh-CN"));

                var choices = new Choices("蓝猫一号");
                var builder = new GrammarBuilder { Culture = _speechRecognizer.RecognizerInfo.Culture };
                builder.Append(choices);

                var grammar = new Grammar(builder);
                _speechRecognizer.LoadGrammar(grammar);

                _speechRecognizer.SpeechDetected += SpeechRecognizerOnSpeechDetected;
                _speechRecognizer.SpeechRecognitionRejected += SpeechRecognizerOnSpeechRecognitionRejected;
                _speechRecognizer.RecognizeCompleted += SpeechRecognizerOnRecognizeCompleted;
                _speechRecognizer.SpeechRecognized += SpeechRecognizerOnSpeechRecognized;
                _speechRecognizer.SetInputToDefaultAudioDevice();
                _speechRecognizer.RecognizeAsync(RecognizeMode.Multiple);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"语音识别初始化失败: {ex.Message}", "语音识别", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SpeechRecognizerOnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
        {
            if (e.Result.Confidence < WakeWordConfidenceThreshold)
            {
                Dispatcher.BeginInvoke(new Action(ScheduleVolumeRestore));
                return;
            }

            if (IsWakePhrase(e.Result.Text))
            {
                Dispatcher.BeginInvoke(new Action(ShowVideoCallWindow));
            }

            Dispatcher.BeginInvoke(new Action(ScheduleVolumeRestore));
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

            if (_speechRecognizer != null)
            {
                _speechRecognizer.SpeechRecognized -= SpeechRecognizerOnSpeechRecognized;
                _speechRecognizer.SpeechDetected -= SpeechRecognizerOnSpeechDetected;
                _speechRecognizer.SpeechRecognitionRejected -= SpeechRecognizerOnSpeechRecognitionRejected;
                _speechRecognizer.RecognizeCompleted -= SpeechRecognizerOnRecognizeCompleted;
                _speechRecognizer.RecognizeAsyncCancel();
                _speechRecognizer.Dispose();
            }

            _volumeRestoreTimer.Stop();
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

        private void SpeechRecognizerOnSpeechDetected(object? sender, SpeechDetectedEventArgs e)
        {
            // 是否压低播放音量由配置控制。
            BeginAudioDucking();
        }

        private void SpeechRecognizerOnSpeechRecognitionRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ScheduleVolumeRestore));
        }

        private void SpeechRecognizerOnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ScheduleVolumeRestore));
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


        private void BeginAudioDucking()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_audioDuckingConfig.Enabled)
                {
                    return;
                }

                _volumeRestoreTimer.Stop();

                if (!_isDuckingAudio)
                {
                    _isDuckingAudio = true;
                    var duckedVolume = Math.Max(5, _userPreferredVolume / 4);
                    SetPlayerVolume(duckedVolume, updateUserPreferred: false);
                }

                ScheduleVolumeRestore();
            }));
        }

        private static bool IsWakePhrase(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var normalized = NormalizeWakeText(text);
            if (string.Equals(normalized, "蓝猫一号", StringComparison.Ordinal))
            {
                return true;
            }

            return string.Equals(normalized, "蓝猫1号", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeWakeText(string text)
        {
            var builder = new StringBuilder(text.Length);
            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch) || char.IsControl(ch) || char.IsPunctuation(ch))
                {
                    continue;
                }

                if (char.GetUnicodeCategory(ch) == UnicodeCategory.DecimalDigitNumber)
                {
                    var numeric = (int)char.GetNumericValue(ch);
                    if (numeric >= 0)
                    {
                        builder.Append(numeric);
                        continue;
                    }
                }

                builder.Append(ch);
            }

            return builder.ToString();
        }

        private void ScheduleVolumeRestore()
        {
            if (!_audioDuckingConfig.Enabled)
            {
                return;
            }

            if (!_isDuckingAudio)
            {
                return;
            }

            _volumeRestoreTimer.Stop();

            if (_audioDuckingConfig.RestoreDelaySeconds <= 0)
            {
                RestorePlayerVolume();
                return;
            }

            _volumeRestoreTimer.Interval = _audioDuckingConfig.RestoreDelay;
            _volumeRestoreTimer.Start();
        }


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

        private void SetPlayerVolume(int volume, bool updateSlider = true, bool updateUserPreferred = true)
        {
            volume = Math.Clamp(volume, 0, 100);

            if (_player.Volume != volume)
            {
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
                await using var fileStream = File.Create(tempPath);
                await remoteStream.CopyToAsync(fileStream).ConfigureAwait(false);
                await fileStream.FlushAsync().ConfigureAwait(false);

                var writtenLength = fileStream.Length;
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
            _videoCallWindow.Closed += (_, _) => _videoCallWindow = null;
            _videoCallWindow.Show();
        }
    }
}