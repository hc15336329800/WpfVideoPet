using Microsoft.Web.WebView2.Core;
using System;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;

namespace WpfVideoPet
{
    public partial class VideoCallWindow : Window
    {
        private readonly AppConfig _config;
        private bool _pageReady;
        private bool _hasActiveCall;
        private readonly VisitorClientLogic? _clientLogic;
        private bool _isPaused;

        public VideoCallWindow(string? roleOverride = null)
        {
            _config = AppConfig.Load(roleOverride);

            if (!_config.IsOperator)
            {
                _clientLogic = new VisitorClientLogic(_config);
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
                    Dispatcher.BeginInvoke(new Action(Close));
                };
                _clientLogic.CallStateChanged += (_, active) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
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
                MessageBox.Show(this,
                    validationError ?? "SIGNAL_SERVER 配置无效，请检查后重试。",
                    "配置错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Close();
                return;
            }

            try
            {
                await Web.EnsureCoreWebView2Async();
                Web.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                Web.Source = new Uri(_config.PageUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"WebView2 初始化失败: {ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Close();
            }
        }

        private void Web_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _pageReady = e.IsSuccess;
            if (e.IsSuccess)
            {
                ClientStatusText.Text = "页面加载完成，等待信令...";
                SendJoinCommand();
            }
            else
            {
                ClientStatusText.Text = "页面加载失败";
            }
        }

        private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
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
                                Dispatcher.Invoke(() =>
                                {
                                    MessageBox.Show(this,
                                        alertMessage,
                                        "提示",
                                        MessageBoxButton.OK,
                                        MessageBoxImage.Information);
                                });
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

            void Show()
            {
                ClientStatusText.Text = message;
                Title = $"视频客户端 - {message}";
                var caption = image == MessageBoxImage.Error ? "错误" : "提示";
                MessageBox.Show(this,
                    message,
                    caption,
                    MessageBoxButton.OK,
                    image);
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
            if (Web?.CoreWebView2 == null)
            {
                return;
            }

            var payloadJson = _clientLogic?.CreateJoinCommandPayload()
                ?? JsonSerializer.Serialize(new
                {
                    cmd = "join",
                    room = _config.Room,
                    ws = _config.SignalServer,
                    role = _config.Role,
                    token = _config.OperatorToken
                });

            Web.CoreWebView2.PostWebMessageAsJson(payloadJson);
        }

        private void SendSimpleCommand(string command)
        {
            if (Web?.CoreWebView2 == null)
            {
                return;
            }

            var payloadJson = _clientLogic?.CreateSimpleCommandPayload(command)
                ?? JsonSerializer.Serialize(new { cmd = command });
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
                if (_pageReady && _hasActiveCall)
                {
                    SendSimpleCommand("hangup");
                }
            }
            catch
            {
            }

            base.OnClosing(e);
        }
    }
}