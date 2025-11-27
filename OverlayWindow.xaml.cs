using HelixToolkit.SharpDX; // Core 效果管理器
using HelixToolkit.SharpDX.Core; // ViewportCore 类型
using HelixToolkit.SharpDX.Core.Animations; // 动画更新器
using HelixToolkit.SharpDX.Core.Assimp; // 模型导入器
using HelixToolkit.SharpDX.Core.Cameras; // 相机核心类型
using HelixToolkit.SharpDX.Core.Model; // 材质定义
using HelixToolkit.SharpDX.Core.Model.Scene; // 场景节点
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        private const float ModelScaleFactor = 1.3f; // 模型放大比例   模型放大比例
        private const int RenderHeartbeat = 300; // 渲染心跳间隔帧
        private const float DefaultAnimationFrameRate = 30f; // 默认动画帧率
        private const float MinimumAnimationFrameRate = 1f; // 允许配置的最低动画

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex); // 获取窗口样式

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong); // 设置窗口样式

        private IEffectsManager? _effectsManager; // Core 效果管理器
        private PerspectiveCameraCore? _cameraCore; // 核心透视相机
        private GroupNode? _sceneRoot; // 场景根节点
        private HelixToolkitScene? _loadedScene; // 导入后的场景
        private IAnimationUpdater? _animationUpdater; // 当前动画更新器
        private IReadOnlyDictionary<string, IAnimationUpdater>? _animationUpdaters; // 所有可用动画片段
        private List<KeyValuePair<string, IAnimationUpdater>>? _sortedAnimationClips; // 便于按索引切换的动画列表
        private string? _defaultAnimationKey; // 默认动画名称，便于外部恢复
        private float _animationOffsetSeconds; // 动画播放起点
        private float _animationDurationSeconds; // 动画循环时长
        private bool _isAnimationPlaying; // 动画是否播放
        private long _animationStartTicks; // 动画起始时间刻度

        private readonly float _targetAnimationFrameRate; // 动画目标帧率
        private readonly bool _useFixedFrameRate; // 是否启用固定帧率节拍
        private int _lastAnimationFrameIndex; // 上一次应用的帧序号
        private int _animationFrameCount; // 动画关键帧数量
        private double _animationFrameDuration; // 原始关键帧间隔秒
        private double _animationSourceFrameRate; // 原始动画帧率
                                    private RenderHostDX? _renderHost; // DX11 渲染宿主
        private ViewportCore? _viewportCore; // ViewportCore 管理器
        private ImageSourceDX? _imageSource; // 输出图像源
        private bool _isRendering; // 渲染循环标记
        private bool _hasLoggedImageSource; // 是否记录过图像源绑定
        private int _frameCounter; // 渲染帧计数
        private readonly int _renderTargetWidth; // 3D 渲染目标宽度
        private readonly int _renderTargetHeight; // 3D 渲染目标高度
        private bool _useFixedRenderResolution; // 是否使用固定渲染分辨率（允许在失败时回退）
        private bool _hasSyncedFixedResolution; // 固定分辨率同步标记
        private bool _hasLoggedTimeUpdate; // 是否已经输出过更新时间的一次性日志
        private bool _hasLoggedRotationPlaceholder; // 是否已经输出过旋转占位的一次性日志
        private readonly string? _preferredAnimationName; // 用户配置的默认动画名称
        private readonly int _preferredAnimationIndex; // 用户配置的默认动画索引（1 起算）
        private readonly double _petViewportOffsetX; // 3D 容器在界面上的水平偏移（像素）
        private readonly double _petViewportOffsetY; // 3D 容器在界面上的垂直偏移（像素）
        private readonly double _petViewportScale; // 3D 容器的缩放倍率
        private readonly double _petViewportCameraVerticalBias; // 3D 相机垂直偏移比例

        /// <summary>
        /// 初始化覆盖层窗口，订阅生命周期事件并准备 Core 渲染环境。
        /// </summary>
        public OverlayWindow(AppConfig? config = null)
        {
            InitializeComponent();
            RenderOptions.SetBitmapScalingMode(PetImage, BitmapScalingMode.HighQuality);
            PetImage.SizeChanged += PetImage_SizeChanged; // 追踪 3D 容器尺寸，方便定位模型遮挡问题
            var configuredRate = config?.OverlayAnimationFrameRate ?? DefaultAnimationFrameRate; // 读取配置中的帧率
            if (configuredRate <= 0f)
            {
                _targetAnimationFrameRate = DefaultAnimationFrameRate; // 退回默认值
                _useFixedFrameRate = false; // 不强制固定节拍
            }
            else
            {
                _targetAnimationFrameRate = Math.Max(MinimumAnimationFrameRate, configuredRate); // 保障下限
                _useFixedFrameRate = true; // 启用固定帧率驱动
            }
            if (config?.OverlayRender is { Width: > 0, Height: > 0 })
            {
                _renderTargetWidth = config.OverlayRender.Width;
                _renderTargetHeight = config.OverlayRender.Height;
                _useFixedRenderResolution = true;
            }
            else
            {
                _renderTargetWidth = 0;
                _renderTargetHeight = 0;
                _useFixedRenderResolution = false;
            }
            _lastAnimationFrameIndex = -1; // 初始化帧序号
            _animationFrameCount = 0; // 初始化关键帧数量
            _animationFrameDuration = 0d; // 初始化关键帧时长
            _animationSourceFrameRate = 0d; // 初始化原始帧率
            _preferredAnimationName = string.IsNullOrWhiteSpace(config?.OverlayDefaultAnimation) ? null : config!.OverlayDefaultAnimation.Trim();
            _preferredAnimationIndex = Math.Max(0, config?.OverlayDefaultAnimationIndex ?? 0);
            _petViewportOffsetX = config?.OverlayPetPlacement?.OffsetX ?? 0d;
            _petViewportOffsetY = config?.OverlayPetPlacement?.OffsetY ?? 0d;
            _petViewportScale = config?.OverlayPetPlacement?.Scale ?? 1d;
            _petViewportCameraVerticalBias = config?.OverlayPetPlacement?.CameraVerticalBias ?? 0d;

            if (!string.IsNullOrEmpty(_preferredAnimationName))
            {
                AppLogger.Info($"OverlayWindow 动画偏好：名称 = {_preferredAnimationName}");
            }
            if (_preferredAnimationIndex > 0)
            {
                AppLogger.Info($"OverlayWindow 动画偏好：索引 = {_preferredAnimationIndex}");
            }
            if (Math.Abs(_petViewportCameraVerticalBias) > double.Epsilon)
            {
                AppLogger.Info($"OverlayWindow 3D 相机垂直偏移：bias={_petViewportCameraVerticalBias:F3} (相对高度)");
            }
            ApplyPetViewportPlacementOverrides();
            Loaded += OverlayWindow_Loaded; // 窗口加载时初始化渲染

            Unloaded += OverlayWindow_Unloaded; // 窗口卸载时清理资源
            var animationMode = _useFixedFrameRate ? $"{_targetAnimationFrameRate:F1}fps" : "CompositionTarget 实时节奏"; // 日志描述
            AppLogger.Info($"OverlayWindow 初始化，动画驱动模式：{animationMode}。");
        }

        /// <summary>
        /// 监听右下角 3D 容器的尺寸变化，记录日志以便调试摆放位置与裁剪问题。
        /// </summary>
        private void PetImage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            AppLogger.Info($"PetImage 尺寸更新：实际 {e.NewSize.Width:F0}x{e.NewSize.Height:F0}px，渲染源：{_renderTargetWidth}x{_renderTargetHeight}。");
        }
        /// <summary>
        /// 根据配置应用 3D 图像的平移与缩放补偿，帮助角色贴近右下角且避免头部被遮挡。
        /// </summary>
        private void ApplyPetViewportPlacementOverrides()
        {
            var translate = PetImageTranslateTransform;
            var scale = PetImageScaleTransform;

            var clampedOffsetX = Math.Clamp(_petViewportOffsetX, -2000d, 2000d);
            var clampedOffsetY = Math.Clamp(_petViewportOffsetY, -2000d, 2000d);
            var clampedScale = _petViewportScale <= 0d ? 1d : Math.Clamp(_petViewportScale, 0.2d, 4d);

            if (translate != null)
            {
                translate.X = clampedOffsetX;
                translate.Y = clampedOffsetY;
            }

            if (scale != null)
            {
                scale.ScaleX = clampedScale;
                scale.ScaleY = clampedScale;
            }

            AppLogger.Info($"PetImage 视口补偿：Offset=({clampedOffsetX:F1},{clampedOffsetY:F1})px Scale={clampedScale:F3}");
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

                var scaleMatrix = SDX.Matrix.Scaling(ModelScaleFactor); // 模型缩放矩阵
                _loadedScene.Root.ModelMatrix = scaleMatrix * _loadedScene.Root.ModelMatrix; // 应用统一缩放
                AppLogger.Info($"模型加载成功：{modelPath}，缩放系数：{ModelScaleFactor:P0}");

                ApplyPreferredRenderTechniques(_loadedScene); // 统一材质渲染技术

                InitializeAnimationPlayer();
                _loadedScene?.Root?.UpdateAllTransformMatrix(); //  确保 BoundsWithTransform 是最新的


                //  根据场景包围盒自动对齐相机的垂直中心，并允许按照配置附加偏移，避免“看不到头”
                if (_cameraCore != null && TryGetSceneBounds(_loadedScene.Root, out var bb))
                {
                    var centerY = (bb.Minimum.Y + bb.Maximum.Y) * 0.5f;
                    var boundsHeight = Math.Max(0.0001f, bb.Maximum.Y - bb.Minimum.Y);
                    var biasRatio = Math.Clamp(_petViewportCameraVerticalBias, -1d, 1d);
                    var biasedCenterY = centerY + (float)(boundsHeight * biasRatio);
                    var pos = _cameraCore.Position;
                    var look = _cameraCore.LookDirection;
                    var target = pos + look;
                    var deltaY = biasedCenterY - target.Y;
                    _cameraCore.LookDirection = new SDX.Vector3(look.X, look.Y + deltaY, look.Z); // 仅抬/降目标点
                    AppLogger.Info($"Camera vertical aligned: centerY={centerY:F3} height={boundsHeight:F3} bias={biasRatio:F3} targetY={biasedCenterY:F3} ΔY={deltaY:F3}。");
                }
            }
            else
            {
                AppLogger.Warn("模型加载失败，使用占位立方体。");
                _sceneRoot.AddChildNode(CreateFallbackBox());
            }

            _sceneRoot.AddChildNode(CreateAmbientLight());
            _sceneRoot.AddChildNode(CreateDirectionalLight());
            _sceneRoot.AddChildNode(CreateFillLight());

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

            var initialWidth = _useFixedRenderResolution ? Math.Max(1, _renderTargetWidth) : Math.Max(1, (int)Math.Ceiling(PetImage.ActualWidth));
            var initialHeight = _useFixedRenderResolution ? Math.Max(1, _renderTargetHeight) : Math.Max(1, (int)Math.Ceiling(PetImage.ActualHeight));

            if (!TryStartRenderTarget(initialWidth, initialHeight, _useFixedRenderResolution))
            {
                // 若固定分辨率失败，则尝试回退到控件实际尺寸，避免高分辨率导致 Direct3D 初始化异常后 3D 模型消失。
                AppLogger.Warn($"固定渲染分辨率 {_renderTargetWidth}x{_renderTargetHeight} 初始化失败，准备回退到控件尺寸重试。");
                var fallbackWidth = Math.Max(1, (int)Math.Ceiling(PetImage.ActualWidth <= 0 ? 1280 : PetImage.ActualWidth));
                var fallbackHeight = Math.Max(1, (int)Math.Ceiling(PetImage.ActualHeight <= 0 ? 720 : PetImage.ActualHeight));

                _useFixedRenderResolution = false; // 回退到动态分辨率模式
                _hasSyncedFixedResolution = false; // 重置同步标记，防止后续误判

                if (!TryStartRenderTarget(fallbackWidth, fallbackHeight, false))
                {
                    AppLogger.Error("StartD3D 在回退分辨率下仍然失败，3D 渲染将无法显示。");
                    return;
                }
            }

            _renderHost.UpdateAndRender();

            CompositionTarget.Rendering += OnCompositionRendering;
            _isRendering = true;
            _frameCounter = 0;
            _hasLoggedImageSource = false;
            _isAnimationPlaying = _animationUpdater != null;
            _animationStartTicks = Stopwatch.GetTimestamp();
            _lastAnimationFrameIndex = -1;
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
            _animationFrameCount = 0; // 重置关键帧数量
            _animationFrameDuration = 0d; // 重置关键帧时长
            _animationSourceFrameRate = 0d; // 重置原始帧率
            _hasSyncedFixedResolution = false; // 重置固定分辨率标记
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
        /// 封装 StartD3D 并捕获异常，便于在高分辨率失败时输出日志并决定是否回退。
        /// </summary>
        /// <param name="width">目标渲染宽度。</param>
        /// <param name="height">目标渲染高度。</param>
        /// <param name="isFixed">是否处于固定分辨率模式。</param>
        /// <returns>StartD3D 是否执行成功。</returns>
        private bool TryStartRenderTarget(int width, int height, bool isFixed)
        {
            if (_renderHost == null)
            {
                AppLogger.Warn("StartD3D 被跳过：渲染宿主尚未初始化。");
                return false;
            }

            try
            {
                _renderHost.StartD3D(width, height);
                AppLogger.Info($"StartD3D: {width}x{height} (fixed={isFixed})");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"StartD3D 失败：{width}x{height} (fixed={isFixed})");
                return false;
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

            if (_useFixedRenderResolution)
            {
                var width = Math.Max(1, _renderTargetWidth); // 固定宽度
                var height = Math.Max(1, _renderTargetHeight); // 固定高度
                _renderHost.Resize(width, height);
                if (!_hasSyncedFixedResolution)
                {
                    _hasSyncedFixedResolution = true;
                    AppLogger.Info($"PetImage 固定渲染分辨率：{width}x{height}");
                }
                return;
            }

            var dynamicWidth = Math.Max(1, (int)Math.Ceiling(e.NewSize.Width));
            var dynamicHeight = Math.Max(1, (int)Math.Ceiling(e.NewSize.Height));
            _renderHost.Resize(dynamicWidth, dynamicHeight);
            AppLogger.Info($"PetImage 尺寸变化：{dynamicWidth}x{dynamicHeight}");
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
            var sortedClips = updaters
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToList(); // 排序后保证索引选择具有确定性

            _animationUpdaters = updaters;
            _sortedAnimationClips = sortedClips;

            foreach (var clip in sortedClips)
            {
                AppLogger.Info($"检测到动画片段：{clip.Key}");
            }

            _animationUpdater = ResolvePreferredAnimation(updaters, sortedClips)
                                ?? (updaters.TryGetValue("Idle", out var idle) ? idle : sortedClips.FirstOrDefault().Value);
            if (_animationUpdater == null)
            {
                _isAnimationPlaying = false;
                AppLogger.Warn("未能创建动画更新器，模型将静态展示。");
                return;
            }

            _defaultAnimationKey = _animationUpdater.Name;

            ApplyAnimationUpdater(_animationUpdater, "初始化默认动画");
        }

        /// <summary>
        /// 根据配置优先匹配指定的动画名称或索引。
        /// </summary>
        /// <param name="updaters">动画名称与更新器的映射表。</param>
        /// <param name="sortedClips">按名称排序后的动画列表，用于索引选择。</param>
        /// <returns>匹配到的动画更新器；若未命中则返回 null。</returns>
        private IAnimationUpdater? ResolvePreferredAnimation(
            IReadOnlyDictionary<string, IAnimationUpdater> updaters,
            IReadOnlyList<KeyValuePair<string, IAnimationUpdater>> sortedClips)
        {
            if (!string.IsNullOrEmpty(_preferredAnimationName))
            {
                if (updaters.TryGetValue(_preferredAnimationName, out var namedUpdater))
                {
                    AppLogger.Info($"按名称命中默认动画：{_preferredAnimationName}");
                    return namedUpdater;
                }

                AppLogger.Warn($"未找到名称为 {_preferredAnimationName} 的动画片段，尝试按索引匹配。");
            }

            if (_preferredAnimationIndex > 0)
            {
                var zeroBasedIndex = _preferredAnimationIndex - 1;
                if (zeroBasedIndex >= 0 && zeroBasedIndex < sortedClips.Count)
                {
                    var indexedClip = sortedClips[zeroBasedIndex];
                    AppLogger.Info($"按索引 {_preferredAnimationIndex} 选择默认动画：{indexedClip.Key}");
                    return indexedClip.Value;
                }

                AppLogger.Warn($"动画索引 {_preferredAnimationIndex} 超出范围（有效范围：1-{sortedClips.Count}），将回退至 Idle/首个动画。");
            }

            return null;
        }

        /// <summary>
        /// 按索引切换动画，索引从 1 开始；用于外部根据业务状态临时切换模型表现。
        /// </summary>
        /// <param name="index">目标动画序号（1 起算，对应排序后的动画列表）。</param>
        /// <param name="triggerReason">触发原因，用于日志说明。</param>
        public void SwitchAnimationByIndex(int index, string triggerReason)
        {
            if (_sortedAnimationClips == null || _animationUpdaters == null)
            {
                AppLogger.Warn($"切换动画失败：动画列表未初始化。触发来源：{triggerReason}");
                return;
            }

            if (index <= 0)
            {
                AppLogger.Warn($"切换动画失败：索引 {index} 非法，触发来源：{triggerReason}");
                return;
            }

            var zeroBasedIndex = index - 1;
            if (zeroBasedIndex < 0 || zeroBasedIndex >= _sortedAnimationClips.Count)
            {
                AppLogger.Warn($"切换动画失败：索引 {index} 超出范围（1-{_sortedAnimationClips.Count}）。触发来源：{triggerReason}");
                return;
            }

            var targetClip = _sortedAnimationClips[zeroBasedIndex];
            if (ReferenceEquals(_animationUpdater, targetClip.Value))
            {
                AppLogger.Info($"动画已处于目标状态：{targetClip.Key}，触发来源：{triggerReason}");
                return;
            }

            AppLogger.Info($"准备切换动画至索引 {index}（{targetClip.Key}），触发来源：{triggerReason}");
            ApplyAnimationUpdater(targetClip.Value, $"外部切换：{triggerReason}");
        }

        /// <summary>
        /// 恢复初始化时的默认动画，适用于临时动画播放结束后的回退处理。
        /// </summary>
        /// <param name="triggerReason">触发原因，用于日志说明。</param>
        public void RestoreDefaultAnimation(string triggerReason)
        {
            if (string.IsNullOrWhiteSpace(_defaultAnimationKey))
            {
                AppLogger.Warn($"无法恢复默认动画：未记录默认动画键。触发来源：{triggerReason}");
                return;
            }

            if (_animationUpdaters == null || !_animationUpdaters.TryGetValue(_defaultAnimationKey, out var defaultUpdater))
            {
                AppLogger.Warn($"无法恢复默认动画：未找到名称 {_defaultAnimationKey} 对应的动画。触发来源：{triggerReason}");
                return;
            }

            if (ReferenceEquals(_animationUpdater, defaultUpdater))
            {
                AppLogger.Info($"当前已处于默认动画：{_defaultAnimationKey}，触发来源：{triggerReason}");
                return;
            }

            AppLogger.Info($"恢复默认动画：{_defaultAnimationKey}，触发来源：{triggerReason}");
            ApplyAnimationUpdater(defaultUpdater, $"恢复默认动画：{triggerReason}");
        }

        /// <summary>
        /// 配置并启用指定的动画更新器，同时重置播放状态与统计信息。
        /// </summary>
        /// <param name="updater">目标动画更新器。</param>
        /// <param name="reason">触发原因，用于日志说明。</param>
        private void ApplyAnimationUpdater(IAnimationUpdater updater, string reason)
        {
            _animationUpdater = updater;
            _animationUpdater.RepeatMode = AnimationRepeatMode.Loop;
            _animationUpdater.Reset();
            _animationStartTicks = Stopwatch.GetTimestamp();
            _isAnimationPlaying = true;
            _lastAnimationFrameIndex = -1;

            RecalculateAnimationMetrics(updater);

            AppLogger.Info($"动画已切换为：{updater.Name}，触发原因：{reason}，Offset={_animationOffsetSeconds:F3}s Duration={_animationDurationSeconds:F3}s");
        }

        /// <summary>
        /// 重新计算动画的时间指标与帧率统计，便于外部切换后保持日志一致性。
        /// </summary>
        /// <param name="updater">当前生效的动画更新器。</param>
        private void RecalculateAnimationMetrics(IAnimationUpdater updater)
        {
            _animationOffsetSeconds = updater.StartTime;
            _animationDurationSeconds = Math.Max(0f, updater.EndTime - updater.StartTime);
            _animationFrameDuration = 0d;
            _animationFrameCount = 0;
            _animationSourceFrameRate = 0d;

            if (updater is NodeAnimationUpdater nodeUpdater)
            {
                var minTime = float.PositiveInfinity; // 最早关键帧
                var maxTime = float.NegativeInfinity; // 最晚关键帧
                var keyframeCount = 0; // 关键帧数量
                var uniqueTimes = new SortedSet<float>(); // 唯一关键帧时间集合

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
                        uniqueTimes.Add(frame.Time);
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

                _animationFrameDuration = _animationDurationSeconds > 0f
                    ? _animationDurationSeconds / Math.Max(1, uniqueTimes.Count)
                    : 0d;

                _animationFrameCount = uniqueTimes.Count;

                var playbackMax = Math.Max(_animationOffsetSeconds + _animationDurationSeconds, nodeUpdater.EndTime);

                var estimatedCycleSeconds = _animationFrameCount > 1 ? _animationFrameDuration * (_animationFrameCount - 1) : 0d; // 源动画单周期时长
                var targetCycleSeconds = _targetAnimationFrameRate > 1e-4f && _animationFrameCount > 0 ? _animationFrameCount / _targetAnimationFrameRate : 0d; // 目标帧率下单周期时长
                _animationSourceFrameRate = _animationFrameDuration > 1e-6d ? 1.0 / _animationFrameDuration : 0d; // 估算原始帧率

                AppLogger.Info($"动画关键帧统计：Count={keyframeCount} Unique={_animationFrameCount} Min={_animationOffsetSeconds:F3}s Max={playbackMax:F3}s SrcFPS={_animationSourceFrameRate:F2} Cycle@Src={estimatedCycleSeconds:F3}s Cycle@Target={targetCycleSeconds:F3}s");
            }
            else
            {
                _animationFrameCount = 1; // 非节点动画兜底设置，避免后续计算除零
                _animationFrameDuration = _animationDurationSeconds;
                _animationSourceFrameRate = _animationFrameDuration > 1e-6d ? 1.0 / _animationFrameDuration : 0d;
                AppLogger.Info($"动画关键帧统计：Count=1 Unique={_animationFrameCount} Min={_animationOffsetSeconds:F3}s Max={_animationOffsetSeconds + _animationDurationSeconds:F3}s SrcFPS={_animationSourceFrameRate:F2}（非节点动画兜底计算）");
            }
        }

        /// <summary>
        /// 按固定帧率或实际时间步进动画，确保渲染与模型播放节奏一致。
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
            if (_useFixedFrameRate && _targetAnimationFrameRate > 1e-4f)
            {
                var frameDuration = 1.0 / _targetAnimationFrameRate; // 单帧耗时
                var frameIndex = (int)Math.Floor(elapsedSeconds / frameDuration); // 当前帧序号
                if (frameIndex == _lastAnimationFrameIndex)
                {
                    return;
                }

                _lastAnimationFrameIndex = frameIndex;

                if (_animationFrameDuration > 1e-6d && _animationFrameCount > 0)
                {
                    var totalFrameCount = Math.Max(1, _animationFrameCount);
                    var wrappedIndex = frameIndex % totalFrameCount;
                    if (wrappedIndex < 0)
                    {
                        wrappedIndex += totalFrameCount;
                    }

                    var candidateSeconds = _animationOffsetSeconds + (float)(wrappedIndex * _animationFrameDuration);
                    if (_animationDurationSeconds > 1e-4f)
                    {
                        var minSeconds = _animationOffsetSeconds;
                        var maxSeconds = _animationOffsetSeconds + _animationDurationSeconds;
                        if (candidateSeconds < minSeconds)
                        {
                            candidateSeconds = minSeconds;
                        }
                        else if (candidateSeconds > maxSeconds)
                        {
                            candidateSeconds = maxSeconds;
                        }
                    }

                    playbackSeconds = candidateSeconds;
                }
                else if (_animationDurationSeconds > 1e-4f)
                {
                    var totalFrameCount = Math.Max(1, (int)Math.Round(_animationDurationSeconds * _targetAnimationFrameRate)); // 循环内总帧数
                    if (totalFrameCount > 0)
                    {
                        var wrappedIndex = frameIndex % totalFrameCount;
                        if (wrappedIndex < 0)
                        {
                            wrappedIndex += totalFrameCount;
                        }

                        playbackSeconds = _animationOffsetSeconds + (float)(wrappedIndex / _targetAnimationFrameRate);
                    }
                    else
                    {
                        playbackSeconds = _animationOffsetSeconds;
                    }
                }
                else
                {
                    playbackSeconds = _animationOffsetSeconds + (float)(frameIndex / _targetAnimationFrameRate);
                }
            }
            else if (_animationDurationSeconds > 1e-4f)
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
        /// 遍历已加载场景的所有网格节点，将 PBR 材质转换为 Blinn/Phong 技术并输出详细统计日志，保障缺失环境贴图时的亮度。
        /// </summary>
        private void ApplyPreferredRenderTechniques(HelixToolkitScene scene)
        {
            if (scene?.Root == null)
            {
                AppLogger.Warn("场景为空，无法调整渲染技术。");
                return;
            }

            var nodeStack = new Stack<SceneNode>(); // 深度遍历栈
            var totalMeshCount = 0; // 网格节点计数
            var convertedMeshCount = 0; // 成功转换的网格计数
            var retainedMeshCount = 0; // 维持原材质的网格计数

            nodeStack.Push(scene.Root);

            while (nodeStack.Count > 0)
            {
                var currentNode = nodeStack.Pop(); // 当前节点

                if (currentNode.Items != null)
                {
                    for (var i = 0; i < currentNode.Items.Count; i++)
                    {
                        var childNode = currentNode.Items[i]; // 子节点
                        if (childNode != null)
                        {
                            nodeStack.Push(childNode);
                        }
                    }
                }

                if (currentNode is not MeshNode meshNode)
                {
                    continue;
                }

                totalMeshCount++;

                if (meshNode.Material is PBRMaterialCore pbrMaterial)
                {
                    var baseColor = pbrMaterial.AlbedoColor; // PBR 基础色
                    var emissiveColor = pbrMaterial.EmissiveColor; // 自发光色
                  
                    var roughness = Math.Clamp(pbrMaterial.RoughnessFactor, 0f, 1f); // 粗糙度
                    var diffuseFactor = 0.82f + 0.1f * roughness; // 漫反射衰减系数
                    var ambientFactor = 0.18f + 0.32f * (1f - roughness); // 环境光系数
                    var ambientColor = new SDX.Color4( // 计算较柔和的环境分量
                        Math.Clamp(baseColor.Red * ambientFactor + emissiveColor.Red * 0.6f, 0f, 1f),
                        Math.Clamp(baseColor.Green * ambientFactor + emissiveColor.Green * 0.6f, 0f, 1f),
                        Math.Clamp(baseColor.Blue * ambientFactor + emissiveColor.Blue * 0.6f, 0f, 1f),
                        1f);
                    var diffuseColor = new SDX.Color4( // 漫反射颜色
                        Math.Clamp(baseColor.Red * diffuseFactor, 0f, 1f),
                        Math.Clamp(baseColor.Green * diffuseFactor, 0f, 1f),
                        Math.Clamp(baseColor.Blue * diffuseFactor, 0f, 1f),
                        baseColor.Alpha);
                    var specularShininess = 48f + (160f - 48f) * (1f - roughness); // 高光锐度

                    var phongMaterial = new PhongMaterialCore
                    {
                    
                        DiffuseColor = diffuseColor,
                        AmbientColor = ambientColor,
                        EmissiveColor = emissiveColor,
                     
                        SpecularColor = new SDX.Color4(0.65f, 0.65f, 0.65f, 1f),
                        SpecularShininess = specularShininess,
                        DiffuseMap = pbrMaterial.AlbedoMap
                    };

                    meshNode.Material = phongMaterial;
                    convertedMeshCount++;
                    AppLogger.Info($"Mesh[{meshNode.Name ?? "<未命名>"}] PBR 材质已转换为 Blinn/Phong 管线。");
                }
                else
                {
                    retainedMeshCount++;
                    if (meshNode.Material == null)
                    {
                        AppLogger.Info($"Mesh[{meshNode.Name ?? "<未命名>"}] 未设置材质，维持默认渲染技术。");
                    }
                }
            }

            AppLogger.Info($"材质处理完成：网格总数={totalMeshCount}，PBR 转换={convertedMeshCount}，原材质保留={retainedMeshCount}。");
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

            FileInfo? fileInfo = null;
            try
            {
                fileInfo = new FileInfo(modelPath);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"无法读取模型文件信息：{modelPath}");
                return null;
            }

            if (fileInfo.Exists == false)
            {
                AppLogger.Warn($"模型文件不存在：{modelPath}");
                return null;
            }

            AppLogger.Info($"模型文件检测：路径={fileInfo.FullName}，大小={FormatFileSize(fileInfo.Length)}，修改时间={fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            if (TryGetFirstChinesePathSegment(fileInfo.FullName, out var chineseSegment))
            {
                var segmentDisplay = string.IsNullOrEmpty(chineseSegment) ? "<未知>" : chineseSegment;
                AppLogger.Warn(
                    $"模型路径包含中文目录或文件名 \"{segmentDisplay}\"，当前随附的 Assimp 原生库在部分系统上无法解析 Unicode 路径，建议改为纯英文路径：{fileInfo.DirectoryName ?? fileInfo.FullName}");
            }
            var sha256 = TryComputeSha256(fileInfo.FullName);
            if (!string.IsNullOrEmpty(sha256))
            {
                AppLogger.Info($"模型文件校验：SHA256={sha256}");
            }
            else
            {
                AppLogger.Warn("模型文件 SHA256 计算失败，可能存在 IO 访问问题。");
            }

            var glbInspection = InspectGlbStructure(fileInfo.FullName);
            if (glbInspection != null)
            {
                AppLogger.Info(
                    $"GLB 结构验证：magic={glbInspection.MagicTag} headerValid={glbInspection.HeaderValid} version={glbInspection.Version} declaredLength={glbInspection.DeclaredLength} lengthMatch={glbInspection.IsLengthMatched} chunkCount={glbInspection.ChunkCount} jsonBytes={glbInspection.JsonChunkLength} binBytes={glbInspection.BinaryChunkLength} scenes={glbInspection.SceneCount} nodes={glbInspection.NodeCount} meshes={glbInspection.MeshCount} skins={glbInspection.SkinCount} animations={glbInspection.AnimationCount} defaultScene={glbInspection.DefaultSceneIndex?.ToString() ?? "<未指定>"} rootNodes={glbInspection.DefaultSceneRootCount}。");

                if (glbInspection.SampleNodeNames.Count > 0)
                {
                    AppLogger.Info($"GLB 节点示例：{string.Join(", ", glbInspection.SampleNodeNames)}");
                }

                if (glbInspection.MeshCount == 0)
                {
                    AppLogger.Warn("GLB JSON 中未检测到 meshes，模型文件可能仅包含动画或骨骼数据。");
                }
            }
            else
            {
                AppLogger.Warn("GLB 结构解析失败，文件可能已损坏或非标准 GLB。");
            }
            if (fileInfo.Length <= 0)
            {
                AppLogger.Warn("模型文件大小为 0，可能在发布或复制过程中被损坏。");
                return null;
            }

            var extension = fileInfo.Extension; // 模型扩展名
            if (!string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Warn($"当前仅支持 GLB 模型，拒绝加载：{modelPath}");
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
                    AppLogger.Warn($"导入的场景为空或缺少根节点。请检查 GLB 是否包含网格数据，当前文件大小：{FormatFileSize(fileInfo.Length)}。");
                    return null;
                }

                var meshCount = CountMeshNodes(scene.Root);
                AppLogger.Info($"GLB 模型加载完成，根节点子项={scene.Root.Items?.Count ?? 0}，网格节点数={meshCount}。");

                return scene;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"加载模型失败：{modelPath}");
                return null;
            }
        }


        /// <summary>
        /// 尝试找出模型路径中第一个包含中文字符的目录片段，方便日志指向具体目录。
        /// </summary>
        private static bool TryGetFirstChinesePathSegment(string? fullPath, out string? offendingSegment)
        {
            offendingSegment = null;
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return false;
            }

            // 统一目录分隔符，避免同时存在 / 与 \ 导致拆分困难。
            var normalized = fullPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                if (ContainsChineseCharacters(segment))
                {
                    offendingSegment = segment;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 统计场景树中的网格节点数量，方便定位发布后“空场景”问题。
        /// </summary>
        private static int CountMeshNodes(SceneNode? node)
        {
            if (node == null)
            {
                return 0;
            }

            var count = node is MeshNode ? 1 : 0;
            if (node.Items == null || node.Items.Count == 0)
            {
                return count;
            }

            foreach (var child in node.Items)
            {
                count += CountMeshNodes(child);
            }

            return count;
        }

        /// <summary>
        /// 计算给定文件的 SHA256 摘要，帮助对比不同机器上的发布资源是否一致。
        /// </summary>
        private static string? TryComputeSha256(string path)
        {
            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var sha256 = SHA256.Create();
                var hashBytes = sha256.ComputeHash(stream);
                var builder = new StringBuilder(hashBytes.Length * 2);
                foreach (var b in hashBytes)
                {
                    builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"计算 SHA256 失败：{path}");
                return null;
            }
        }

        /// <summary>
        /// 检测路径中是否包含中文字符，用于提醒用户避免将模型放在不兼容的目录下。
        /// </summary>
        private static bool ContainsChineseCharacters(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            foreach (var ch in text)
            {
                if (IsChineseCharacter(ch))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 判断单个字符是否处于常见的中日韩统一表意文字区间。
        /// </summary>
        private static bool IsChineseCharacter(char ch)
        {
            return (ch >= '\u4E00' && ch <= '\u9FFF') // 基本汉字
                   || (ch >= '\u3400' && ch <= '\u4DBF') // 扩展 A
                   || (ch >= '\uF900' && ch <= '\uFAFF'); // 兼容汉字
        }

        /// <summary>
        /// 解析 GLB 文件头和 JSON Chunk，输出基础结构与节点统计，便于对比 Assimp 导入失败的根因。
        /// </summary>
        private static GlbInspectionResult? InspectGlbStructure(string path)
        {
            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length < 12)
                {
                    AppLogger.Warn($"GLB 文件长度不足 12 字节，无法读取头信息：{path}");
                    return null;
                }

                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                var magic = reader.ReadUInt32();
                var version = reader.ReadUInt32();
                var declaredLength = reader.ReadUInt32();

                const uint JsonChunkType = 0x4E4F534A; // 'JSON'
                const uint BinChunkType = 0x004E4942; // 'BIN\0'

                var chunkCount = 0;
                uint jsonChunkLength = 0;
                uint binChunkLength = 0;
                byte[]? jsonChunk = null;

                while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
                {
                    var chunkLength = reader.ReadUInt32();
                    var chunkType = reader.ReadUInt32();
                    chunkCount++;

                    if (chunkLength > reader.BaseStream.Length - reader.BaseStream.Position)
                    {
                        AppLogger.Warn($"GLB Chunk 长度异常，chunkType=0x{chunkType:X8} chunkLength={chunkLength}，剩余可读字节={reader.BaseStream.Length - reader.BaseStream.Position}。");
                        break;
                    }

                    if (chunkType == JsonChunkType)
                    {
                        var data = reader.ReadBytes((int)chunkLength);
                        if (data.Length < chunkLength)
                        {
                            AppLogger.Warn($"读取 GLB JSON Chunk 时遇到 EOF，期望 {chunkLength} 字节，仅得到 {data.Length} 字节。");
                            break;
                        }

                        jsonChunk = data;
                        jsonChunkLength = chunkLength;
                    }
                    else
                    {
                        reader.BaseStream.Seek(chunkLength, SeekOrigin.Current);
                        if (chunkType == BinChunkType)
                        {
                            binChunkLength = chunkLength;
                        }
                    }
                }

                var sampleNodeNames = new List<string>();
                var sceneCount = 0;
                var nodeCount = 0;
                var meshCount = 0;
                var skinCount = 0;
                var animationCount = 0;
                int? defaultSceneIndex = null;
                var defaultSceneRootCount = 0;

                if (jsonChunk != null && jsonChunk.Length > 0)
                {
                    using var json = JsonDocument.Parse(jsonChunk);
                    var root = json.RootElement;

                    if (root.TryGetProperty("scenes", out var scenesElement) && scenesElement.ValueKind == JsonValueKind.Array)
                    {
                        sceneCount = scenesElement.GetArrayLength();
                    }

                    if (root.TryGetProperty("scene", out var sceneIndexElement) && sceneIndexElement.ValueKind == JsonValueKind.Number && sceneIndexElement.TryGetInt32(out var index))
                    {
                        defaultSceneIndex = index;
                    }

                    if (defaultSceneIndex.HasValue && root.TryGetProperty("scenes", out scenesElement) && scenesElement.ValueKind == JsonValueKind.Array)
                    {
                        var sceneIdx = defaultSceneIndex.Value;
                        if (sceneIdx >= 0 && sceneIdx < scenesElement.GetArrayLength())
                        {
                            var defaultScene = scenesElement[sceneIdx];
                            if (defaultScene.TryGetProperty("nodes", out var defaultNodes) && defaultNodes.ValueKind == JsonValueKind.Array)
                            {
                                defaultSceneRootCount = defaultNodes.GetArrayLength();
                            }
                        }
                    }

                    if (root.TryGetProperty("nodes", out var nodesElement) && nodesElement.ValueKind == JsonValueKind.Array)
                    {
                        nodeCount = nodesElement.GetArrayLength();
                        foreach (var nodeElement in nodesElement.EnumerateArray())
                        {
                            if (sampleNodeNames.Count >= 5)
                            {
                                break;
                            }

                            if (nodeElement.TryGetProperty("name", out var nameElement))
                            {
                                var nodeName = nameElement.GetString();
                                if (!string.IsNullOrWhiteSpace(nodeName))
                                {
                                    sampleNodeNames.Add(nodeName!);
                                }
                            }
                        }
                    }

                    if (root.TryGetProperty("meshes", out var meshesElement) && meshesElement.ValueKind == JsonValueKind.Array)
                    {
                        meshCount = meshesElement.GetArrayLength();
                    }

                    if (root.TryGetProperty("skins", out var skinsElement) && skinsElement.ValueKind == JsonValueKind.Array)
                    {
                        skinCount = skinsElement.GetArrayLength();
                    }

                    if (root.TryGetProperty("animations", out var animationsElement) && animationsElement.ValueKind == JsonValueKind.Array)
                    {
                        animationCount = animationsElement.GetArrayLength();
                    }
                }

                return new GlbInspectionResult
                {
                    MagicTag = Encoding.ASCII.GetString(BitConverter.GetBytes(magic)),
                    Version = version,
                    DeclaredLength = declaredLength,
                    ChunkCount = chunkCount,
                    JsonChunkLength = jsonChunkLength,
                    BinaryChunkLength = binChunkLength,
                    SceneCount = sceneCount,
                    NodeCount = nodeCount,
                    MeshCount = meshCount,
                    SkinCount = skinCount,
                    AnimationCount = animationCount,
                    DefaultSceneIndex = defaultSceneIndex,
                    DefaultSceneRootCount = defaultSceneRootCount,
                    SampleNodeNames = sampleNodeNames,
                    HeaderValid = magic == 0x46546C67 && version >= 2,
                    IsLengthMatched = declaredLength == stream.Length
                };
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"解析 GLB 结构失败：{path}");
                return null;
            }
        }

        /// <summary>
        /// 用于承载 GLB 结构分析结果的内部模型。
        /// </summary>
        private sealed class GlbInspectionResult
        {
            public required string MagicTag { get; init; }
            public uint Version { get; init; }
            public uint DeclaredLength { get; init; }
            public int ChunkCount { get; init; }
            public uint JsonChunkLength { get; init; }
            public uint BinaryChunkLength { get; init; }
            public int SceneCount { get; init; }
            public int NodeCount { get; init; }
            public int MeshCount { get; init; }
            public int SkinCount { get; init; }
            public int AnimationCount { get; init; }
            public int? DefaultSceneIndex { get; init; }
            public int DefaultSceneRootCount { get; init; }
            public required IReadOnlyList<string> SampleNodeNames { get; init; }
            public bool HeaderValid { get; init; }
            public bool IsLengthMatched { get; init; }
        }

        /// <summary>
        /// 将字节长度转换为易读字符串，帮助分析资源发布问题。
        /// </summary>
        private static string FormatFileSize(long length)
        {
            if (length < 1024)
            {
                return $"{length} 字节";
            }

            var size = length / 1024d;
            var unit = "KB";
            if (size >= 1024d)
            {
                size /= 1024d;
                unit = "MB";
            }

            return $"{size.ToString("F2", CultureInfo.InvariantCulture)} {unit}";
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
                Color = new SDX.Color4(0.45f, 0.45f, 0.45f, 1f)
            };
        }

        /// <summary>
        /// 创建方向光节点模拟主光源。 【主光】
        /// </summary>
        private static DirectionalLightNode CreateDirectionalLight()
        {
            return new DirectionalLightNode
            {
                Color = new SDX.Color4(1.2f, 1.2f, 1.2f, 1f), //红色 (R)、绿色 (G)、蓝色 (B) 的强度，以及 透明度/不透明度
                Direction = SDX.Vector3.Normalize(new SDX.Vector3(-1f, -1f, -1f))
            };
        }

        /// <summary>
        /// 创建辅助方向光以提升面部和背光区域亮度。 【补光】
        /// </summary>
        private static DirectionalLightNode CreateFillLight()
        {
            return new DirectionalLightNode
            {
                Color = new SDX.Color4(0.2f, 0.2f, 0.2f, 1f), //红色 (R)、绿色 (G)、蓝色 (B) 的强度，以及 透明度/不透明度
                Direction = SDX.Vector3.Normalize(new SDX.Vector3(0.5f, -0.3f, 0.2f))
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
            if (!_hasLoggedTimeUpdate)
            {
                _hasLoggedTimeUpdate = true;
                AppLogger.Info($"首次更新时间显示：{timeText}（后续更新静默以避免日志刷屏）");
            }
        }

        /// <summary>
        /// 预留的 3D 模型旋转接口，当前核心渲染版本未启用旋转。
        /// </summary>
        public void UpdatePetRotation(double angle)
        {
            if (!_hasLoggedRotationPlaceholder)
            {
                _hasLoggedRotationPlaceholder = true;
                AppLogger.Info($"首次调用旋转占位方法，角度：{angle}（占位逻辑仅提示一次日志）");
            }
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