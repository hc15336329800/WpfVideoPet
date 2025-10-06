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
        //  5 秒倒计时 压低声音
        private static readonly TimeSpan VolumeRestoreDelay = TimeSpan.FromSeconds(5);
        private readonly DispatcherTimer _volumeRestoreTimer;
        private bool _suppressSliderCallback;
        private bool _isDuckingAudio;
        private int _userPreferredVolume;
        private readonly AppConfig _appConfig;
        private MqttTaskService? _mqttService;
        private readonly HttpClient _httpClient = new();
        private readonly string _mediaCacheDirectory;
        private readonly ConcurrentDictionary<string, Task> _mediaDownloadTasks = new(StringComparer.OrdinalIgnoreCase);

        public MainWindow()
        {
            InitializeComponent();
            _appConfig = AppConfig.Load(null);
            _mediaCacheDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MediaCache");
            Directory.CreateDirectory(_mediaCacheDirectory);

            Core.Initialize();

            _libVlc = new LibVLC();
            _player = new LibVLCSharp.Shared.MediaPlayer(_libVlc);

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
                Interval = VolumeRestoreDelay
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
                return;
            }

            using var media = new Media(_libVlc, new Uri(path));
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
                return;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return;
            }

            using var media = new Media(_libVlc, uri);
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
            if (e.Result.Confidence < 0.55)
            {
                Dispatcher.BeginInvoke(new Action(ScheduleVolumeRestore));
                return;
            }

            if (string.Equals(e.Result.Text, "蓝猫一号", StringComparison.Ordinal))
            {
                Dispatcher.Invoke(ShowVideoCallWindow);
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

        private void BeginAudioDucking()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
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

        // 设置压低声音时间？
        private void ScheduleVolumeRestore()
        {
            if (!_isDuckingAudio)
            {
                return;
            }

            _volumeRestoreTimer.Stop();
            _volumeRestoreTimer.Interval = VolumeRestoreDelay;
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
                return;
            }

            if (string.IsNullOrWhiteSpace(_appConfig.Mqtt.ServerUri))
            {
                Debug.WriteLine("MQTT ServerUri 未配置，跳过初始化。");
                return;
            }

            _mqttService = new MqttTaskService(_appConfig.Mqtt);
            _mqttService.RemoteMediaTaskReceived += OnRemoteMediaTaskReceived;

            _ = _mqttService.StartAsync().ContinueWith(task =>
            {
                if (task.Exception != null)
                {
                    Debug.WriteLine($"MQTT 连接失败: {task.Exception.GetBaseException().Message}");
                }
            }, TaskScheduler.Default);
        }

        /// <summary>
        /// MQTT 收到远程媒体任务后的第一时间响应。
        /// </summary>
        private void OnRemoteMediaTaskReceived(object? sender, RemoteMediaTask task)
        {
            _ = Task.Run(() => HandleRemoteMediaTaskAsync(task));
        }

        /// <summary>
        /// 处理单个远程媒体任务：优先播放本地缓存，再回落到在线播放并触发缓存。
        /// </summary>
        private async Task HandleRemoteMediaTaskAsync(RemoteMediaTask task)
        {
            try
            {
                var media = task.Media;

                var cached = await TryGetCachedMediaAsync(media).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(cached))
                {
                    await Dispatcher.InvokeAsync(() => PlayFile(cached));
                    return;
                }

                var playbackUrl = !string.IsNullOrWhiteSpace(media.AccessibleUrl)
                    ? media.AccessibleUrl
                    : media.DownloadUrl;

                if (!string.IsNullOrWhiteSpace(playbackUrl))
                {
                    await Dispatcher.InvokeAsync(() => PlayRemoteMedia(playbackUrl!));
                }

                await CacheMediaAsync(task).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理远程媒体任务失败: {ex}");
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
                return null;
            }

            if (media.FileSize.HasValue)
            {
                var length = new FileInfo(cachePath).Length;
                if (length != media.FileSize.Value)
                {
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
        private async Task CacheMediaAsync(RemoteMediaTask task)
        {
            var media = task.Media;
            var downloadUrl = !string.IsNullOrWhiteSpace(media.DownloadUrl)
                ? media.DownloadUrl
                : media.AccessibleUrl;

            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                return;
            }

            Directory.CreateDirectory(_mediaCacheDirectory);

            var existing = await TryGetCachedMediaAsync(media).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(existing))
            {
                return;
            }

            var targetPath = BuildCachePath(media);
            var cacheKey = targetPath;

            if (_mediaDownloadTasks.TryGetValue(cacheKey, out var existingTask))
            {
                await existingTask.ConfigureAwait(false);
                return;
            }

            var downloadTask = DownloadAndStoreAsync(downloadUrl!, targetPath, media);
            if (!_mediaDownloadTasks.TryAdd(cacheKey, downloadTask))
            {
                downloadTask = _mediaDownloadTasks[cacheKey];
            }

            try
            {
                await downloadTask.ConfigureAwait(false);
            }
            finally
            {
                _mediaDownloadTasks.TryRemove(cacheKey, out _);
            }
        }

        /// <summary>
        /// 拉取远程媒体并在校验后保存到目标路径。
        /// </summary>
        private async Task DownloadAndStoreAsync(string url, string targetPath, RemoteMediaInfo media)
        {
            var tempPath = targetPath + ".downloading";

            try
            {
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var remoteStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var fileStream = File.Create(tempPath);
                await remoteStream.CopyToAsync(fileStream).ConfigureAwait(false);

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
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"缓存媒体文件失败: {ex.Message}");
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
        /// 校验文件的 SHA-256 哈希是否与预期一致。
        /// </summary>
        private static async Task<bool> VerifyFileHashAsync(string filePath, string expectedHash)
        {
            try
            {
                await using var stream = File.OpenRead(filePath);
                using var sha256 = SHA256.Create();
                var hash = await sha256.ComputeHashAsync(stream).ConfigureAwait(false);
                var actual = Convert.ToHexString(hash);
                return string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
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
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"释放 MQTT 服务时发生异常: {ex.Message}");
                }
            }

            _httpClient.Dispose();
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