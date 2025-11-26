using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WpfVideoPet
{
    /// <summary>
    /// 提供显示分辨率与 DPI 的安全读取，并在屏幕参数变化时通过防抖事件通知订阅者。
    /// </summary>
    internal sealed class DisplayEnvironmentService : IDisposable
    {
        private readonly DispatcherTimer _debounceTimer; // 分辨率变化防抖定时器
        private DisplaySnapshot _lastSnapshot; // 最近一次捕获的显示快照
        private bool _pendingRefresh; // 是否有待处理的显示变化
        private bool _disposed; // 释放标记

        /// <summary>
        /// 发生显示参数变化时触发，携带最新的显示快照。
        /// </summary>
        public event EventHandler<DisplaySnapshot>? DisplayChanged;

        /// <summary>
        /// 初始化显示环境服务，开始监听系统显示参数变化并提供防抖通知。
        /// </summary>
        public DisplayEnvironmentService()
        {
            _lastSnapshot = CaptureSafeSnapshot();

            _debounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _debounceTimer.Tick += OnDebounceTick;

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        }

        /// <summary>
        /// 捕获当前显示器的宽高与 DPI，若检测到无效分辨率则回退到 1280x720 并标记回退状态。
        /// </summary>
        /// <returns>包含宽、高、DPI 与是否使用回退值的显示快照。</returns>
        public static DisplaySnapshot CaptureSafeSnapshot()
        {
            double width = SystemParameters.PrimaryScreenWidth; // 当前屏幕宽度
            double height = SystemParameters.PrimaryScreenHeight; // 当前屏幕高度
            var isFallback = false; // 是否使用了回退分辨率

            double dpi = 96; // 当前 DPI
            try
            {
                using var graphics = Graphics.FromHwnd(IntPtr.Zero);
                dpi = graphics.DpiX;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"读取系统 DPI 失败，使用默认 96 DPI: {ex.Message}");
            }

            if (width <= 0 || height <= 0)
            {
                width = 1280;
                height = 720;
                isFallback = true;
            }

            if (dpi <= 0)
            {
                dpi = 96;
            }

            return new DisplaySnapshot(width, height, dpi, isFallback);
        }

        /// <summary>
        /// 释放事件订阅与计时器，防止资源泄漏。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _debounceTimer.Stop();
            _debounceTimer.Tick -= OnDebounceTick;

            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        }

        /// <summary>
        /// 当系统显示设置变化时开启防抖，延迟刷新显示快照。
        /// </summary>
        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            ScheduleSnapshotRefresh();
        }

        /// <summary>
        /// 当显示相关静态属性变化时开启防抖，延迟刷新显示快照。
        /// </summary>
        private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
        {
            ScheduleSnapshotRefresh();
        }

        /// <summary>
        /// 标记需要刷新显示信息并启动防抖计时器。
        /// </summary>
        private void ScheduleSnapshotRefresh()
        {
            _pendingRefresh = true;
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        /// <summary>
        /// 防抖计时结束后刷新显示快照，并在变化时向外发布事件。
        /// </summary>
        private void OnDebounceTick(object? sender, EventArgs e)
        {
            if (!_pendingRefresh)
            {
                return;
            }

            _pendingRefresh = false;
            _debounceTimer.Stop();

            var snapshot = CaptureSafeSnapshot();
            if (snapshot == _lastSnapshot)
            {
                return;
            }

            _lastSnapshot = snapshot;
            DisplayChanged?.Invoke(this, snapshot);
        }
    }

    /// <summary>
    /// 表示一次显示环境的快照，包括宽、高、DPI 以及是否为回退分辨率。
    /// </summary>
    public sealed record DisplaySnapshot(double Width, double Height, double Dpi, bool IsFallback);
}