using HelixToolkit.SharpDX; // Core 效果管理器
using HelixToolkit.SharpDX.Core; // ViewportCore 类型
using HelixToolkit.SharpDX.Core.Animations; // 动画更新器
using HelixToolkit.SharpDX.Core.Assimp; // 模型导入器
using HelixToolkit.SharpDX.Core.Cameras; // 相机核心类型
using HelixToolkit.SharpDX.Core.Model; // 材质定义
using HelixToolkit.SharpDX.Core.Model.Scene; // 场景节点
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using D3D = SharpDX.Direct3D11;
using SDX = SharpDX;
using ImageSourceDX = HelixToolkit.Wpf.SharpDX.DX11ImageSource; // DX11 图像源
using RenderHostDX = HelixToolkit.Wpf.SharpDX.Controls.DX11ImageSourceRenderHost; // DX11 渲染宿主
using ImageSourceArgs = HelixToolkit.Wpf.SharpDX.Controls.DX11ImageSourceArgs; // DX11 图像事件参数

namespace WpfVideoPet
{
    /// <summary>
    /// 负责在主界面上叠加提示信息、天气信息和语音识别状态的窗体，同时承载 Core 版本的 3D 渲染输出。
    /// </summary>
    public partial class OverlayWindow : Window
    {
        private const int GwlExstyle = -20; // Win32 窗口扩展样式索引
        private const int WsExTransparent = 0x20; // 允许鼠标穿透样式值
        private const int WsExNoActivate = 0x8000000; // 禁止窗口激活样式值
        private const string ModelFileName = "117.glb"; // 默认模型文件名avatar2.fbx
//private const string ModelFileName = "117.fbx";
        private const int RenderHeartbeat = 300; // 渲染心跳间隔帧

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex); // 获取窗口样式

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong); // 设置窗口样式

        private IEffectsManager? _effectsManager; // Core 效果管理器
        private PerspectiveCameraCore? _cameraCore; // 核心透视相机
        private GroupNode? _sceneRoot; // 场景根节点
        private HelixToolkitScene? _loadedScene; // 导入后的场景
        private IAnimationUpdater? _animationUpdater; // 当前动画更新器
        private float _animationOffsetSeconds; // 动画播放起点
        private float _animationDurationSeconds; // 动画循环时长
        private bool _isAnimationPlaying; // 动画是否播放
        private long _animationStartTicks; // 动画起始时间刻度

        private RenderHostDX? _renderHost; // DX11 渲染宿主
        private ViewportCore? _viewportCore; // ViewportCore 管理器
        private ImageSourceDX? _imageSource; // 输出图像源
        private bool _isRendering; // 渲染循环标记
        private bool _hasLoggedImageSource; // 是否记录过图像源绑定
        private int _frameCounter; // 渲染帧计数

        /// <summary>
        /// 初始化覆盖层窗口，订阅生命周期事件并准备 Core 渲染环境。
        /// </summary>
        public OverlayWindow()
        {
            InitializeComponent();
            Loaded += OverlayWindow_Loaded; // 窗口加载时初始化渲染
            Unloaded += OverlayWindow_Unloaded; // 窗口卸载时清理资源
        }

        /// <summary>
        /// 在窗口句柄创建完成后追加透明与非激活样式，确保覆盖层不抢占焦点。
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle; // 当前窗口句柄
            var styles = GetWindowLong(handle, GwlExstyle); // 原始扩展样式
            SetWindowLong(handle, GwlExstyle, styles | WsExTransparent | WsExNoActivate); // 应用透明和非激活样式
            AppLogger.Info("OverlayWindow 已设置透明与非激活窗口样式。");
        }

        /// <summary>
        /// 在窗口加载完成后初始化 Core 渲染管线、导入模型并启动渲染循环。
        /// </summary>
        private void OverlayWindow_Loaded(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("OverlayWindow_Loaded 开始初始化 3D 渲染逻辑。");
            try
            {
                InitializeCoreRenderer();
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "OverlayWindow 初始化 3D 渲染失败。");
            }
        }

        /// <summary>
        /// 在窗口卸载时停止渲染循环并释放所有 DX11 相关资源，防止内存泄漏。
        /// </summary>
        private void OverlayWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("OverlayWindow_Unloaded 开始清理 3D 渲染资源。");
            DisposeCoreRenderer();
        }

        /// <summary>
        /// 初始化 Core 渲染器、导入模型并准备渲染循环。
        /// </summary>
        private void InitializeCoreRenderer()
        {
            DisposeCoreRenderer();

            _effectsManager = new DefaultEffectsManager();
            _cameraCore = new PerspectiveCameraCore
            {
                Position = new SDX.Vector3(1.2f, 0.8f, 3f), // 相机初始位置
                LookDirection = new SDX.Vector3(-1.2f, -0.8f, -3f), // 相机观察方向
                UpDirection = new SDX.Vector3(0f, 1f, 0f), // 上方向
                FieldOfView = 45f // 视角
            };

            _sceneRoot = new GroupNode();

            var modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Models", ModelFileName); // 模型绝对路径
            _loadedScene = LoadHelixScene(modelPath);
            if (_loadedScene?.Root != null)
            {
                _sceneRoot.AddChildNode(_loadedScene.Root);
                AppLogger.Info($"模型加载成功：{modelPath}");

                InitializeAnimationPlayer();
                _loadedScene?.Root?.UpdateAllTransformMatrix(); //  确保 BoundsWithTransform 是最新的

                //  根据场景包围盒自动对齐相机的垂直中心，避免“看不到头”
                if (_cameraCore != null && TryGetSceneBounds(_loadedScene.Root, out var bb))
                {
                    var centerY = (bb.Minimum.Y + bb.Maximum.Y) * 0.5f;
                    var pos = _cameraCore.Position;
                    var look = _cameraCore.LookDirection;
                    var target = pos + look;
                    var deltaY = centerY - target.Y;
                    _cameraCore.LookDirection = new SDX.Vector3(look.X, look.Y + deltaY, look.Z); // 仅抬/降目标点
                    AppLogger.Info($"Camera vertical aligned to centerY={centerY:F3} (ΔY={deltaY:F3}).");
                }
            }
            else
            {
                AppLogger.Warn("模型加载失败，使用占位立方体。");
                _sceneRoot.AddChildNode(CreateFallbackBox());
            }

            _sceneRoot.AddChildNode(CreateAmbientLight());
            _sceneRoot.AddChildNode(CreateDirectionalLight());

            var transparentColor = new SDX.Color4(0f, 0f, 0f, 0f); // 透明色

            _renderHost = new RenderHostDX
            {
                ClearColor = transparentColor
            };
            _renderHost.OnImageSourceChanged += OnRenderHostImageSourceChanged;
            _renderHost.EffectsManager = _effectsManager;

            _viewportCore = new ViewportCore(_renderHost)
            {
                EffectsManager = _effectsManager,
                CameraCore = _cameraCore,
                BackgroundColor = transparentColor,
                ShowCoordinateSystem = false,
                ShowFPS = false,
                ShowRenderDetail = false,
                ShowViewCube = false
            };
            _viewportCore.Items.AddChildNode(_sceneRoot);
            _viewportCore.Attach(_renderHost);

            AppLogger.Info("已为 3D 视口应用透明背景并关闭坐标与调试标识。");

            try
            {
                _renderHost.InvalidateSceneGraph();
                AppLogger.Info("调用 InvalidateSceneGraph() 请求初始构建。");
            }
            catch (MissingMethodException)
            {
                _renderHost.InvalidateRender();
                AppLogger.Info("Fallback 调用 InvalidateRender()。");
            }

            PetImage.SizeChanged += OnPetImageSizeChanged;

            var width = Math.Max(1, (int)Math.Ceiling(PetImage.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(PetImage.ActualHeight));
            _renderHost.StartD3D(width, height);
            AppLogger.Info($"StartD3D: {width}x{height}");
            _renderHost.UpdateAndRender();

            CompositionTarget.Rendering += OnCompositionRendering;
            _isRendering = true;
            _frameCounter = 0;
            _hasLoggedImageSource = false;
            _isAnimationPlaying = _animationUpdater != null;
            _animationStartTicks = Stopwatch.GetTimestamp();
            AppLogger.Info("渲染循环已启动。");
        }

        /// <summary>
        /// 在窗口卸载或重新初始化前释放渲染循环与 DX11 资源。
        /// </summary>
        private void DisposeCoreRenderer()
        {
            if (_isRendering)
            {
                CompositionTarget.Rendering -= OnCompositionRendering;
                _isRendering = false;
                AppLogger.Info("停止 CompositionTarget 渲染循环。");
            }

            PetImage.SizeChanged -= OnPetImageSizeChanged;

            _viewportCore?.Detach();
            _viewportCore?.Dispose();
            _viewportCore = null;

            if (_renderHost != null)
            {
                _renderHost.OnImageSourceChanged -= OnRenderHostImageSourceChanged;
                _renderHost.Dispose();
                _renderHost = null;
            }

            if (_imageSource != null)
            {
                _imageSource.Dispose();
                _imageSource = null;
            }

            PetImage.Source = null;

            _effectsManager = null;
            _cameraCore = null;
            _sceneRoot = null;
            _loadedScene = null;
            _animationUpdater = null;
            _isAnimationPlaying = false;
            _animationStartTicks = 0;
            _animationOffsetSeconds = 0f;
            _animationDurationSeconds = 0f;
            AppLogger.Info("3D 渲染资源清理完成。");
        }

        /// <summary>
        /// 每帧渲染回调：驱动动画更新并请求 DX11 渲染输出。
        /// </summary>
        private void OnCompositionRendering(object? sender, EventArgs e)
        {
            if (_renderHost == null)
            {
                return;
            }

            UpdateAnimationFrame();
            _renderHost.UpdateAndRender();

            _frameCounter++;
            if (_frameCounter % RenderHeartbeat == 0)
            {
                AppLogger.Info($"渲染心跳：{RenderHeartbeat} 帧");
            }
        }

        /// <summary>
        /// 处理 DX11 渲染宿主输出的图像源并绑定到界面上的 Image 控件。
        /// </summary>
        private void OnRenderHostImageSourceChanged(object? sender, ImageSourceArgs e)
        {
            _imageSource = e.Source;
            PetImage.Source = _imageSource;
            if (!_hasLoggedImageSource)
            {
                _hasLoggedImageSource = true;
                AppLogger.Info("DX11ImageSource 已绑定到界面。");
            }
        }

        /// <summary>
        /// 当 Image 控件尺寸变化时同步调整渲染宿主后台缓冲大小。
        /// </summary>
        private void OnPetImageSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_renderHost == null)
            {
                return;
            }

            var width = Math.Max(1, (int)Math.Ceiling(e.NewSize.Width));
            var height = Math.Max(1, (int)Math.Ceiling(e.NewSize.Height));
            _renderHost.Resize(width, height);
            AppLogger.Info($"PetImage 尺寸变化：{width}x{height}");
        }

        /// <summary>
        /// 初始化动画播放器，优先选择 Idle 动画并记录播放区间。
        /// </summary>
        private void InitializeAnimationPlayer()
        {
            if (_loadedScene?.Animations == null || !_loadedScene.Animations.Any())
            {
                _animationUpdater = null;
                _isAnimationPlaying = false;
                AppLogger.Info("场景中未找到可用动画。");
                return;
            }

            var updaters = _loadedScene.Animations.CreateAnimationUpdaters();
            foreach (var clip in updaters.Keys)
            {
                AppLogger.Info($"检测到动画片段：{clip}");
            }

            _animationUpdater = updaters.TryGetValue("Idle", out var idle) ? idle : updaters.Values.FirstOrDefault();
            if (_animationUpdater == null)
            {
                _isAnimationPlaying = false;
                AppLogger.Warn("未能创建动画更新器，模型将静态展示。");
                return;
            }

            _animationUpdater.RepeatMode = AnimationRepeatMode.Loop;
            _animationUpdater.Reset();
            _animationStartTicks = Stopwatch.GetTimestamp();
            _isAnimationPlaying = true;

            _animationOffsetSeconds = _animationUpdater.StartTime;
            _animationDurationSeconds = Math.Max(0f, _animationUpdater.EndTime - _animationUpdater.StartTime);

            if (_animationUpdater is NodeAnimationUpdater nodeUpdater)
            {
                var minTime = float.PositiveInfinity; // 最早关键帧
                var maxTime = float.NegativeInfinity; // 最晚关键帧
                var keyframeCount = 0; // 关键帧数量

                foreach (var node in nodeUpdater.NodeCollection)
                {
                    foreach (var frame in node.KeyFrames)
                    {
                        if (frame.Time < minTime)
                        {
                            minTime = frame.Time;
                        }

                        if (frame.Time > maxTime)
                        {
                            maxTime = frame.Time;
                        }

                        keyframeCount++;
                    }
                }

                if (float.IsNaN(minTime) || float.IsInfinity(minTime))
                {
                    minTime = nodeUpdater.StartTime;
                }

                if (float.IsNaN(maxTime) || float.IsInfinity(maxTime))
                {
                    maxTime = nodeUpdater.EndTime;
                }

                _animationOffsetSeconds = Math.Max(0f, minTime);
                _animationDurationSeconds = Math.Max(0f, maxTime - minTime);
                if (_animationDurationSeconds <= 1e-4f)
                {
                    _animationDurationSeconds = Math.Max(0f, nodeUpdater.EndTime - nodeUpdater.StartTime);
                }

                var playbackMax = _animationOffsetSeconds + _animationDurationSeconds;
                AppLogger.Info($"动画关键帧统计：Count={keyframeCount} Min={_animationOffsetSeconds:F3}s Max={playbackMax:F3}s");
            }

            AppLogger.Info($"动画初始化成功：Name={_animationUpdater.Name} Offset={_animationOffsetSeconds:F3}s Duration={_animationDurationSeconds:F3}s");
        }

        /// <summary>
        /// 按秒为单位驱动动画更新，维持骨骼或节点动画播放。
        /// </summary>
        private void UpdateAnimationFrame()
        {
            if (!_isAnimationPlaying || _animationUpdater == null)
            {
                return;
            }

            var elapsedTicks = Stopwatch.GetTimestamp() - _animationStartTicks;
            var elapsedSeconds = (double)elapsedTicks / Stopwatch.Frequency;

            float playbackSeconds;
            if (_animationDurationSeconds > 1e-4f)
            {
                var cycle = (float)(elapsedSeconds % _animationDurationSeconds);
                if (cycle < 0f)
                {
                    cycle += _animationDurationSeconds;
                }

                playbackSeconds = _animationOffsetSeconds + cycle;
            }
            else
            {
                playbackSeconds = _animationOffsetSeconds + (float)elapsedSeconds;
            }

            _animationUpdater.Update(playbackSeconds, 1);
            _loadedScene?.Root?.UpdateAllTransformMatrix();
        }

        /// <summary>
        /// 从指定路径加载模型并返回 HelixToolkitScene 对象。
        /// </summary>
        private static HelixToolkitScene? LoadHelixScene(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                AppLogger.Warn("模型路径为空，取消加载。");
                return null;
            }

            if (!File.Exists(modelPath))
            {
                AppLogger.Warn($"模型文件不存在：{modelPath}");
                return null;
            }

            try
            {
                using var importer = new Importer
                {
                    Configuration =
                    {
                        ImportAnimations = true,
        CreateSkeletonForBoneSkinningMesh = false //  不生成骨骼可视化（否则会出现红色骨骼点/蓝色骨架线）
                    }
                };

                var scene = importer.Load(modelPath);
                if (scene?.Root == null)
                {
                    AppLogger.Warn("导入的场景为空或缺少根节点。");
                    return null;
                }

                return scene;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"加载模型失败：{modelPath}");
                return null;
            }
        }

        /// <summary>
        /// 创建一个基础立方体节点作为占位模型，便于确认渲染链路。
        /// </summary>
        private static MeshNode CreateFallbackBox()
        {
            var builder = new MeshBuilder();
            builder.AddBox(SDX.Vector3.Zero, 1f, 1f, 1f);
            var mesh = builder.ToMeshGeometry3D();

            var material = new PhongMaterialCore
            {
                DiffuseColor = new SDX.Color4(0.2f, 0.4f, 1f, 1f),
                AmbientColor = new SDX.Color4(0.1f, 0.1f, 0.1f, 1f),
                SpecularColor = new SDX.Color4(1f, 1f, 1f, 1f),
                SpecularShininess = 64f
            };

            return new MeshNode
            {
                Geometry = mesh,
                Material = material,
                CullMode = D3D.CullMode.Back
            };
        }

        /// <summary>
        /// 创建柔和环境光节点以提供基础亮度。
        /// </summary>
        private static AmbientLightNode CreateAmbientLight()
        {
            return new AmbientLightNode
            {
                Color = new SDX.Color4(0.2f, 0.2f, 0.2f, 1f)
            };
        }

        /// <summary>
        /// 创建方向光节点模拟主光源。
        /// </summary>
        private static DirectionalLightNode CreateDirectionalLight()
        {
            return new DirectionalLightNode
            {
                Color = new SDX.Color4(0.9f, 0.9f, 0.9f, 1f),
                Direction = SDX.Vector3.Normalize(new SDX.Vector3(-1f, -1f, -1f))
            };
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
        /// 预留的 3D 模型旋转接口，当前核心渲染版本未启用旋转。
        /// </summary>
        public void UpdatePetRotation(double angle)
        {
            AppLogger.Info($"调用旋转占位方法，角度：{angle}");
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

        // 【新增】递归计算场景包围盒（含变换），用于相机自动居中
        private static bool TryGetSceneBounds(SceneNode node, out SDX.BoundingBox bounds)
        {
            bounds = new SDX.BoundingBox(
                new SDX.Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity),
                new SDX.Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity)
            );
            var found = false;

            // HelixToolkit 的 SceneNode 通常都提供 BoundsWithTransform
            var b = node.BoundsWithTransform;
            if (b.Minimum != b.Maximum) 
            {
                bounds.Minimum = SDX.Vector3.Min(bounds.Minimum, b.Minimum);
                bounds.Maximum = SDX.Vector3.Max(bounds.Maximum, b.Maximum);
                found = true;
            }

            foreach (var child in node.Items)
            {
                if (TryGetSceneBounds(child, out var cb))
                {
                    bounds.Minimum = SDX.Vector3.Min(bounds.Minimum, cb.Minimum);
                    bounds.Maximum = SDX.Vector3.Max(bounds.Maximum, cb.Maximum);
                    found = true;
                }
            }
            return found;
        }

    }
}