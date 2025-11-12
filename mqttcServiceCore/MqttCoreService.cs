using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace WpfVideoPet.mqtt
{
    /// <summary>
    /// 提供精简的 MQTT 通信封装，负责连接、收发与日志记录，不再对载荷长度做任何限制。
    /// 主要职责包括：建立连接、订阅主题、维护自动重连以及向上层转发完整的原始消息。
    /// </summary>
    public sealed class MqttCoreService : IAsyncDisposable
    {
        private readonly MqttConfig _config; // MQTT 配置
        private readonly MqttFactory _factory = new(); // MQTT 工厂
        private readonly SemaphoreSlim _connectLock = new(1, 1); // 连接互斥锁
        private readonly object _subscriptionLock = new(); // 订阅集合锁
        private readonly HashSet<string> _additionalTopics = new(StringComparer.OrdinalIgnoreCase); // 额外订阅主题集
        private IMqttClient? _client; // MQTT 客户端实例
        private bool _disposed; // 释放标记

        /// <summary>
        /// 初始化 MQTT 桥接服务并记录配置引用，供后续连接与发送使用。
        /// </summary>
        /// <param name="config">MQTT 通信配置，需包含服务器地址与 Topic 信息。</param>
        public MqttCoreService(MqttConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config)); // 配置引用
        }

        /// <summary>
        /// 开启 MQTT 会话，确保连接建立并订阅默认下行主题。
        /// </summary>
        /// <param name="cancellationToken">外部取消令牌。</param>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 发送原始二进制数据到指定主题，默认使用配置中的上行主题。
        /// </summary>
        /// <param name="payload">待发送的原始二进制数据。</param>
        /// <param name="topic">可选的自定义主题，缺省为配置中的上行主题。</param>
        /// <param name="retain">是否设置保留标记。</param>
        /// <param name="cancellationToken">外部取消令牌。</param>
        public async Task SendAsync(ReadOnlyMemory<byte> payload, string? topic = null, bool retain = false, CancellationToken cancellationToken = default)
        {
            

            if (_disposed || !_config.Enabled)
            {
                return;
            }

            var targetTopic = string.IsNullOrWhiteSpace(topic) ? _config.UplinkTopic : topic; // 目标主题
            if (string.IsNullOrWhiteSpace(targetTopic))
            {
                AppLogger.Warn("未配置 MQTT 上行主题，已跳过发送。");
                return;
            }

            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            if (_client == null || !_client.IsConnected)
            {
                AppLogger.Warn("MQTT 客户端尚未连接，发送已忽略。");
                return;
            }

            var message = new MqttApplicationMessageBuilder() // MQTT 消息构建器
                .WithTopic(targetTopic)
                .WithPayload(payload.ToArray()) // 将 ReadOnlyMemory<byte> 转为 byte[]
                .WithRetainFlag(retain)
                .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)Math.Clamp(_config.Qos, 0, 2))
                .Build();

            await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
            AppLogger.Info($"已发送 MQTT 消息，主题: {targetTopic}, 长度: {payload.Length}, 内容: {Convert.ToHexString(payload.Span)}");
        }

        /// <summary>
        /// 将字符串按指定编码转换为原始字节后发送，保持完整内容。
        /// </summary>
        /// <param name="text">待发送的字符串内容。</param>
        /// <param name="encoding">转换所用编码，默认 UTF-8。</param>
        /// <param name="topic">可选的自定义主题。</param>
        /// <param name="retain">是否设置保留标记。</param>
        /// <param name="cancellationToken">外部取消令牌。</param>
        public async Task SendStringAsync(string text, Encoding? encoding = null, string? topic = null, bool retain = false, CancellationToken cancellationToken = default)
        {
            encoding ??= Encoding.UTF8; // 字符串编码
            var input = text ?? string.Empty; // 待编码字符串
            var encoded = encoding.GetBytes(input); // 编码后的字节
            await SendAsync(encoded, topic, retain, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 释放 MQTT 客户端资源并断开连接，停止接收后续消息。
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_client != null)
            {
                try
                {
                    if (_client.IsConnected)
                    {
                        await _client.DisconnectAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"断开 MQTT 时异常: {ex.Message}");
                }
                finally
                {
                    _client.Dispose();
                    _client = null;
                }
            }

            _connectLock.Dispose();
        }

        /// <summary>
        /// 当收到 MQTT 消息时触发，将完整载荷上抛给业务层。
        /// </summary>
        public event EventHandler<MqttBridgeMessage>? MessageReceived;


        /// <summary>
        /// 获取当前配置的默认下行主题，供业务层按需筛选主通道消息。
        /// </summary>
        public string DownlinkTopic => _config.DownlinkTopic; // 下行主题

        /// <summary>
        /// 建立连接并订阅默认下行主题，避免重复连接。
        /// </summary>
        /// <param name="cancellationToken">外部取消令牌。</param>
        private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            if (_disposed || !_config.Enabled)
            {
                return;
            }

            if (_client != null && _client.IsConnected)
            {
                return;
            }

            await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_client != null && _client.IsConnected)
                {
                    return;
                }

                _client ??= _factory.CreateMqttClient();
                _client.ApplicationMessageReceivedAsync -= HandleApplicationMessageAsync;
                _client.ApplicationMessageReceivedAsync += HandleApplicationMessageAsync;
                _client.DisconnectedAsync -= HandleDisconnectedAsync;
                _client.DisconnectedAsync += HandleDisconnectedAsync;

                var options = BuildClientOptions();
                await _client.ConnectAsync(options, cancellationToken).ConfigureAwait(false);

                var qos = (MqttQualityOfServiceLevel)Math.Clamp(_config.Qos, 0, 2); // 订阅的 QoS
                var downTopic = _config.DownlinkTopic; // 下行主题
                if (!string.IsNullOrWhiteSpace(downTopic))
                {
                    await SubscribeTopicAsync(downTopic, qos, cancellationToken, "已订阅 MQTT 下行主题").ConfigureAwait(false);
                }
                else
                {
                    AppLogger.Warn("未配置 MQTT 下行主题，接收功能已禁用。");
                }
                foreach (var extraTopic in SnapshotAdditionalTopics())
                {
                    if (string.IsNullOrWhiteSpace(extraTopic))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(downTopic) && string.Equals(extraTopic, downTopic, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    await SubscribeTopicAsync(extraTopic, qos, cancellationToken, "已订阅 MQTT 额外主题").ConfigureAwait(false);
                }
            }
            finally
            {
                _connectLock.Release();
            }
        }

        /// <summary>
        /// 记录额外的下行主题并在连接就绪时发起订阅，用于扩展监听 PLC 控制等自定义通道。
        /// </summary>
        /// <param name="topic">待订阅的主题名称，自动忽略空白值与重复请求。</param>
        /// <param name="cancellationToken">外部取消令牌，便于在调用方关闭时提前退出。</param>
        /// <returns>异步任务对象。</returns>
        public async Task SubscribeAdditionalTopicAsync(string topic, CancellationToken cancellationToken = default)
        {
            if (_disposed || !_config.Enabled)
            {
                return;
            }

            var normalized = topic?.Trim(); // 规范化主题
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            lock (_subscriptionLock)
            {
                _additionalTopics.Add(normalized);
            }

            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            if (_client == null || !_client.IsConnected)
            {
                return;
            }

            var qos = (MqttQualityOfServiceLevel)Math.Clamp(_config.Qos, 0, 2); // 订阅的 QoS
            await SubscribeTopicAsync(normalized, qos, cancellationToken, "已订阅 MQTT 额外主题").ConfigureAwait(false);
        }

        /// <summary>
        /// 返回当前记录的额外主题快照，避免遍历过程中与新增操作发生竞争。
        /// </summary>
        /// <returns>包含所有额外主题的字符串数组。</returns>
        private string[] SnapshotAdditionalTopics()
        {
            lock (_subscriptionLock)
            {
                return _additionalTopics.ToArray();
            }
        }

        /// <summary>
        /// 向 MQTT 服务器发送订阅请求，并校验返回码，确保主题真正被 Broker 接受。
        /// </summary>
        /// <param name="topic">目标主题。</param>
        /// <param name="qos">请求的 QoS 等级。</param>
        /// <param name="cancellationToken">外部取消令牌。</param>
        /// <param name="logPrefix">日志前缀，便于区分订阅来源。</param>
        /// <returns>异步任务对象。</returns>
        private async Task SubscribeTopicAsync(string topic, MqttQualityOfServiceLevel qos, CancellationToken cancellationToken, string logPrefix)
        {
            if (_client == null || !_client.IsConnected)
            {
                return;
            }

            var filter = new MqttTopicFilterBuilder() // 订阅过滤器
                .WithTopic(topic)
                .WithQualityOfServiceLevel(qos)
                .Build();


            var subscribeResult = await _client.SubscribeAsync(filter, cancellationToken).ConfigureAwait(false); // 订阅结果
            if (subscribeResult?.Items == null || subscribeResult.Items.Count == 0)
            {
                AppLogger.Warn($"{logPrefix}: {topic} 返回空的结果集，无法确认订阅是否成功。");
                return;
            }

            var failedItems = new List<MqttClientSubscribeResultItem>(); // 失败的返回项
            foreach (var item in subscribeResult.Items)
            {
                if (item.ResultCode != MqttClientSubscribeResultCode.GrantedQoS0 &&
                    item.ResultCode != MqttClientSubscribeResultCode.GrantedQoS1 &&
                    item.ResultCode != MqttClientSubscribeResultCode.GrantedQoS2)
                {
                    failedItems.Add(item);
                }
            }

            if (failedItems.Count > 0)
            {
                var reasons = string.Join(", ", failedItems.Select(i => $"[{i.TopicFilter?.Topic ?? "未知"}:{i.ResultCode}]")).Trim();
                var message = $"订阅 MQTT 主题失败: {topic}, 原因: {reasons}";
                AppLogger.Error(message);
                throw new InvalidOperationException(message);
            }

            var granted = string.Join(", ", subscribeResult.Items.Select(i => $"{i.TopicFilter?.Topic ?? topic}:{i.ResultCode}"));
            AppLogger.Info($"{logPrefix}: {topic} (Granted={granted})");
        }

        /// <summary>
        /// 构造 MQTT 客户端连接参数，包含服务器、凭据与会话设置。
        /// </summary>
        /// <returns>已配置的 MQTT 客户端选项。</returns>
        private MqttClientOptions BuildClientOptions()
        {
            if (!Uri.TryCreate(_config.ServerUri, UriKind.Absolute, out var uri))
            {
                throw new InvalidOperationException($"无效的 MQTT ServerUri: {_config.ServerUri}");
            }

            var port = uri.IsDefaultPort ? 1883 : uri.Port; // MQTT 端口
            var clientId = PrepareClientId(); // 客户端标识

            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(uri.Host, port)
                .WithClientId(clientId)
                .WithCleanSession(_config.CleanSession)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(Math.Max(5, _config.KeepAliveInterval)))
                .WithTimeout(TimeSpan.FromSeconds(Math.Max(1, _config.ConnectionTimeout)));

            if (!string.IsNullOrWhiteSpace(_config.Username))
            {
                builder.WithCredentials(_config.Username, _config.Password);
            }

            return builder.Build();
        }

        /// <summary>
        /// 生成符合 MQTT 限制的客户端标识，优先使用配置中的 ClientId。
        /// </summary>
        /// <returns>最终用于连接的 ClientId。</returns>
        private string PrepareClientId()
        {
            var configured = string.IsNullOrWhiteSpace(_config.ClientId)
                ? $"simple_{Environment.MachineName}_{Guid.NewGuid():N}"
                : _config.ClientId.Trim(); // 客户端 ID 配置

            const int maxLength = 64; // ClientId 最大长度
            return configured.Length > maxLength ? configured[..maxLength] : configured;
        }

        /// <summary>
        /// MQTT 客户端掉线后的重连回调，延迟后尝试自动重连。
        /// </summary>
        /// <param name="args">断线事件参数。</param>
        /// <returns>异步任务。</returns>
        private Task HandleDisconnectedAsync(MqttClientDisconnectedEventArgs args)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            return Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                try
                {
                    await EnsureConnectedAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"MQTT 重连失败: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 处理收到的 MQTT 消息，将原始载荷转换后交给上层处理。
        /// </summary>
        /// <param name="args">消息参数。</param>
        /// <returns>异步任务。</returns>
        private Task HandleApplicationMessageAsync(MqttApplicationMessageReceivedEventArgs args)
        {
            try
            {
                var payload = args.ApplicationMessage?.PayloadSegment; // 消息负载
                if (payload == null)
                {
                    return Task.CompletedTask;
                }

                var buffer = payload.Value.ToArray(); // 接收缓冲区
                var topic = args.ApplicationMessage?.Topic ?? string.Empty; // 消息主题
                AppLogger.Info($"收到 MQTT 消息，主题: {topic}, 长度: {buffer.Length}, 内容: {Convert.ToHexString(buffer)}");
                MessageReceived?.Invoke(this, new MqttBridgeMessage(topic, buffer));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理 MQTT 消息时异常: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 表示从 MQTT 收到的完整消息，包含原始主题与载荷。
        /// </summary>
        public sealed class MqttBridgeMessage
        {
            /// <summary>
            /// 使用指定主题与原始载荷初始化消息包装对象。
            /// </summary>
            /// <param name="topic">消息主题。</param>
            /// <param name="payload">任意长度的原始载荷。</param>
            public MqttBridgeMessage(string topic, byte[] payload)
            {
                Topic = topic ?? string.Empty; // 消息主题
                Payload = new ReadOnlyMemory<byte>(payload ?? Array.Empty<byte>()); // 原始载荷
            }

            /// <summary>
            /// 获取消息所属的 MQTT 主题。
            /// </summary>
            public string Topic { get; } // 消息主题

            /// <summary>
            /// 获取原始载荷内容。
            /// </summary>
            public ReadOnlyMemory<byte> Payload { get; } // 原始载荷
        }
    }
}
