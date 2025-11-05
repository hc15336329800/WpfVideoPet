using System;
using System.Threading;
using System.Threading.Tasks;
using WpfVideoPet.mqtt;
using static WpfVideoPet.mqtt.FixedLengthMqttBridge;

namespace WpfVideoPet.service
{
    /// <summary>
    /// 简化后的 MQTT 任务服务，统一复用 FixedLengthMqttBridge 处理 16 字节消息。
    /// </summary>
    public sealed class RemoteMediaService : IAsyncDisposable
    {
        private readonly FixedLengthMqttBridge _bridge; // 16 字节 MQTT 桥
        private readonly EventHandler<FixedLengthMqttMessage> _messageHandler; // 消息回调
        private bool _started; // 是否已启动
        private bool _disposed; // 释放状态

        /// <summary>
        /// 以共享的 MQTT 桥接实例初始化任务服务。
        /// </summary>
        /// <param name="bridge">用于维持连接的 MQTT 桥接实例。</param>
        public RemoteMediaService(FixedLengthMqttBridge bridge)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _messageHandler = OnBridgeMessageReceived;
        }

        /// <summary>
        /// 当收到 MQTT 16 字节消息时触发的事件。
        /// </summary>
        public event EventHandler<FixedLengthMqttMessage>? PayloadReceived;

        /// <summary>
        /// 启动任务服务，确保桥接已连接并挂载消息回调。
        /// </summary>
        /// <param name="cancellationToken">外部取消令牌。</param>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RemoteMediaService));
            }

            if (_started)
            {
                return;
            }

            await _bridge.StartAsync(cancellationToken).ConfigureAwait(false);
            _bridge.MessageReceived += _messageHandler;
            _started = true;
        }

        /// <summary>
        /// 通过共享桥接发送 16 字节原始数据。
        /// </summary>
        /// <param name="payload">定长 16 字节数据。</param>
        /// <param name="topic">可选的目标主题。</param>
        /// <param name="retain">是否保留消息。</param>
        /// <param name="cancellationToken">外部取消令牌。</param>
        public Task SendAsync(ReadOnlyMemory<byte> payload, string? topic = null, bool retain = false, CancellationToken cancellationToken = default)
        {
            return _bridge.SendAsync(payload, topic, retain, cancellationToken);
        }

        /// <summary>
        /// 将字符串转换为 16 字节后发送，自动补齐或截断。
        /// </summary>
        /// <param name="text">待发送的字符串。</param>
        /// <param name="topic">可选的目标主题。</param>
        /// <param name="retain">是否保留消息。</param>
        /// <param name="cancellationToken">外部取消令牌。</param>
        public Task SendStringAsync(string text, string? topic = null, bool retain = false, CancellationToken cancellationToken = default)
        {
            return _bridge.SendStringAsync(text, null, 0x00, topic, retain, cancellationToken);
        }

        /// <summary>
        /// 释放服务资源并解除事件订阅。
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
                _started = false;
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// 处理桥接推送的 MQTT 消息，向外层转发。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="message">定长消息内容。</param>
        private void OnBridgeMessageReceived(object? sender, FixedLengthMqttMessage message)
        {
            if (_disposed)
            {
                return;
            }

            PayloadReceived?.Invoke(this, message);
        }
    }
}