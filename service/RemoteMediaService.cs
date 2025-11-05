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
        public event EventHandler<FixedLengthMqttMessage>? PayloadReceived;//当收到 MQTT 16 字节消息时触发的事件。

        private bool _started; // 是否已启动
        private bool _disposed; // 释放状态

        // [常量] 监听与发送的固定主题
        private const string ListenTopic117 = "117"; // 收到该主题时在控制台输出“117”
        private const string SendTopic118 = "118"; // 向该主题发送 "helloworld"

 
        //*************************************初始化*********************************************

        /// <summary>
        /// 初始化： 不自己创建或管理 MQTT 连接，而是复用一个已经存在的 FixedLengthMqttBridge 实例来收发消息。
        /// </summary>
        /// <param name="bridge">用于维持连接的 MQTT 桥接实例。</param>
        public RemoteMediaService(FixedLengthMqttBridge bridge)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _messageHandler = OnBridgeMessageReceived;
        }

        //*************************************接收消息*********************************************

        // [回调逻辑] 在收到桥接消息时，若 topic == "117"，控制台输出“117”
        private void OnBridgeMessageReceived(object? sender, FixedLengthMqttMessage message)
        {
            if (_disposed) return;

            // [新增] 仅当主题为 117 时输出
            if (message.Topic == ListenTopic117)
            {
                Console.WriteLine("117"); // 控制台输出
            }

            PayloadReceived?.Invoke(this, message);
        }
         

        //*************************************发送消息*********************************************

        /// <summary>
        /// 向主题 118 发送 "helloworld"（UTF-8，自动截断/补齐至16字节）。
        /// </summary>
        public Task SendHelloWorldTo118Async(CancellationToken cancellationToken = default)
        {
            return SendStringAsync("helloworld", topic: SendTopic118, retain: false, cancellationToken);
        }


        //*************************************封装发送方法*********************************************


        /// <summary>
        /// 【向主题发送】发送 16 字节原始数据。（原始16字节）
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
        /// 【向主题发送】将字符串转换为 16 字节后发送，自动补齐或截断。（任意长度）
        /// </summary>
        /// <param name="text">待发送的字符串。</param>
        /// <param name="topic">可选的目标主题。</param>
        /// <param name="retain">是否保留消息。</param>
        /// <param name="cancellationToken">外部取消令牌。</param>
        public Task SendStringAsync(string text, string? topic = null, bool retain = false, CancellationToken cancellationToken = default)
        {
            return _bridge.SendStringAsync(text, null, 0x00, topic, retain, cancellationToken);
        }


        //**********************************************************************************


        /// <summary>
        /// 启动任务服务，确保桥接已连接并挂载消息回调。
        /// </summary>
        /// <param name="cancellationToken">外部取消令牌。</param>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RemoteMediaService));
            if (_started) return;

            await _bridge.StartAsync(cancellationToken).ConfigureAwait(false);
            _bridge.MessageReceived += _messageHandler;
            _started = true;
        }

 
        /// <summary>
        /// 释放服务资源并解除事件订阅。
        /// </summary>
        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;

            _disposed = true;
            if (_started)
            {
                _bridge.MessageReceived -= _messageHandler;
                _started = false;
            }
            return ValueTask.CompletedTask;
        }
    }
}
