using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WpfVideoPet.service
{
    /// <summary>
    /// 提供全局复用的 MQTT 客户端，支持多处同时发布与订阅。
    /// </summary>
    public sealed class MqttSingletonService : IAsyncDisposable
    {
        private static readonly object _syncRoot = new(); // 静态同步锁
        private static MqttSingletonService? _instance; // 单例实例

        private readonly MqttConfig _config; // MQTT 配置信息
        private readonly MqttFactory _factory = new(); // MQTT 工厂
        private readonly SemaphoreSlim _connectLock = new(1, 1); // 连接互斥锁
        private readonly Dictionary<string, List<Func<MqttApplicationMessageReceivedEventArgs, Task>>> _handlers = new(StringComparer.OrdinalIgnoreCase); // 订阅处理器字典

        private IMqttClient? _client; // MQTT 客户端实例
        private bool _disposed; // 释放状态标记

        /// <summary>
        /// 获取或创建 MQTT 单例。
        /// </summary>
        /// <param name="config">MQTT 配置信息。</param>
        /// <returns>全局单例实例。</returns>
        public static MqttSingletonService GetOrCreate(MqttConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            lock (_syncRoot)
            {
                _instance ??= new MqttSingletonService(config);
                return _instance;
            }
        }

        /// <summary>
        /// 初始化 MQTT 单例实例。
        /// </summary>
        /// <param name="config">MQTT 配置。</param>
        private MqttSingletonService(MqttConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// 发布字符串消息到指定主题。
        /// </summary>
        /// <param name="topic">目标主题。</param>
        /// <param name="payload">字符串内容。</param>
        /// <param name="retain">是否保留消息。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        public Task PublishAsync(string topic, string payload, bool retain = false, CancellationToken cancellationToken = default)
        {
            var bytes = Encoding.UTF8.GetBytes(payload ?? string.Empty); // 发布内容
            return PublishAsync(topic, bytes, retain, cancellationToken);
        }

        /// <summary>
        /// 发布二进制消息到指定主题。
        /// </summary>
        /// <param name="topic">目标主题。</param>
        /// <param name="payload">二进制内容。</param>
        /// <param name="retain">是否保留消息。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        public async Task PublishAsync(string topic, byte[] payload, bool retain = false, CancellationToken cancellationToken = default)
        {
            if (_disposed || !_config.Enabled || string.IsNullOrWhiteSpace(topic))
            {
                return;
            }

            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            if (_client == null || !_client.IsConnected)
            {
                return;
            }

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload ?? Array.Empty<byte>())
                .WithRetainFlag(retain)
                .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)Math.Clamp(_config.Qos, 0, 2))
                .Build();

            await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 订阅主题并绑定处理委托。
        /// </summary>
        /// <param name="topic">订阅的主题。</param>
        /// <param name="handler">处理回调。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        public async Task SubscribeAsync(string topic, Func<MqttApplicationMessageReceivedEventArgs, Task> handler, CancellationToken cancellationToken = default)
        {
            if (_disposed || handler == null || string.IsNullOrWhiteSpace(topic) || !_config.Enabled)
            {
                return;
            }

            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            if (_client == null || !_client.IsConnected)
            {
                return;
            }

            bool needSubscribe;
            lock (_handlers)
            {
                if (!_handlers.TryGetValue(topic, out var list))
                {
                    list = new List<Func<MqttApplicationMessageReceivedEventArgs, Task>>();
                    _handlers[topic] = list;
                }

                if (!list.Contains(handler))
                {
                    list.Add(handler);
                }

                needSubscribe = list.Count == 1;
            }

            if (needSubscribe)
            {
                var filter = new MqttTopicFilterBuilder()
                    .WithTopic(topic)
                    .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)Math.Clamp(_config.Qos, 0, 2))
                    .Build();

                await _client.SubscribeAsync(filter, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 取消订阅主题并移除处理委托。
        /// </summary>
        /// <param name="topic">目标主题。</param>
        /// <param name="handler">处理回调。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        public async Task UnsubscribeAsync(string topic, Func<MqttApplicationMessageReceivedEventArgs, Task> handler, CancellationToken cancellationToken = default)
        {
            if (handler == null || string.IsNullOrWhiteSpace(topic))
            {
                return;
            }

            bool needUnsubscribe = false;
            lock (_handlers)
            {
                if (_handlers.TryGetValue(topic, out var list))
                {
                    list.Remove(handler);
                    if (list.Count == 0)
                    {
                        _handlers.Remove(topic);
                        needUnsubscribe = true;
                    }
                }
            }

            if (needUnsubscribe && _client != null && _client.IsConnected)
            {
                await _client.UnsubscribeAsync(new MqttClientUnsubscribeOptionsBuilder().WithTopicFilter(topic).Build(), cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 释放 MQTT 客户端资源。
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
                    Debug.WriteLine($"MQTT 断开时异常: {ex.Message}");
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
        /// 保证客户端与服务器保持连接。
        /// </summary>
        /// <param name="cancellationToken">取消令牌。</param>
        private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            if (_disposed || !_config.Enabled)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_config.ServerUri))
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
            }
            finally
            {
                _connectLock.Release();
            }
        }

        /// <summary>
        /// 构建 MQTT 客户端连接参数。
        /// </summary>
        /// <returns>客户端配置。</returns>
        private MqttClientOptions BuildClientOptions()
        {
            if (!Uri.TryCreate(_config.ServerUri, UriKind.Absolute, out var uri))
            {
                throw new InvalidOperationException($"无效的 MQTT ServerUri: {_config.ServerUri}");
            }

            var port = uri.IsDefaultPort ? 1883 : uri.Port; // 端口号
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
        /// 生成合法的客户端标识。
        /// </summary>
        /// <returns>处理后的 ClientId。</returns>
        private string PrepareClientId()
        {
            var clientId = !string.IsNullOrWhiteSpace(_config.ClientId)
                ? _config.ClientId
                : $"wpfpet_{Environment.MachineName}_{Guid.NewGuid():N}";

            const int maxLength = 64; // 客户端标识最大长度
            return clientId.Length > maxLength ? clientId[..maxLength] : clientId;
        }

        /// <summary>
        /// 处理断线事件并尝试重连。
        /// </summary>
        /// <param name="arg">断线参数。</param>
        private Task HandleDisconnectedAsync(MqttClientDisconnectedEventArgs arg)
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
        /// 分发接收到的 MQTT 消息。
        /// </summary>
        /// <param name="args">消息参数。</param>
        private Task HandleApplicationMessageAsync(MqttApplicationMessageReceivedEventArgs args)
        {
            List<Func<MqttApplicationMessageReceivedEventArgs, Task>>? delegates = null;
            var topic = args.ApplicationMessage?.Topic;

            if (!string.IsNullOrWhiteSpace(topic))
            {
                lock (_handlers)
                {
                    if (_handlers.TryGetValue(topic, out var list) && list.Count > 0)
                    {
                        delegates = new List<Func<MqttApplicationMessageReceivedEventArgs, Task>>(list);
                    }
                }
            }

            if (delegates == null || delegates.Count == 0)
            {
                return Task.CompletedTask;
            }

            foreach (var handler in delegates)
            {
                try
                {
                    var task = handler(args);
                    if (task.IsCompleted)
                    {
                        continue;
                    }

                    task.ContinueWith(static t =>
                    {
                        if (t.Exception != null)
                        {
                            Debug.WriteLine($"MQTT 处理委托异常: {t.Exception.InnerException?.Message}");
                        }
                    }, TaskContinuationOptions.OnlyOnFaulted);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"执行 MQTT 回调失败: {ex.Message}");
                }
            }

            return Task.CompletedTask;
        }
    }
}