using LibVLCSharp.Shared;
using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;
using System.Speech.Recognition;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace WpfVideoPet
{
    public partial class MainWindow : Window
    {
        private LibVLC _libVlc;
        private LibVLCSharp.Shared.MediaPlayer _player;
        private readonly DispatcherTimer _clockTimer;
        private readonly DispatcherTimer _petTimer;
        private double _angle;
        private OverlayWindow? _overlay;
        private SpeechRecognitionEngine? _speechRecognizer;

        public MainWindow()
        {
            InitializeComponent();
            Core.Initialize();
            _libVlc = new LibVLC();
            _player = new LibVLCSharp.Shared.MediaPlayer(_libVlc);
            VideoView.MediaPlayer = _player;

            _player.Volume = 60;
            SldVolume.Value = _player.Volume;

            LocationChanged += (_, __) => UpdateOverlayBounds();
            SizeChanged += (_, __) => UpdateOverlayBounds();
            StateChanged += (_, __) => UpdateOverlayBounds();
            Loaded += OnMainWindowLoaded;

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

            InitializeSpeechRecognition();
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

        private void BtnPlay_Click(object sender, RoutedEventArgs e) => _player.SetPause(false);

        private void BtnPause_Click(object sender, RoutedEventArgs e) => _player.Pause();

        private void BtnStop_Click(object sender, RoutedEventArgs e) => _player.Stop();

        private void SldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => _player.Volume = (int)e.NewValue;

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }

            if (e.Key == Key.F1)
            {
                // F1 快捷键：立即显示 “helloworld”
                MessageBox.Show("helloworld", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
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
            if (e.Result.Confidence < 0.7)
            {
                return;
            }

            if (string.Equals(e.Result.Text, "蓝猫一号", StringComparison.Ordinal))
            {
                Dispatcher.Invoke(() =>
                    MessageBox.Show("helloworld", "提示", MessageBoxButton.OK, MessageBoxImage.Information));
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
            _player?.Stop();
            _player?.Dispose();
            _libVlc?.Dispose();

            if (_speechRecognizer != null)
            {
                _speechRecognizer.SpeechRecognized -= SpeechRecognizerOnSpeechRecognized;
                _speechRecognizer.RecognizeAsyncCancel();
                _speechRecognizer.Dispose();
            }
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
    }
}