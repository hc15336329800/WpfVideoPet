using HelixToolkit.Scene; // SceneNode 类型
using HelixToolkit.SharpDX.Assimp; // 导入器类型
using HelixToolkit.SharpDX.Core.Animations; // 动画控制器与循环模式
using HelixToolkit.SharpDX.Core.Assimp;
using HelixToolkit.Wpf.SharpDX; // DX11 控件
using LibVLCSharp.Shared;
using SharpDX.Direct3D9;
using System; 
using System.Linq; 
using System.Reflection.Metadata;
using System.Runtime.InteropServices; 
using System.Windows; 
using System.Windows.Input;
using System.Windows.Interop; 
 
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
 
        private IAnimationController _animator;   // 【新增】动画控制器引用 
 
        public OverlayWindow() 
        { 
            InitializeComponent(); 
            this.Loaded += OverlayWindow_Loaded; // 【新增】窗口加载时载入模型 
        } 
 
        protected override void OnSourceInitialized(EventArgs e) 
        { 
            base.OnSourceInitialized(e); 
            var handle = new WindowInteropHelper(this).Handle; 
            var styles = GetWindowLong(handle, GwlExstyle); 
            SetWindowLong(handle, GwlExstyle, styles | WsExTransparent | WsExNoActivate); 
        } 
 
        /// <summary> 
        /// 加载 .glb 模型并自动播放首个或 Idle 动画。 
        /// </summary> 
        private void OverlayWindow_Loaded(object sender, RoutedEventArgs e) 
        { 
            try 
            { 
                // 【修改点】把 .glb 与可能的“贴图文件”文件夹放到同一目录，保持相对路径 
                string modelPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, 
                    "Assets", "Models", "8f882a96eb3b49a29cba9b87c71209cc.glb"); 
 
                if (!System.IO.File.Exists(modelPath)) 
                { 
                    AppLogger.Warn($"模型文件不存在：{modelPath}"); 
                    return; 
                } 
 
                // 导入器：启用骨骼蒙皮 
                var importer = new Importer // glTF/GLB 导入器
                { 
                    Configuration = { CreateSkeletonForBoneSkinning = true } 
                }; 
 
                // 加载 glb => SceneNode 
                SceneNode node = importer.Load(modelPath); 
                if (node == null) 
                { 
                    AppLogger.Warn($"模型加载失败：{modelPath}"); 
                    return; 
                } 
 
                // 清空并加入到场景 
                SceneRoot.Children.Clear(); 
                SceneRoot.Children.Add(node); 
 
                // 若包含动画则播放 
                _animator = node.TryGetAnimationController(); 
                if (_animator != null) 
                { 
                    _animator.RepeatMode = AnimationRepeatMode.Loop; // 【修改点】正确的循环枚举 
            string clip = _animator.AnimationNames.Contains("Idle") 
                        ? "Idle" 
                        : _animator.AnimationNames.FirstOrDefault() ?? string.Empty; 
 
                    if (!string.IsNullOrEmpty(clip)) 
                    { 
                        _animator.Play(clip); 
                        AppLogger.Info($"播放动画片段：{clip}"); 
                    } 
                    else 
                    { 
                        AppLogger.Info("模型未包含可用动画片段。"); 
                    } 
                } 
                else 
                { 
                    AppLogger.Info("未获取到动画控制器（模型可能不含动画）。"); 
                } 
            } 
            catch (Exception ex) 
            { 
                AppLogger.Error($"加载 3D 模型失败：{ex}"); 
            } 
        } 
 
        public void UpdateWeather(string city, string weather, string temperature) 
        { 
            TxtCity.Text = city; 
            TxtWeather.Text = weather; 
            TxtTemp.Text = temperature; 
        } 
 
        public void UpdateTime(string timeText) 
        { 
            TxtTime.Text = timeText; 
        } 
 
        /// <summary> 
        /// 旋转占位：DX11 版本不再使用 XAML 中的 CubeRotation。 
        /// 如需旋转模型，可给 node/SceneRoot 附加 Transform。 
        /// </summary> 
        public void UpdatePetRotation(double angle) 
        { 
            // 留空或在此自定义旋转逻辑 
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