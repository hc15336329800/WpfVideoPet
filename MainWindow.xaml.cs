using LibVLCSharp.Shared;
using Microsoft.Win32;
using System;
using System.IO;
using System.Timers;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;



namespace WpfVideoPet
{
    public partial class MainWindow : Window
    {
        private LibVLC _libVlc;                 // [视频核心] LibVLC 实例
        private LibVLCSharp.Shared.MediaPlayer _player;            // [视频核心] 播放器
        private readonly DispatcherTimer _clockTimer;     // 左上角时钟刷新
        private readonly DispatcherTimer _petTimer;       // 3D 旋转动画
        private double _angle = 0;              // 立方体角度

     

        public MainWindow()
        {
            InitializeComponent();
            Core.Initialize();                         // [必需] 初始化 LibVLC
            _libVlc = new LibVLC();
            _player = new LibVLCSharp.Shared.MediaPlayer(_libVlc);
            VideoView.MediaPlayer = _player;           // 绑定到 XAML 的 VideoView

            // 默认音量
            _player.Volume = 60;
            SldVolume.Value = _player.Volume;

            // 左上角：时钟+天气（天气此处演示为假数据，替换为真实 API 即可）
            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (_, __) => TxtTime.Text = $"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            _clockTimer.Start();
            LoadWeatherMock(); // 演示：假数据；接入真实 API 时改这里

            // 右下角：简易旋转动画
            _petTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _petTimer.Tick += (_, __) => { _angle = (_angle + 1) % 360; CubeRotation.Angle = _angle; };
            _petTimer.Start();


        }

        // === 播放控制 ===
        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.ts;*.m3u8|所有文件|*.*"
            };
            if (dlg.ShowDialog() == true) PlayFile(dlg.FileName);
        }

        public void PlayFile(string path)
        {
            if (!File.Exists(path)) return;
            using var media = new Media(_libVlc, new Uri(path));
            _player.Play(media);
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e) => _player.SetPause(false);
        private void BtnPause_Click(object sender, RoutedEventArgs e) => _player.Pause();
        private void BtnStop_Click(object sender, RoutedEventArgs e) => _player.Stop();
        private void SldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => _player.Volume = (int)e.NewValue;

        // 快捷键：Esc 退出全屏窗口（当前窗口无边框最大化）
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
            if (e.Key == Key.Space) { if (_player.IsPlaying) _player.Pause(); else _player.SetPause(false); }
            if (e.Key == Key.M) _player.Mute = !_player.Mute;
        }

        // === 天气（演示数据） ===
        private void LoadWeatherMock()
        {
            TxtCity.Text = "城市: 示例市";
            TxtWeather.Text = "天气: 多云转晴，西北风3级";
            TxtTemp.Text = "气温: 22℃";
        }

        // 释放
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _player?.Stop();
            _player?.Dispose();
            _libVlc?.Dispose();
   
        }
    }
}
