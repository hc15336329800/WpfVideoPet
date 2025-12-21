using Microsoft.Web.WebView2.Core;
using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using WebView2Core = Microsoft.Web.WebView2.Core;


//新需求是：
//call.js 发消息给宿主 → 宿主弹出“非阻碍式、会消失的提醒”。
//这属于 WPF + WebView2 典型场景，不需要 Uno.UI。

namespace WpfVideoPet
{
    public partial class VideoCallWindow : Window
    {
        private readonly AppConfig _config;
        private bool _pageReady;
        private bool _hasActiveCall;
        private readonly VisitorClientLogic? _clientLogic;
        private bool _isPaused;
        private readonly DispatcherTimer? _ringTimeoutTimer;
        private bool _ringTimeoutStarted;
        private string? _lastNotificationSignature;
 

        public VideoCallWindow() : this(AppConfig.Load(null))
        {
        }

        public VideoCallWindow(string? roleOverride) : this(AppConfig.Load(roleOverride))
        {
        }

        private VideoCallWindow(AppConfig config)
        {
            _config = config;

            if (!_config.IsOperator)
            {
                _clientLogic = new VisitorClientLogic(_config);
                _ringTimeoutTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMinutes(1)
                };
                _ringTimeoutTimer.Tick += RingTimeoutTimer_Tick;
                _clientLogic.StatusTextChanged += (_, message) =>
                {
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        return;
                    }

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ClientStatusText.Text = message;
                        Title = $"视频客户端 - {message}";
                    }));
                };
                _clientLogic.InformationMessageRequested += (_, message) =>
                {
                    ShowClientNotification(message, MessageBoxImage.Information);
                };
                _clientLogic.AlertRaised += (_, message) =>
                {
                    ShowClientNotification(message, MessageBoxImage.Error);
                };
                _clientLogic.CloseRequested += (_, _) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StopRingTimeout();
                        Close();
                    }));
                };
                _clientLogic.CallStateChanged += (_, active) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (active)
                        {
                            StopRingTimeout();
                        }
                        _hasActiveCall = active;
                    }));
                };
            }

            InitializeComponent();

            Title = _config.IsOperator ? "视频坐席端" : "视频客户端";
            OperatorPanel.Visibility = _config.IsOperator ? Visibility.Visible : Visibility.Collapsed;
            OperatorStatus.Text = _config.IsOperator ? "初始化中..." : string.Empty;
            AcceptButton.IsEnabled = false;
            RejectButton.IsEnabled = false;
            HangupButton.IsEnabled = false;

            ClientStatusText.Text = _config.IsOperator ? "坐席模式" : "正在连接...";

            Loaded += VideoCallWindow_Loaded;
            Web.NavigationCompleted += Web_NavigationCompleted;
        }

        private async void VideoCallWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_config.TryValidate(out var validationError))
            {
                AutoCloseMessageBox.Show(
                    validationError ?? "SIGNAL_SERVER 配置无效，请检查后重试。",
                    "配置错误");
                Close();
                return;
            }

            try
            {
                await Web.EnsureCoreWebView2Async();
                Web.CoreWebView2.PermissionRequested += CoreWebView2_PermissionRequested;
                Web.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                //【新增 | 目的：确保页面注册好 message 监听后再发 join，避免首发丢失】
                Web.CoreWebView2.DOMContentLoaded += (_, __) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SendJoinCommand(); // 首发
                        _ = Task.Delay(500).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(SendJoinCommand))); // 兜底重发
                    }));
                };

                var localPageUri = TryGetLocalPageUri();
                Web.Source = localPageUri ?? new Uri(_config.PageUrl);
            }
            catch (Exception ex)
            {
                AutoCloseMessageBox.Show(
                    $"WebView2 初始化失败: {ex.Message}",
                    "错误");
                Close();
            }
        }
        

        private void Web_NavigationCompleted(object? sender, WebView2Core.CoreWebView2NavigationCompletedEventArgs e)
        {
            _pageReady = e.IsSuccess;
            if (e.IsSuccess)
            {
                ClientStatusText.Text = "页面加载完成，等待信令...";
                //SendJoinCommand();
            }
            else
            {
                var errorStatus = e.WebErrorStatus;
                var errorDetail = errorStatus != WebView2Core.CoreWebView2WebErrorStatus.Unknown
                    ? errorStatus.ToString()
                    : "未知错误";
                var message = $"页面加载失败：{errorDetail}";
                ClientStatusText.Text = message;
                AutoCloseMessageBox.Show(
                    message,
                    "页面加载失败",
                    this);
            }
        }

        private void CoreWebView2_WebMessageReceived(object? sender, WebView2Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var raw = e.TryGetWebMessageAsString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return;
                }

                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeElement))
                {
                    return;
                }

                var type = typeElement.GetString();
                if (string.IsNullOrWhiteSpace(type))
                {
                    return;
                }

                switch (type)
                {
                    case "operator-state":
                        if (_config.IsOperator)
                        {
                            var state = root.TryGetProperty("state", out var stateElement)
                                ? stateElement.GetString()
                                : null;
                            Dispatcher.Invoke(() => UpdateOperatorState(state));
                        }
                        break;
                    case "client-event":
                    case "client-status":
                        if (!_config.IsOperator)
                        {
                            _clientLogic?.ProcessSignalMessage(type, root);
                        }
                        break;
                    case "client-error": //【新增 | 目的：让错误信息走现有提示管道】
                        if (_config.IsOperator)
                        {
                            var errorMessage = root.TryGetProperty("message", out var errorElement)
                                ? errorElement.GetString()
                                : null;
                            if (!string.IsNullOrWhiteSpace(errorMessage))
                            {
                                ShowClientNotification(errorMessage, MessageBoxImage.Error);
                            }
                        }
                        else
                        {
                            _clientLogic?.ProcessSignalMessage(type, root);
                        }
                        break;

                    case "call-state":
                        if (_config.IsOperator)
                        {
                            var callState = root.TryGetProperty("state", out var callElement)
                                ? callElement.GetString()
                                : null;
                            if (string.Equals(callState, "active", StringComparison.OrdinalIgnoreCase))
                            {
                                _hasActiveCall = true;
                            }
                            else if (string.Equals(callState, "ended", StringComparison.OrdinalIgnoreCase))
                            {
                                _hasActiveCall = false;
                            }
                        }
                        else
                        {
                            _clientLogic?.ProcessSignalMessage(type, root);
                        }
                        break;
                    case "alert":
                        if (_config.IsOperator)
                        {
                            var alertMessage = root.TryGetProperty("message", out var alertElement)
                                ? alertElement.GetString()
                                : null;
                            if (!string.IsNullOrWhiteSpace(alertMessage))
                            {
                                Dispatcher.Invoke(() => ShowClientNotification(alertMessage, MessageBoxImage.Information));
                            }
                        }
                        else
                        {
                            _clientLogic?.ProcessSignalMessage(type, root);
                        }
                        break;
                }
            }
            catch (JsonException)
            {
            }
        }

        private void UpdateOperatorState(string? state)
        {
            if (!_config.IsOperator)
            {
                return;
            }

            state = state?.Trim().ToLowerInvariant();
            string statusText = state switch
            {
                "ringing" => "有新的来访请求",
                "connecting" => "正在建立连接...",
                "in-call" => "通话中",
                "ended" => "通话已结束",
                "offline" => "信令已断开",
                _ => "空闲等待呼入"
            };

            OperatorStatus.Text = statusText;

            switch (state)
            {
                case "ringing":
                    AcceptButton.IsEnabled = true;
                    RejectButton.IsEnabled = true;
                    HangupButton.IsEnabled = false;
                    break;
                case "connecting":
                    AcceptButton.IsEnabled = false;
                    RejectButton.IsEnabled = true;
                    HangupButton.IsEnabled = false;
                    break;
                case "in-call":
                    AcceptButton.IsEnabled = false;
                    RejectButton.IsEnabled = false;
                    HangupButton.IsEnabled = true;
                    _hasActiveCall = true;
                    break;
                case "ended":
                case "offline":
                    AcceptButton.IsEnabled = false;
                    RejectButton.IsEnabled = false;
                    HangupButton.IsEnabled = false;
                    _hasActiveCall = false;
                    break;
                default:
                    AcceptButton.IsEnabled = false;
                    RejectButton.IsEnabled = false;
                    HangupButton.IsEnabled = false;
                    _hasActiveCall = false;
                    break;
            }
        }

        private void ShowClientNotification(string? message, MessageBoxImage image)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var signature = $"{image}:{message}";
            var showDialog = !string.Equals(_lastNotificationSignature, signature, StringComparison.Ordinal);
            _lastNotificationSignature = signature;

            void Show()
            {
                ClientStatusText.Text = message;
                Title = _config.IsOperator
                    ? $"视频坐席端 - {message}"
                    : $"视频客户端 - {message}";
                if (!showDialog)
                {
                    return;
                }

                var caption = image == MessageBoxImage.Error ? "错误" : "提示";
                AutoCloseMessageBox.Show(message, caption, this);
            }
            if (Dispatcher.CheckAccess())
            {
                Show();
            }
            else
            {
                Dispatcher.Invoke(Show);
            }
        }
  
  
        private void SendJoinCommand()
        {

            //【新增 | 目的：HTTPS 页面禁止信令用 ws://】
            try
            {
                var pageUri = Web?.Source ?? new Uri(_config.PageUrl);
                var sigScheme = new Uri(_config.SignalServer).Scheme;
                if (string.Equals(pageUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(sigScheme, "ws", StringComparison.OrdinalIgnoreCase))
                {
                    ShowClientNotification("当前为 HTTPS 页面，但信令为 ws://，浏览器会拦截。请改用 wss:// 并确保证书主机名匹配。", MessageBoxImage.Error);
                    return;
                }
            }
             catch { /* 忽略解析异常 */ }


            if (Web?.CoreWebView2 == null)
            {
                return;
            }

            var payloadJson = _clientLogic?.CreateJoinCommandPayload()
                 ?? JsonSerializer.Serialize(new
                 {
                     type = "join",
                     room = _config.Room,
                     ws = _config.SignalServer,
                     role = _config.Role,
                     token = _config.IsOperator ? _config.OperatorToken : null //【修改】仅坐席带 token
                 });

            StartRingTimeout();
            Web.CoreWebView2.PostWebMessageAsJson(payloadJson);
        }

        private void SendSimpleCommand(string command)
        {
            if (Web?.CoreWebView2 == null)
            {
                return;
            }

            var payloadJson = _clientLogic?.CreateSimpleCommandPayload(command)
                ?? JsonSerializer.Serialize(new { type = command });
            Web.CoreWebView2.PostWebMessageAsJson(payloadJson);
        }

        private void Accept_Click(object sender, RoutedEventArgs e)
        {
            SendSimpleCommand("accept");
        }

        private void Reject_Click(object sender, RoutedEventArgs e)
        {
            SendSimpleCommand("reject");
        }

        private void Hangup_Click(object sender, RoutedEventArgs e)
        {
            SendSimpleCommand("hangup");
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            _isPaused = !_isPaused;
            PauseButton.Content = _isPaused ? "继续" : "暂停";
            SendSimpleCommand(_isPaused ? "pause" : "resume");
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            try
            {
                StopRingTimeout();
                if (_pageReady && _hasActiveCall)
                {
                    SendSimpleCommand("hangup");
                }
            }
            catch
            {
            }

            try
            {
                if (Web?.CoreWebView2 != null)
                {
                    Web.CoreWebView2.PermissionRequested -= CoreWebView2_PermissionRequested;
                }
            }
            catch
            {
            }

            base.OnClosing(e);
        }

        private void CoreWebView2_PermissionRequested(object? sender, WebView2Core.CoreWebView2PermissionRequestedEventArgs args)
        {
            if (args == null)
            {
                return;
            }

            if (args.PermissionKind == WebView2Core.CoreWebView2PermissionKind.Microphone ||
       args.PermissionKind == WebView2Core.CoreWebView2PermissionKind.Camera ||
       IsScreenCapturePermission(args.PermissionKind))
            {
                args.State = WebView2Core.CoreWebView2PermissionState.Allow;
            }
        }


        private static Uri? TryGetLocalPageUri()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var localPath = Path.Combine(baseDir, "Assets", "client2.html");
                if (File.Exists(localPath))
                {
                    return new Uri(localPath);
                }
            }
            catch
            {
            }
            return null;
        }

        private static bool IsScreenCapturePermission(WebView2Core.CoreWebView2PermissionKind permissionKind)
        {
            // Screen capture permission was introduced after some WebView2 releases.
            // Compare using the enum name so the code continues to compile with older versions.
            return string.Equals(permissionKind.ToString(), "ScreenCapture", StringComparison.Ordinal);
        }

        private void StartRingTimeout()
        {
            if (_config.IsOperator || _ringTimeoutTimer == null || _ringTimeoutStarted)
            {
                return;
            }

            _ringTimeoutStarted = true;
            _ringTimeoutTimer.Stop();
            _ringTimeoutTimer.Start();
        }

        private void StopRingTimeout()
        {
            if (_ringTimeoutTimer == null)
            {
                return;
            }

            _ringTimeoutTimer.Stop();
            _ringTimeoutStarted = false;
        }

        private void RingTimeoutTimer_Tick(object? sender, EventArgs e)
        {
            if (_ringTimeoutTimer == null)
            {
                return;
            }

            _ringTimeoutTimer.Stop();
            _ringTimeoutStarted = false;

            if (_config.IsOperator || _hasActiveCall)
            {
                return;
            }

            try
            {
                SendSimpleCommand("hangup");
            }
            catch
            {
            }

            ShowClientNotification("坐席暂未接听，本次呼叫已结束。", MessageBoxImage.Information);
            Close();
        }
    }
}