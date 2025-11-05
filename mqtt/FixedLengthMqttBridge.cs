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

namespace WpfVideoPet.mqtt
{
    /// <summary>
    /// 提供最精简的 16 字节 MQTT 通信框架，负责连接、收发与日志记录。
    /// </summary>
    public sealed class FixedLengthMqttBridge : IAsyncDisposable
    {
        private readonly MqttConfig _config; // MQTT 配置
        private readonly MqttFactory _factory = new(); // MQTT 工厂
        private readonly SemaphoreSlim _connectLock = new(1, 1); // 连接互斥锁
        private IMqttClient? _client; // MQTT 客户端实例
        private bool _disposed; // 释放标记

        /// <summary>
        /// 初始化 16 字节 MQTT 桥接服务，并记录配置引用供后续使用。
        /// </summary>
        /// <param name="config">MQTT 通信配置，需包含服务器地址与 Topic 信息。</param>
        public FixedLengthMqttBridge(MqttConfig config)
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
        /// 发送固定 16 字节的原始数据到指定主题，默认使用上行主题。
        /// </summary>
        /// <param name="payload">待发送的数据，必须正好 16 字节。</param>
        /// <param name="topic">可选的自定义主题，缺省为配置中的上行主题。</param>
        /// <param name="retain">是否设置保留标记。</param>
        /// <param name="cancellationToken">外部取消令牌。</param>
        public async Task SendAsync(ReadOnlyMemory<byte> payload, string? topic = null, bool retain = false, CancellationToken cancellationToken = default)
        {
            if (payload.Length != 16)
            {
                throw new ArgumentException("发送的数据必须是 16 字节。", nameof(payload));
            }

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
            AppLogger.Info($"已发送 16 字节 MQTT 消息，主题: {targetTopic}, 内容: {Convert.ToHexString(payload.Span)}");
        }

        /// <summary>
        /// 将字符串按指定编码转换为 16 字节发送，不足补齐，超出截断。
        /// </summary>
        /// <param name="text">待发送的字符串内容。</param>
        /// <param name="encoding">转换所用编码，默认 UTF-8。</param>
        /// <param name="padding">补齐字节值，默认 0x00。</param>
        /// <param name="topic">可选的自定义主题。</param>
        /// <param name="retain">是否设置保留标记。</param>
        /// <param name="cancellationToken">外部取消令牌。</param>
        public async Task SendStringAsync(string text, Encoding? encoding = null, byte padding = 0x00, string? topic = null, bool retain = false, CancellationToken cancellationToken = default)
        {
            encoding ??= Encoding.UTF8; // 字符串编码
            var input = text ?? string.Empty; // 待编码字符串
            var buffer = ArrayPool<byte>.Shared.Rent(16); // 可重用缓冲区
            try
            {
                Array.Clear(buffer, 0, 16);
                var encoded = encoding.GetBytes(input); // 编码后的字节
                var copyLength = Math.Min(16, encoded.Length); // 需要复制的长度
                Array.Copy(encoded, buffer, copyLength);
                if (copyLength < 16)
                {
                    for (var i = copyLength; i < 16; i++)
                    {
                        buffer[i] = padding;
                    }
                }
                else if (encoded.Length > 16)
                {
                    AppLogger.Warn($"字符串超出 16 字节限制，已截断发送: {input}");
                }

                await SendAsync(buffer.AsMemory(0, 16), topic, retain, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
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
        /// 当收到 MQTT 消息时触发，将符合长度的 16 字节数据上抛给业务层。
        /// </summary>
        public event EventHandler<ReadOnlyMemory<byte>>? MessageReceived;

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

                var downTopic = _config.DownlinkTopic; // 下行主题
                if (!string.IsNullOrWhiteSpace(downTopic))
                {
                    var qos = (MqttQualityOfServiceLevel)Math.Clamp(_config.Qos, 0, 2); // 订阅的 QoS
                    var filter = new MqttTopicFilterBuilder()
                        .WithTopic(downTopic)
                        .WithQualityOfServiceLevel(qos)
                        .Build();

                    await _client.SubscribeAsync(filter, cancellationToken).ConfigureAwait(false);
                    AppLogger.Info($"已订阅 MQTT 下行主题: {downTopic}");
                }
                else
                {
                    AppLogger.Warn("未配置 MQTT 下行主题，接收功能已禁用。");
                }
            }
            finally
            {
                _connectLock.Release();
            }
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
        /// 处理收到的 MQTT 消息，过滤并上抛 16 字节数据包。
        /// </summary>
        /// <param name="args">消息参数。</param>
        /// <returns>异步任务。</returns>
        private Task HandleApplicationMessageAsync(MqttApplicationMessageReceivedEventArgs args)
        {
            try
            {
                var payload = args.ApplicationMessage?.PayloadSegment; // 消息负载
                if (payload == null || payload.Value.Count == 0)
                {
                    return Task.CompletedTask;
                }

                if (payload.Value.Count != 16)
                {
                    AppLogger.Warn($"收到非 16 字节消息，长度: {payload.Value.Count}，已忽略。");
                    return Task.CompletedTask;
                }

                var buffer = payload.Value.ToArray(); // 接收缓冲区
                var topic = args.ApplicationMessage?.Topic ?? string.Empty; // 消息主题
                AppLogger.Info($"收到 16 字节 MQTT 消息，主题: {topic}, 内容: {Convert.ToHexString(buffer)}");
                MessageReceived?.Invoke(this, new FixedLengthMqttMessage(topic, buffer));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理 MQTT 消息时异常: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 表示从 MQTT 收到的 16 字节定长消息，包含原始主题与载荷。
        /// </summary>
        public sealed class FixedLengthMqttMessage
        {
            /// <summary>
            /// 使用指定主题与原始载荷初始化消息包装对象。
            /// </summary>
            /// <param name="topic">消息主题。</param>
            /// <param name="payload">长度为 16 字节的原始载荷。</param>
            public FixedLengthMqttMessage(string topic, byte[] payload)
            {
                if (payload == null || payload.Length != 16)
                {
                    throw new ArgumentException("MQTT 消息必须是 16 字节长度。", nameof(payload));
                }

                Topic = topic ?? string.Empty; // 消息主题
                Payload = new ReadOnlyMemory<byte>(payload); // 原始载荷
            }

            /// <summary>
            /// 获取消息所属的 MQTT 主题。
            /// </summary>
            public string Topic { get; } // 消息主题

            /// <summary>
            /// 获取 16 字节的原始载荷内容。
            /// </summary>
            public ReadOnlyMemory<byte> Payload { get; } // 原始载荷
        }
    }
}
