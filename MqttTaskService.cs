using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Windows;

namespace WpfVideoPet
{
    /// <summary>
    /// 负责与后端 BladeX 系统进行 MQTT 通讯的服务，订阅任务 Topic 并将媒体任务向上抛出。
    /// </summary>
    public sealed class MqttTaskService : IAsyncDisposable
    {
        private readonly MqttConfig _config;
        private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly SemaphoreSlim _connectLock = new(1, 1);
        private readonly HashSet<string> _processedJobIds = new(StringComparer.Ordinal);
        private readonly MqttFactory _factory = new();

        private IMqttClient? _client;
        private bool _disposed;

        public MqttTaskService(MqttConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// 收到远程媒体任务时触发的事件，由 UI 层决定如何播放与缓存。
        /// </summary>
        public event EventHandler<RemoteMediaTask>? RemoteMediaTaskReceived;

        /// <summary>
        /// 建立连接并订阅任务下发 Topic。
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed || !_config.Enabled)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_config.ServerUri))
            {
                Debug.WriteLine("MQTT 未配置服务器地址，跳过启动。");
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

                var topic = _config.DownlinkTopic;
                if (!string.IsNullOrWhiteSpace(topic))
                {
                    var qos = (MqttQualityOfServiceLevel)Math.Clamp(_config.Qos, 0, 2);
                    var filter = new MqttTopicFilterBuilder()
                        .WithTopic(topic)
                        .WithQualityOfServiceLevel(qos)
                        .Build();

                    await _client.SubscribeAsync(filter, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Debug.WriteLine("MQTT 未配置 ClientId，无法推导订阅 Topic。");
                }
            }
            finally
            {
                _connectLock.Release();
            }
        }

        private MqttClientOptions BuildClientOptions()
        {
            if (!Uri.TryCreate(_config.ServerUri, UriKind.Absolute, out var uri))
            {
                throw new InvalidOperationException($"无效的 MQTT ServerUri: {_config.ServerUri}");
            }

            var port = uri.IsDefaultPort ? 1883 : uri.Port;
            var clientId = PrepareClientId();

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

        private string PrepareClientId()
        {
            string clientId = !string.IsNullOrWhiteSpace(_config.ClientId)
                ? _config.ClientId
                : $"wpfpet_{Environment.MachineName}_{Guid.NewGuid():N}";

            const int maxLength = 64;
            return clientId.Length > maxLength ? clientId[..maxLength] : clientId;
        }

        private Task HandleDisconnectedAsync(MqttClientDisconnectedEventArgs arg)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            return Task.Run(async () =>
            {
                // 简单的重连策略：5 秒后尝试重新连接。
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                try
                {
                    await StartAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"MQTT 重连失败: {ex.Message}");
                }
            });
        }

        private Task HandleApplicationMessageAsync(MqttApplicationMessageReceivedEventArgs args)
        {
            try
            {
                var segment = args.ApplicationMessage?.PayloadSegment;
                if (segment is null || segment.Value.Count == 0)
                {
                    return Task.CompletedTask;
                }

                // 先弹出原始消息内容，便于测试订阅主题是否收到数据。
                ShowIncomingMessagePopup(segment.Value);

                if (!TryParseRemoteMediaTask(segment.Value, out var task))
                {
                    return Task.CompletedTask;
                }

                var remoteTask = task!;
                RemoteMediaTaskReceived?.Invoke(this, remoteTask);
            }
            catch (Exception ex) when (ex is not JsonException)
            {
                Debug.WriteLine($"处理 MQTT 消息时异常: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private bool TryParseRemoteMediaTask(ArraySegment<byte> payload, out RemoteMediaTask? task)
        {
            task = null;

            try
            {
                var message = JsonSerializer.Deserialize<TaskDownlinkMessage>(payload.AsSpan(), _serializerOptions);
                if (message == null || string.IsNullOrWhiteSpace(message.JobId))
                {
                    return false;
                }

                lock (_processedJobIds)
                {
                    if (!_processedJobIds.Add(message.JobId))
                    {
                        return false;
                    }
                }

                if (!string.Equals(message.JobType, "REMOTE_MEDIA", StringComparison.OrdinalIgnoreCase) || message.Media == null)
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(message.Media.DownloadUrl) && string.IsNullOrWhiteSpace(message.Media.AccessibleUrl))
                {
                    Debug.WriteLine("MQTT 远程媒体任务缺少可用的播放地址，已忽略。");
                    return false;
                }

                var mediaInfo = new RemoteMediaInfo
                {
                    CoverUrl = message.Media.CoverUrl,
                    DownloadUrl = message.Media.DownloadUrl,
                    AccessibleUrl = message.Media.AccessibleUrl,
                    FileHash = message.Media.FileHash,
                    FileSize = message.Media.FileSize,
                    MediaId = message.Media.MediaId,
                    JobId = message.JobId
                };

                task = new RemoteMediaTask(
                    message.JobId!,
                    message.ScheduleTime,
                    message.JobStatus,
                    message.ClientId,
                    message.Topic,
                    message.Timestamp,
                    mediaInfo);

                return true;
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"MQTT 消息解析失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 通过 UI 线程弹窗展示 MQTT 收到的原始消息，便于快速验证通信链路。
        /// </summary>
        /// <param name="payload">MQTT 消息负载。</param>
        private void ShowIncomingMessagePopup(ArraySegment<byte> payload)
        {
            if (payload.Array is null)
            {
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }

            var text = Encoding.UTF8.GetString(payload.Array, payload.Offset, payload.Count);
            if (string.IsNullOrWhiteSpace(text))
            {
                text = "(收到空消息)";
            }

            void ShowDialog()
            {
                MessageBox.Show(text, "MQTT 测试消息", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            if (dispatcher.CheckAccess())
            {
                ShowDialog();
            }
            else
            {
                dispatcher.BeginInvoke((Action)ShowDialog);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                if (_client != null)
                {
                    _client.ApplicationMessageReceivedAsync -= HandleApplicationMessageAsync;
                    _client.DisconnectedAsync -= HandleDisconnectedAsync;

                    if (_client.IsConnected)
                    {
                        await _client.DisconnectAsync().ConfigureAwait(false);
                    }

                    _client.Dispose();
                    _client = null;
                }
            }
            finally
            {
                _connectLock.Dispose();
            }
        }

        private sealed class TaskDownlinkMessage
        {
            public string? JobId { get; set; }
            public string? ScheduleTime { get; set; }
            public string? JobStatus { get; set; }
            public string? ClientId { get; set; }
            public string? Topic { get; set; }
            public RemoteMediaInfoPayload? Media { get; set; }
            public string? JobType { get; set; }
            public long? Timestamp { get; set; }
        }

        private sealed class RemoteMediaInfoPayload
        {
            public string? CoverUrl { get; set; }
            public long? FileSize { get; set; }
            public string? DownloadUrl { get; set; }
            public string? FileHash { get; set; }
            public string? AccessibleUrl { get; set; }
            public string? MediaId { get; set; }
        }
    }

    /// <summary>
    /// MQTT 下发的远程媒体任务实体。
    /// </summary>
    public sealed class RemoteMediaTask
    {
        public RemoteMediaTask(string jobId, string? scheduleTime, string? jobStatus, string? clientId, string? topic, long? timestamp, RemoteMediaInfo media)
        {
            JobId = jobId;
            ScheduleTime = scheduleTime;
            JobStatus = jobStatus;
            ClientId = clientId;
            Topic = topic;
            Timestamp = timestamp;
            Media = media ?? throw new ArgumentNullException(nameof(media));
        }

        public string JobId { get; }
        public string? ScheduleTime { get; }
        public string? JobStatus { get; }
        public string? ClientId { get; }
        public string? Topic { get; }
        public long? Timestamp { get; }
        public RemoteMediaInfo Media { get; }
    }

    /// <summary>
    /// 媒体资源的描述信息。
    /// </summary>
    public sealed class RemoteMediaInfo
    {
        public string? CoverUrl { get; set; }
        public long? FileSize { get; set; }
        public string? DownloadUrl { get; set; }
        public string? FileHash { get; set; }
        public string? AccessibleUrl { get; set; }
        public string? MediaId { get; set; }
        public string? JobId { get; set; }
    }
}
