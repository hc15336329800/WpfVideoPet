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
                    if (!string.IsNullOrWhiteSpace(statusMessage))
                    {
                        StatusTextChanged?.Invoke(this, statusMessage);
                    }
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
                cmd = "join",
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

            var payload = new { cmd = command };
            return JsonSerializer.Serialize(payload);
        }

        private void HandleClientEvent(string? evt, string? message)
        {
            string display = message ?? evt switch
            {
                "busy" => "坐席正在忙碌，请稍后再试。",
                "no-operator" => "坐席当前离线。",
                "rejected" => "坐席已拒绝本次通话请求。",
                "ended" => "通话已结束。",
                _ => string.Empty
            };

            if (!string.IsNullOrWhiteSpace(display))
            {
                InformationMessageRequested?.Invoke(this, display);
            }

            if (evt is "busy" or "no-operator" or "rejected")
            {
                SetActiveCall(false);
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (evt is "ended")
            {
                SetActiveCall(false);
            }
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

            HasActiveCall = isActive;
            CallStateChanged?.Invoke(this, isActive);
        }
    }
}