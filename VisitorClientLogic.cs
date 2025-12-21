using System;
using System.Text.Json;

namespace WpfVideoPet
{
    /// <summary>
    /// 访客端和 WebView2 之间的通话逻辑，封装信令交互与状态回调。
    /// </summary>
    public sealed class VisitorClientLogic
    {
        private readonly AppConfig _config;

        public VisitorClientLogic(AppConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            if (_config.IsOperator)
            {
                throw new ArgumentException("访客端逻辑仅适用于 client 角色。", nameof(config));
            }
        }

        public bool HasActiveCall { get; private set; }

        public event EventHandler<string>? StatusTextChanged;

        public event EventHandler<string>? InformationMessageRequested;

        public event EventHandler<string>? AlertRaised;

        public event EventHandler? CloseRequested;

        public event EventHandler<bool>? CallStateChanged;

        public void ProcessSignalMessage(string? messageType, JsonElement root)
        {
            if (string.IsNullOrWhiteSpace(messageType))
            {
                return;
            }

            switch (messageType)
            {
                case "client-event":
                    HandleClientEvent(
                        root.TryGetProperty("event", out var eventElement) ? eventElement.GetString() : null,
                        root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null);
                    break;
                case "client-status":
                    var statusMessage = root.TryGetProperty("message", out var statusElement)
                        ? statusElement.GetString()
                        : null;
                    HandleClientStatus(statusMessage);
                    break;
                case "client-error":
                    var errorCode = root.TryGetProperty("code", out var codeElement)
                        ? codeElement.GetString()
                        : null;
                    var errorMessage = root.TryGetProperty("message", out var errorElement)
                        ? errorElement.GetString()
                        : null;
                    HandleClientEvent(errorCode ?? "client-error", errorMessage);
                    break;
                case "call-state":
                    var callState = root.TryGetProperty("state", out var callElement)
                        ? callElement.GetString()
                        : null;
                    UpdateCallState(callState);
                    break;
                case "alert":
                    var alertMessage = root.TryGetProperty("message", out var alertElement)
                        ? alertElement.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(alertMessage))
                    {
                        AlertRaised?.Invoke(this, alertMessage);
                    }
                    break;
            }
        }

        public string CreateJoinCommandPayload()
        {
            var payload = new
            {
                type = "join",//【修改】cmd->type，服务端只识别 type
                room = _config.Room,
                ws = _config.SignalServer,
                role = _config.Role,
                token = _config.OperatorToken
            };

            return JsonSerializer.Serialize(payload);
        }

        public string CreateSimpleCommandPayload(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new ArgumentException("命令不能为空。", nameof(command));
            }

            var payload = new { type = command };
            return JsonSerializer.Serialize(payload);
        }

        private void HandleClientEvent(string? evt, string? message)
        {
            if (string.IsNullOrWhiteSpace(evt) && string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            string display = !string.IsNullOrWhiteSpace(message)
                ? message!
                : evt switch
                {
                    "busy" => "坐席正在忙碌，请稍后再试。",
                    "no-operator" => "坐席当前离线。",
                    "rejected" => "坐席已拒绝本次通话请求。",
                    "ended" => "通话已结束。",
                    "ws-error" or "signal-error" => "无法连接信令服务器，请检查 SIGNAL_SERVER 配置或网络是否畅通。",
                    "join-timeout" or "join-failed" => "加入房间失败，请确认房间号和信令服务器状态。",
                    "room-not-found" => "房间不存在，请检查房间号设置。",
                    "unauthorized" => "房间鉴权失败，请确认访客访问凭证是否正确。",
                    _ => string.Empty
                };

            bool isConnectionIssue = evt is "ws-error"
                or "signal-error"
                or "join-timeout"
                or "join-failed"
                or "room-not-found"
                or "unauthorized"
                or "client-error"
                || (!string.IsNullOrWhiteSpace(evt) && evt.Contains("error", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(display))
            {
                StatusTextChanged?.Invoke(this, display);

                if (isConnectionIssue)
                {
                    AlertRaised?.Invoke(this, display);
                }
                else
                {
                    InformationMessageRequested?.Invoke(this, display);
                }
            }

            if (evt is "busy" or "no-operator" or "rejected" || isConnectionIssue)
            {
                SetActiveCall(false);
                var closeReason = !string.IsNullOrWhiteSpace(display) ? display : evt ?? "未知原因"; // 关闭原因
                RequestClose(closeReason);
            }
            else if (evt is "ended")
            {
                SetActiveCall(false);
            }
        }

        private void HandleClientStatus(string? statusMessage)
        {
            if (string.IsNullOrWhiteSpace(statusMessage))
            {
                return;
            }

            var trimmed = statusMessage.Trim();
            var normalized = trimmed.ToLowerInvariant();

            if (normalized is "ws-error"
                or "signal-error"
                or "join-timeout"
                or "join-failed"
                or "room-not-found"
                or "unauthorized")
            {
                HandleClientEvent(normalized, null);
                return;
            }

            StatusTextChanged?.Invoke(this, trimmed);

            if (LooksLikeConnectionIssue(trimmed))
            {
                AlertRaised?.Invoke(this, trimmed);
                SetActiveCall(false);
                var closeReason = $"连接异常: {trimmed}"; // 关闭原因
                RequestClose(closeReason);
            }
        }


        private static bool LooksLikeConnectionIssue(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            var normalized = message.Trim().ToLowerInvariant();

            return normalized.Contains("无法连接", StringComparison.Ordinal)
                || normalized.Contains("未能连接", StringComparison.Ordinal)
                || normalized.Contains("连接超时", StringComparison.Ordinal)
                || normalized.Contains("超时", StringComparison.Ordinal)
                || normalized.Contains("失败", StringComparison.Ordinal)
                || normalized.Contains("错误", StringComparison.Ordinal)
                || normalized.Contains("断开", StringComparison.Ordinal)
                || normalized.Contains("房间", StringComparison.Ordinal)
                || normalized.Contains("signal", StringComparison.Ordinal)
                || normalized.Contains("server", StringComparison.Ordinal)
                || normalized.Contains("error", StringComparison.Ordinal);
        }

        private void UpdateCallState(string? state)
        {
            var normalized = state?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            if (string.Equals(normalized, "active", StringComparison.OrdinalIgnoreCase))
            {
                SetActiveCall(true);
            }
            else if (string.Equals(normalized, "ended", StringComparison.OrdinalIgnoreCase))
            {
                SetActiveCall(false);
            }
        }

        private void SetActiveCall(bool isActive)
        {
            if (HasActiveCall == isActive)
            {
                return;
            }

            var wasActive = HasActiveCall;
            HasActiveCall = isActive;
            CallStateChanged?.Invoke(this, isActive);

            if (!isActive && wasActive)
            {
                var closeReason = "通话结束"; // 关闭原因
                RequestClose(closeReason);
            }
        }

        /// <summary>
        /// 记录窗口关闭原因并触发关闭请求，便于排查坐席未上线或信令异常等问题。
        /// </summary>
        /// <param name="reason">触发关闭的业务原因。</param>
        private void RequestClose(string reason)
        {
            AppLogger.Info($"访客端触发关闭请求，原因: {reason}");
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}