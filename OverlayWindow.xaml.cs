using HelixToolkit.SharpDX.Core.Animations; // 动画更新器与循环模式
using HelixToolkit.SharpDX.Core.Assimp; // 导入器类型
using HelixToolkit.SharpDX.Core.Model.Scene; // 场景节点类型
using HelixToolkit.Wpf.SharpDX; // DX11 控件
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WpfVideoPet
{
    /// <summary> 
    /// 负责在主界面上叠加提示信息、天气信息和语音识别状态的窗体。 
    /// </summary> 
    public partial class OverlayWindow : Window
    {
        private const int GwlExstyle = -20;       // Win32 窗口扩展样式索引
        private const int WsExTransparent = 0x20; // 允许鼠标穿透样式值
        private const int WsExNoActivate = 0x8000000; // 禁止窗口激活样式值

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex); // 获取窗口样式

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong); // 设置窗口样式

        private HelixToolkitScene? _scene; // 当前 3D 场景数据
        private NodeAnimationUpdater? _animationUpdater; // 骨骼动画更新器
        private long _animationStartTicks; // 动画起始时间戳
        private bool _isRenderingHooked; // 渲染循环是否已挂接

        /// <summary>
        /// 初始化覆盖层窗口，订阅生命周期事件并准备 Helix 渲染环境。
        /// </summary>
        public OverlayWindow()
        {
            InitializeComponent();
            Loaded += OverlayWindow_Loaded; // 【新增】窗口加载时载入模型
            Unloaded += OverlayWindow_Unloaded; // 【新增】窗口卸载时释放资源
            PetViewDx.EffectsManager ??= new DefaultEffectsManager();
        }

        /// <summary>
        /// 在窗口句柄创建完成后追加透明与非激活样式，确保覆盖层不抢占焦点。
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle; // 当前窗口句柄
            var styles = GetWindowLong(handle, GwlExstyle); // 原始扩展样式
            SetWindowLong(handle, GwlExstyle, styles | WsExTransparent | WsExNoActivate); // 应用透明和非激活样式
            AppLogger.Info("OverlayWindow 已设置透明与非激活窗口样式。");
        }

        /// <summary>
        /// 加载 GLB 模型、构建场景节点，并尝试自动播放默认动画。
        /// <para>流程包括：校验模型路径、执行 Assimp 导入、绑定到 Viewport3DX、注册渲染事件用于逐帧更新。</para>
        /// </summary>
        private void OverlayWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                DetachRenderingLoop();
                _animationUpdater = null;
                _scene = null;
                _animationStartTicks = 0;

                // 【修改点】把 .glb 与可能的“贴图文件”文件夹放到同一目录，保持相对路径
                string modelPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "Assets", "Models", "8f882a96eb3b49a29cba9b87c71209cc.glb"); // 3D 模型绝对路径

                if (!System.IO.File.Exists(modelPath))
                {
                    AppLogger.Warn($"模型文件不存在：{modelPath}");
                    return;
                }

                AppLogger.Info($"开始加载 3D 模型：{modelPath}");

                // 导入器：启用骨骼蒙皮与动画
                var importer = new Importer // glTF/GLB 导入器实例
                {
                    Configuration =
                    {
                        CreateSkeletonForBoneSkinningMesh = true,
                        ImportAnimations = true
                    }
                };

                // 加载 glb => HelixToolkitScene
                _scene = importer.Load(modelPath);
                if (_scene?.Root == null)
                {
                    AppLogger.Warn($"模型加载失败：{modelPath}");
                    return;
                }

                // 清空并加入到场景
                SceneRoot.Clear(true);
                SceneRoot.AddNode(_scene.Root);
                PetViewDx.ZoomExtents();
                AppLogger.Info("模型场景已绑定到 Viewport3DX。");

                // 若包含动画则播放
                var targetAnimation = _scene.Animations?.FirstOrDefault(a => a.Name == "Idle") // 目标动画片段
                    ?? _scene.Animations?.FirstOrDefault();

                if (targetAnimation != null)
                {
                    _animationUpdater = new NodeAnimationUpdater(targetAnimation)
                    {
                        RepeatMode = AnimationRepeatMode.Loop
                    };
                    _animationStartTicks = 0;
                    AttachRenderingLoop();
                    AppLogger.Info($"播放动画片段：{targetAnimation.Name}");
                }
                else
                {
                    AppLogger.Info("模型未包含可用动画片段，保持静态展示。");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"加载 3D 模型失败：{ex}");
            }
        }

        /// <summary>
        /// 在窗口卸载时解除渲染事件并回收动画资源，防止后台持续更新。
        /// </summary>
        private void OverlayWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            DetachRenderingLoop();
            _animationUpdater = null;
            _scene = null;
            AppLogger.Info("OverlayWindow 卸载，已清理 3D 模型资源。");
        }

        /// <summary>
        /// 每帧渲染时更新骨骼动画的时间轴，确保动画按照真实时间推进。
        /// </summary>
        private void CompositionTarget_Rendering(object? sender, EventArgs e)
        {
            if (_animationUpdater == null)
            {
                return;
            }

            if (_animationStartTicks == 0)
            {
                _animationStartTicks = Stopwatch.GetTimestamp();
                return;
            }

            var elapsed = Stopwatch.GetTimestamp() - _animationStartTicks; // 动画耗时
            _animationUpdater.Update(elapsed, Stopwatch.Frequency);
        }

        /// <summary>
        /// 将 CompositionTarget.Rendering 挂接到动画更新逻辑，避免重复注册。
        /// </summary>
        private void AttachRenderingLoop()
        {
            if (_isRenderingHooked)
            {
                return;
            }

            CompositionTarget.Rendering += CompositionTarget_Rendering;
            _isRenderingHooked = true;
            AppLogger.Info("已挂接渲染循环，开始驱动动画更新。");
        }

        /// <summary>
        /// 解除渲染事件订阅，避免窗口关闭后仍然执行动画更新。
        /// </summary>
        private void DetachRenderingLoop()
        {
            if (!_isRenderingHooked)
            {
                return;
            }

            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            _isRenderingHooked = false;
            AppLogger.Info("已解除渲染循环，停止动画更新。");
        }

        /// <summary>
        /// 更新天气面板显示内容，并写入日志便于追踪数据来源。
        /// </summary>
        public void UpdateWeather(string city, string weather, string temperature)
        {
            TxtCity.Text = city;
            TxtWeather.Text = weather;
            TxtTemp.Text = temperature;
            AppLogger.Info($"更新天气信息：{city} {weather} {temperature}");
        }

        /// <summary>
        /// 刷新界面上的当前时间显示，同步记录调试日志。
        /// </summary>
        public void UpdateTime(string timeText)
        {
            TxtTime.Text = timeText;
            AppLogger.Info($"更新时间显示：{timeText}");
        }

        /// <summary>
        /// 旋转占位：DX11 版本不再使用 XAML 中的 CubeRotation。
        /// 如需旋转模型，可给 node/SceneRoot 附加 Transform。
        /// 预留的 3D 模型旋转接口，方便后续扩展动画或交互。
        /// </summary>
        public void UpdatePetRotation(double angle)
        {
            // 留空或在此自定义旋转逻辑
            AppLogger.Debug($"调用旋转占位方法，角度：{angle}");
        }

        /// <summary> 
        /// 展示虚拟人通知的占位方法，当前仅输出日志以提示调用来源。 
        /// </summary> 
        public void ShowNotification(string message, TimeSpan? duration = null, string? title = null)
        {
            var safeMessage = string.IsNullOrWhiteSpace(message) ? "<空>" : message.Trim();
            var safeTitle = string.IsNullOrWhiteSpace(title) ? "<默认标题>" : title.Trim();
            AppLogger.Info($"收到通知展示请求，但气泡区域已移除。标题：{safeTitle}，内容：{safeMessage}，原计划时长：{(duration ?? TimeSpan.Zero).TotalMilliseconds} ms。");
        }

        /// <summary> 
        /// 隐藏通知的占位方法，当前仅记录日志确保流程可追踪。 
        /// </summary> 
        public void HideNotification()
        {
            AppLogger.Info("收到通知隐藏请求，但气泡区域已移除，无需额外处理。");
        }

        /// <summary> 
        /// 展示语音识别内容的占位方法，当前仅记录日志。 
        /// </summary> 
        public void ShowTranscription(string title, string content)
        {
            var safeTitle = string.IsNullOrWhiteSpace(title) ? "<默认标题>" : title.Trim();
            var safeContent = string.IsNullOrWhiteSpace(content) ? "<空>" : content.Trim();
            AppLogger.Info($"收到语音识别展示请求，但气泡区域已移除。标题：{safeTitle}，内容：{safeContent}。");
        }

        /// <summary> 
        /// 隐藏语音识别内容的占位方法，当前仅记录日志。 
        /// </summary> 
        public void HideTranscription()
        {
            AppLogger.Info("收到语音识别隐藏请求，但气泡区域已移除，无需额外处理。");
        }

        // ================= Win32 API ================= 
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        // ============================================= 
    }
}