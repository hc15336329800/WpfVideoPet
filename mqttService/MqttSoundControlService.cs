using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WpfVideoPet.mqtt;

namespace WpfVideoPet.service
{
    /// <summary>
    /// 监听 MQTT 下发的音量控制指令，解析后通过事件上抛给上层 UI。
    /// </summary>
    public sealed class MqttSoundControlService : IAsyncDisposable
    {
        private readonly MqttCoreService _bridge; // MQTT 桥接实例
        private readonly string _topic; // 订阅的音量控制主题
        private readonly EventHandler<MqttCoreService.MqttBridgeMessage> _messageHandler; // 消息回调
        private bool _started; // 是否已经启动
        private bool _disposed; // 是否已释放

        /// <summary>
        /// 当成功解析到音量数值时触发，值范围为 0~100。
        /// </summary>
        public event EventHandler<int>? VolumeChanged;

        /// <summary>
        /// 构造音量控制服务。
        /// </summary>
        /// <param name="bridge">MQTT 核心桥接。</param>
        /// <param name="topic">需要订阅的音量控制主题。</param>
        public MqttSoundControlService(MqttCoreService bridge, string topic)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _topic = string.IsNullOrWhiteSpace(topic)
                ? throw new ArgumentException("音量控制主题不能为空。", nameof(topic))
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
                throw new ObjectDisposedException(nameof(MqttSoundControlService));
            }

            if (_started)
            {
                return;
            }

            await _bridge.SubscribeAdditionalTopicAsync(_topic, cancellationToken).ConfigureAwait(false);
            _bridge.MessageReceived += _messageHandler;
            _started = true;
            AppLogger.Info($"音量控制服务已启动，订阅主题: {_topic}。");
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
        /// 处理 MQTT 消息并尝试解析音量数值。
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
                AppLogger.Warn($"收到空白音量控制指令，Topic: {_topic}。");
                return;
            }

            if (!TryParseVolume(textPayload, out var volume))
            {
                AppLogger.Warn($"音量控制指令解析失败，Topic: {_topic}, Payload: {textPayload}");
                return;
            }

            AppLogger.Info($"解析到音量控制指令，目标音量: {volume}。");
            VolumeChanged?.Invoke(this, volume);
        }

        /// <summary>
        /// 尝试从 MQTT 载荷中解析音量数值，兼容纯数字与简单 JSON 对象。
        /// </summary>
        /// <param name="payload">文本载荷。</param>
        /// <param name="volume">解析出的音量。</param>
        /// <returns>解析成功返回 true。</returns>
        private static bool TryParseVolume(string payload, out int volume)
        {
            volume = 0;

            if (int.TryParse(payload, out var directValue))
            {
                volume = Math.Clamp(directValue, 0, 100);
                return true;
            }

            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                int? parsed = null;

                if (root.ValueKind == JsonValueKind.Number && root.TryGetInt32(out var num))
                {
                    parsed = num;
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("volume", out var volumeElement))
                    {
                        if (volumeElement.ValueKind == JsonValueKind.Number && volumeElement.TryGetInt32(out var jsonVolume))
                        {
                            parsed = jsonVolume;
                        }
                        else if (volumeElement.ValueKind == JsonValueKind.String && int.TryParse(volumeElement.GetString(), out var strVolume))
                        {
                            parsed = strVolume;
                        }
                    }
                }

                if (parsed.HasValue)
                {
                    volume = Math.Clamp(parsed.Value, 0, 100);
                    return true;
                }
            }
            catch (JsonException ex)
            {
                AppLogger.Warn("音量控制载荷 JSON 解析失败："+ ex);
            }

            return false;
        }
    }
}