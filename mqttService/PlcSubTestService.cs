using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WpfVideoPet.mqtt;
using static WpfVideoPet.mqtt.MqttCoreService;

namespace WpfVideoPet.service
{
    /// <summary>
    /// 精简版 MQTT 测试订阅服务：
    /// - 复用外部 MqttCoreService 实例
    /// - 默认监听主题：lanmao001/plc/sub
    /// - 收到消息后输出到日志
    /// </summary>
    public sealed class PlcSubTestService : IAsyncDisposable
    {
        private readonly MqttCoreService _bridge;                       // MQTT 核心桥接
        private readonly EventHandler<MqttBridgeMessage> _messageHandler; // 消息回调
        private readonly string _topic;                                 // 当前订阅主题

        private bool _started;  // 是否已启动
        private bool _disposed; // 是否已释放


        private readonly AppConfig _config;


        // [常量] 默认 PLC 订阅主题
        private const string PlcSubTopic = "lanmao001/plc/sub";

        /// <summary>
        /// 使用默认 PLC 订阅主题初始化服务。
        /// </summary>
        /// <param name="bridge">复用的 MQTT 桥接实例。</param>
        public PlcSubTestService(MqttCoreService bridge) : this(bridge, PlcSubTopic) { }


        public PlcSubTestService(MqttCoreService bridge, string topic)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            if (string.IsNullOrWhiteSpace(topic)) throw new ArgumentNullException(nameof(topic));
            _topic = topic;
            _messageHandler = OnBridgeMessageReceived;
        }



        /// <summary>
        /// 使用指定订阅主题初始化服务。
        /// </summary>
        /// <param name="bridge">复用的 MQTT 桥接实例。</param>
        /// <param name="topic">要订阅的主题。</param>

        public PlcSubTestService(AppConfig config, MqttCoreService bridge)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _topic = PlcSubTopic;
            _messageHandler = OnBridgeMessageReceived;
        }

        /// <summary>
        /// 当 MQTT 桥接收到消息时触发，只处理当前订阅主题并输出日志。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="message">MQTT 桥接推送的消息。</param>
        private void OnBridgeMessageReceived(object? sender, MqttBridgeMessage message)
        {
            if (_disposed) return;
            if (!string.Equals(message.Topic, _topic, StringComparison.OrdinalIgnoreCase)) return; // 只关注指定主题

            var payloadText = Encoding.UTF8.GetString(message.Payload.Span);
            AppLogger.Info($"[PLC-TEST] 收到消息 -> Topic: {message.Topic}, Payload: {payloadText}");
        }

        /// <summary>
        /// 启动测试订阅服务：启动 MQTT 桥接并挂载消息回调。
        /// </summary>
        /// <param name="cancellationToken">外部取消令牌。</param>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PlcSubTestService));
            if (_started) return;

            await _bridge.StartAsync(cancellationToken).ConfigureAwait(false);
            _bridge.MessageReceived += _messageHandler;
            _started = true;

            AppLogger.Info($"[PLC-TEST] 已启动，开始监听主题：{_topic}");
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

            AppLogger.Info("[PLC-TEST] 已停止并释放资源。");
            return ValueTask.CompletedTask;
        }
    }
}
