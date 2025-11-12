﻿using System;
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
        private readonly bool _usingFallbackTopic;                      // 是否启用默认主题
        private readonly TaskCompletionSource<bool> _selfTestCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously); // 订阅自检结果
        private readonly string _selfTestMarker = $"PLC-TEST-PING::{Guid.NewGuid():N}"; // 自检消息标记

        private bool _started;  // 是否已启动
        private bool _disposed; // 是否已释放
        private bool _topicSubscribed; // 是否已完成主题订阅

        // [常量] 默认 PLC 订阅主题
        private const string PlcSubTopic = "lanmao001/plc/sub";

        /// <summary>
        /// 使用默认 PLC 订阅主题初始化服务。
        /// </summary>
        /// <param name="bridge">复用的 MQTT 桥接实例。</param>
        public PlcSubTestService(MqttCoreService bridge) : this(bridge, PlcSubTopic, false) { }

        /// <summary>
        /// 使用自定义订阅主题初始化服务。
        /// </summary>
        /// <param name="bridge">复用的 MQTT 桥接实例。</param>
        /// <param name="topic">要订阅的主题。</param>
        public PlcSubTestService(MqttCoreService bridge, string topic) : this(bridge, NormalizeTopic(topic), false) { }

        /// <summary>
        /// 使用应用配置中的 PLC 控制主题初始化服务，自动回落到默认主题。
        /// </summary>
        /// <param name="config">包含 PLC 主题配置的应用配置。</param>
        /// <param name="bridge">复用的 MQTT 桥接实例。</param>
        public PlcSubTestService(AppConfig config, MqttCoreService bridge)
            : this(
                bridge,
                ResolveConfiguredTopic(config ?? throw new ArgumentNullException(nameof(config)), out var usingFallback),
                usingFallback)
        {
        }

        /// <summary>
        /// 统一的内部构造函数，完成字段初始化与回调绑定。
        /// </summary>
        /// <param name="bridge">复用的 MQTT 桥接实例。</param>
        /// <param name="topic">最终确定的订阅主题。</param>
        /// <param name="usingFallback">指示是否使用默认主题。</param>
        private PlcSubTestService(MqttCoreService bridge, string topic, bool usingFallback)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _topic = string.IsNullOrWhiteSpace(topic)
                ? throw new ArgumentNullException(nameof(topic))
                : topic;
            _usingFallbackTopic = usingFallback;
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

            if (string.Equals(payloadText, _selfTestMarker, StringComparison.Ordinal))
            {
                if (!_selfTestCompletion.Task.IsCompleted)
                {
                    AppLogger.Info("[PLC-TEST] 订阅自检消息已收到，Broker 已成功回环测试载荷。");
                    _selfTestCompletion.TrySetResult(true);
                }
                return;
            }
            AppLogger.Info($"[PLC-TEST] 收到消息 -> Topic: {message.Topic}, Payload: {payloadText}");
        }

        /// <summary>
        /// 启动测试订阅服务：确保 MQTT 连接就绪，完成主题订阅，并挂载消息回调。
        /// </summary>
        /// <param name="cancellationToken">外部取消令牌。</param>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PlcSubTestService));
            if (_started) return;

            await _bridge.StartAsync(cancellationToken).ConfigureAwait(false);
            var handlerAttached = false; // 是否已挂载回调

            try
            {
                await SubscribePlcTopicAsync(cancellationToken).ConfigureAwait(false);
                _bridge.MessageReceived += _messageHandler;
                handlerAttached = true;
                await PublishSelfTestAsync(cancellationToken).ConfigureAwait(false);
                await AwaitSelfTestAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _bridge.MessageReceived -= _messageHandler;
                if (handlerAttached)
                {
                    _bridge.MessageReceived -= _messageHandler;
                }
                throw;
            }
            _started = true;

            if (_usingFallbackTopic)
            {
                AppLogger.Warn($"[PLC-TEST] 未在配置中指定控制主题，已回退到默认值：{_topic}");
            }
            else
            {
                AppLogger.Info($"[PLC-TEST] 使用配置控制主题：{_topic}");
            }

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
            _topicSubscribed = false;

            AppLogger.Info("[PLC-TEST] 已停止并释放资源。");
            _selfTestCompletion.TrySetCanceled();
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// 解析配置中的 PLC 控制主题，空值时回落到默认主题。
        /// </summary>
        /// <param name="config">应用配置实例。</param>
        /// <param name="usingFallback">输出指示：true 表示使用默认主题。</param>
        /// <returns>最终确定的订阅主题。</returns>
        private static string ResolveConfiguredTopic(AppConfig config, out bool usingFallback)
        {
            if (config.Plc != null)
            {
                var candidate = config.Plc.ControlSubscribeTopic;
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    usingFallback = false;
                    return NormalizeTopic(candidate);
                }
            }

            usingFallback = true;
            return PlcSubTopic;
        }

        /// <summary>
        /// 规范化主题字符串，移除首尾空白。
        /// </summary>
        /// <param name="topic">原始主题值。</param>
        /// <returns>移除空白后的主题。</returns>
        private static string NormalizeTopic(string topic)
        {
            return topic?.Trim() ?? string.Empty;
        }

        /// <summary>
        /// 调用统一的桥接接口完成 PLC 主题订阅，并在主题为空时输出警告。
        /// </summary>
        /// <param name="cancellationToken">外部取消令牌。</param>
        private async Task SubscribePlcTopicAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_topic))
            {
                AppLogger.Warn("[PLC-TEST] 控制主题为空，已跳过订阅。");
                return;
            }
            if (_topicSubscribed)
            {
                AppLogger.Info($"[PLC-TEST] 控制主题已订阅，无需重复处理：{_topic}");
                return;
            }

            await _bridge.SubscribeAdditionalTopicAsync(_topic, cancellationToken).ConfigureAwait(false);
            _topicSubscribed = true;
            AppLogger.Info($"[PLC-TEST] 已向 MQTT 注册附加订阅，主题：{_topic}");
        }

        /// <summary>
        /// 在订阅完成后向自身发布一条带有唯一标记的测试消息，用于验证回环链路是否畅通。
        /// </summary>
        /// <param name="cancellationToken">外部取消令牌。</param>
        private async Task PublishSelfTestAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_topic))
            {
                return;
            }

            AppLogger.Info($"[PLC-TEST] 正在发送订阅自检消息，标记: {_selfTestMarker}");
            await _bridge.SendStringAsync(_selfTestMarker, topic: _topic, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 等待自检消息回环结果，在限定时间内未收到则输出警告，帮助定位订阅时序问题。
        /// </summary>
        /// <param name="cancellationToken">外部取消令牌。</param>
        private async Task AwaitSelfTestAsync(CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                await _selfTestCompletion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                AppLogger.Warn("[PLC-TEST] 自检消息在 5 秒内未回环，可能仍在等待 Broker 完成订阅确认，请稍后再试或检查主题是否正确。");
            }
        }
    }
}