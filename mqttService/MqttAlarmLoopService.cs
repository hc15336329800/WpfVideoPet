using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WpfVideoPet.mqtt;

namespace WpfVideoPet.service
{
    /// <summary>
    /// 监听 MQTT 下发的报警播放指令，解析时长并通知上层执行循环播放。
    /// </summary>
    public sealed class MqttAlarmLoopService : IAsyncDisposable
    {
        private readonly MqttCoreService _bridge; // MQTT 桥接实例
        private readonly string _topic; // 报警订阅主题
        private readonly EventHandler<MqttCoreService.MqttBridgeMessage> _messageHandler; // 消息回调
        private bool _started; // 是否已经启动
        private bool _disposed; // 是否已释放

        /// <summary>
        /// 当成功解析到报警播放请求时触发，携带目标时长。
        /// </summary>
        public event EventHandler<AlarmPlaybackRequest>? AlarmRequested;

        public MqttAlarmLoopService(MqttCoreService bridge, string topic)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _topic = string.IsNullOrWhiteSpace(topic)
                ? throw new ArgumentException("报警订阅主题不能为空。", nameof(topic))
                : topic.Trim();
            _messageHandler = OnBridgeMessageReceived;
        }

        /// <summary>
        /// 启动订阅并挂载消息处理回调。
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MqttAlarmLoopService));
            }

            if (_started)
            {
                return;
            }

            await _bridge.SubscribeAdditionalTopicAsync(_topic, cancellationToken).ConfigureAwait(false);
            _bridge.MessageReceived += _messageHandler;
            _started = true;
            AppLogger.Info($"报警播放服务已启动，订阅主题: {_topic}。");
        }

        /// <summary>
        /// 释放资源并解除事件订阅。
        /// </summary>
        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            if (_started)
            {
                _bridge.MessageReceived -= _messageHandler;
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// 处理 MQTT 消息并尝试解析播放时长。
        /// </summary>
        private void OnBridgeMessageReceived(object? sender, MqttCoreService.MqttBridgeMessage message)
        {
            if (_disposed)
            {
                return;
            }

            if (!string.Equals(message.Topic, _topic, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var textPayload = Encoding.UTF8.GetString(message.Payload.Span).Trim();
            if (string.IsNullOrWhiteSpace(textPayload))
            {
                AppLogger.Warn($"收到空白报警指令，Topic: {_topic}。");
                return;
            }

            if (!TryParseDuration(textPayload, out var duration))
            {
                AppLogger.Warn($"报警指令解析失败，Topic: {_topic}, Payload: {textPayload}");
                return;
            }

            AppLogger.Info($"解析到报警播放指令，目标时长: {duration}。");
            AlarmRequested?.Invoke(this, new AlarmPlaybackRequest(duration, textPayload));
        }

        /// <summary>
        /// 支持纯数字秒数或简单 JSON 对象的时长解析。
        /// </summary>
        private static bool TryParseDuration(string payload, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;

            if (int.TryParse(payload, out var seconds))
            {
                duration = TimeSpan.FromSeconds(Math.Max(seconds, 1));
                return true;
            }

            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Number && root.TryGetInt32(out var numSeconds))
                {
                    duration = TimeSpan.FromSeconds(Math.Max(numSeconds, 1));
                    return true;
                }

                if (root.ValueKind == JsonValueKind.Object)
                {
                    int? candidateSeconds = null;

                    if (root.TryGetProperty("duration", out var durationElement))
                    {
                        candidateSeconds = TryReadSeconds(durationElement);
                    }
                    if (!candidateSeconds.HasValue && root.TryGetProperty("durationSeconds", out var durationSecondsElement))
                    {
                        candidateSeconds = TryReadSeconds(durationSecondsElement);
                    }
                    if (!candidateSeconds.HasValue && root.TryGetProperty("playSeconds", out var playSecondsElement))
                    {
                        candidateSeconds = TryReadSeconds(playSecondsElement);
                    }

                    if (!candidateSeconds.HasValue && root.TryGetProperty("endTime", out var endTimeElement))
                    {
                        if (DateTimeOffset.TryParse(endTimeElement.GetString(), out var endTime))
                        {
                            var remaining = endTime - DateTimeOffset.Now;
                            if (remaining > TimeSpan.Zero)
                            {
                                candidateSeconds = (int)Math.Ceiling(remaining.TotalSeconds);
                            }
                        }
                    }

                    if (candidateSeconds.HasValue)
                    {
                        duration = TimeSpan.FromSeconds(Math.Max(candidateSeconds.Value, 1));
                        return true;
                    }
                }
            }
            catch (JsonException ex)
            {
                AppLogger.Warn("报警指令载荷 JSON 解析失败: " + ex);
            }

            return false;
        }

        /// <summary>
        /// 从 JSON 元素提取秒数，兼容数字与字符串数字。
        /// </summary>
        private static int? TryReadSeconds(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
            {
                return number;
            }

            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var textNumber))
            {
                return textNumber;
            }

            return null;
        }
    }

    /// <summary>
    /// 封装报警播放请求的参数，便于上层使用。
    /// </summary>
    public sealed record AlarmPlaybackRequest(TimeSpan Duration, string RawPayload);
}