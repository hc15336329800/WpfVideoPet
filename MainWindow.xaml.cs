using LibVLCSharp.Shared;
using Microsoft.Win32;
using System;
using System.IO;
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
        }

        private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnMainWindowLoaded;

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

                UpdateOverlayBounds();
                LoadWeatherMock();
                _overlay.UpdateTime($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }), DispatcherPriority.ApplicationIdle);
        }

        private void UpdateOverlayBounds()
        {
            if (_overlay == null || !_overlay.IsLoaded)
            {
                return;
            }

            if (WindowState == WindowState.Minimized)
            {
                if (_overlay.IsVisible)
                {
                    _overlay.Hide();
                }

                return;
            }

            if (!_overlay.IsVisible)
            {
                _overlay.Show();
            }

            if (WindowState == WindowState.Maximized)
            {
                _overlay.WindowState = WindowState.Maximized;
            }
            else
            {
                if (_overlay.WindowState != WindowState.Normal)
                {
                    _overlay.WindowState = WindowState.Normal;
                }

                _overlay.Left = Left;
                _overlay.Top = Top;
                _overlay.Width = ActualWidth;
                _overlay.Height = ActualHeight;
            }
        }

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.ts;*.m3u8|所有文件|*.*"
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
            _player?.Stop();
            _player?.Dispose();
            _libVlc?.Dispose();
        }
    }
}