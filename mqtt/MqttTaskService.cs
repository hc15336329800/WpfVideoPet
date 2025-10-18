using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Windows;

namespace WpfVideoPet.mqtt
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

            if (payload.Array is null || payload.Count == 0)
            {
                return false;
            }

            var text = Encoding.UTF8.GetString(payload.Array, payload.Offset, payload.Count).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (TryParseStandardRemoteMediaTask(text, out task))
            {
                return true;
            }

            if (TryParseFlexibleRemoteMediaTask(text, out task))
            {
                return true;
            }

            if (TryParseUrlPayload(text, out task))
            {
                return true;
            }

            return false;
        }

        private bool TryParseStandardRemoteMediaTask(string text, out RemoteMediaTask? task)
        {
            task = null;

            TaskDownlinkMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<TaskDownlinkMessage>(text, _serializerOptions);
            }
            catch (JsonException)
            {
                return false;
            }

            if (message == null)
            {
                return false;
            }

            var jobId = string.IsNullOrWhiteSpace(message.JobId)
                ? Guid.NewGuid().ToString("N")
                : message.JobId!.Trim();

            if (!IsRemoteMediaJobType(message.JobType))
            {
                return false;
            }

            if (message.Media == null)
            {
                return false;
            }

            var downloadUrl = TrimToNull(message.Media.DownloadUrl);
            var accessibleUrl = TrimToNull(message.Media.AccessibleUrl);

            if (string.IsNullOrWhiteSpace(downloadUrl) && string.IsNullOrWhiteSpace(accessibleUrl))
            {
                Debug.WriteLine("MQTT 远程媒体任务缺少可用的播放地址，已忽略。");
                return false;
            }

            // 为了保证每次下发都能触发播放，取消对相同 JobId 的去重逻辑。
            RegisterJobId(jobId);

            var mediaInfo = new RemoteMediaInfo
            {
                CoverUrl = TrimToNull(message.Media.CoverUrl),
                DownloadUrl = downloadUrl,
                AccessibleUrl = accessibleUrl,
                FileHash = TrimToNull(message.Media.FileHash),
                FileSize = message.Media.FileSize,
                MediaId = TrimToNull(message.Media.MediaId),
                JobId = jobId
            };

            task = new RemoteMediaTask(
                jobId,
                TrimToNull(message.ScheduleTime),
                TrimToNull(message.JobStatus),
                TrimToNull(message.ClientId),
                TrimToNull(message.Topic),
                message.Timestamp,
                mediaInfo);

            return true;
        }

        private bool TryParseFlexibleRemoteMediaTask(string text, out RemoteMediaTask? task)
        {
            task = null;

            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var jobType = GetString(root, "jobType", "job_type", "type");
                if (!IsRemoteMediaJobType(jobType))
                {
                    return false;
                }

                var jobId = TrimToNull(GetString(root, "jobId", "job_id", "taskId", "task_id", "id"))
                    ?? Guid.NewGuid().ToString("N");

                var scheduleTime = TrimToNull(GetString(root, "scheduleTime", "schedule_time"));
                var jobStatus = TrimToNull(GetString(root, "jobStatus", "job_status", "status"));
                var clientId = TrimToNull(GetString(root, "clientId", "client_id", "terminalId", "terminal_id"));
                var topic = TrimToNull(GetString(root, "topic", "topicName", "topic_name"));
                var timestamp = GetInt64(root, "timestamp", "ts", "time");

                string? directMediaUrl = null;
                RemoteMediaInfo? mediaInfo = null;

                if (TryGetProperty(root, out var mediaElement, "media", "mediaInfo", "media_info", "data"))
                {
                    if (mediaElement.ValueKind == JsonValueKind.Object)
                    {
                        mediaInfo = BuildMediaInfo(jobId, mediaElement, root, directMediaUrl: null);
                    }
                    else if (mediaElement.ValueKind == JsonValueKind.String)
                    {
                        directMediaUrl = TrimToNull(mediaElement.GetString());
                    }
                    else if (mediaElement.ValueKind == JsonValueKind.Array && mediaElement.GetArrayLength() > 0)
                    {
                        var first = mediaElement[0];
                        if (first.ValueKind == JsonValueKind.Object)
                        {
                            mediaInfo = BuildMediaInfo(jobId, first, root, directMediaUrl: null);
                        }
                        else if (first.ValueKind == JsonValueKind.String)
                        {
                            directMediaUrl = TrimToNull(first.GetString());
                        }
                    }
                }

                mediaInfo ??= BuildMediaInfo(jobId, null, root, directMediaUrl);

                if (mediaInfo == null)
                {
                    return false;
                }
                RegisterJobId(jobId);


                task = new RemoteMediaTask(jobId, scheduleTime, jobStatus, clientId, topic, timestamp, mediaInfo);
                return true;
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"MQTT 消息解析失败: {ex.Message}");
                return false;
            }
        }

        private bool TryParseUrlPayload(string text, out RemoteMediaTask? task)
        {
            task = null;

            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var jobId = Guid.NewGuid().ToString("N");

            RegisterJobId(jobId);


            var lastSegment = uri.Segments.Length > 0
                ? uri.Segments[uri.Segments.Length - 1].Trim('/')
                : uri.Host;

            var mediaInfo = new RemoteMediaInfo
            {
                AccessibleUrl = uri.ToString(),
                DownloadUrl = uri.ToString(),
                MediaId = string.IsNullOrWhiteSpace(lastSegment) ? null : lastSegment,
                JobId = jobId
            };

            task = new RemoteMediaTask(jobId, null, null, null, null, null, mediaInfo);
            return true;
        }

        private static bool IsRemoteMediaJobType(string? jobType)
        {
            if (string.IsNullOrWhiteSpace(jobType))
            {
                return true;
            }

            var normalized = NormalizePropertyName(jobType);
            return normalized is "remotemedia" or "remotevideo" or "video" or "mediaplay" or "playmedia" or "videoplay" or "media";
        }

        private static string? TrimToNull(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return text.Trim();
        }

        private void RegisterJobId(string? jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return;
            }
            // 之前的实现会记录 JobId 并阻止重复任务触发播放，
            // 这里保留方法用于后续可能的扩展，但不再做去重处理。
        }


        private RemoteMediaInfo? BuildMediaInfo(string jobId, JsonElement? mediaElement, JsonElement root, string? directMediaUrl)
        {
            string? accessibleUrl = null;
            string? downloadUrl = null;
            string? coverUrl = null;
            string? fileHash = null;
            long? fileSize = null;
            string? mediaId = null;

            if (mediaElement.HasValue)
            {
                var media = mediaElement.Value;

                if (media.ValueKind == JsonValueKind.Object)
                {
                    accessibleUrl = TrimToNull(GetString(media, "accessibleUrl", "accessible_url", "url", "mediaUrl", "media_url", "playUrl", "play_url"));
                    downloadUrl = TrimToNull(GetString(media, "downloadUrl", "download_url", "url"));
                    coverUrl = TrimToNull(GetString(media, "coverUrl", "cover_url", "poster", "thumbnail"));
                    fileHash = TrimToNull(GetString(media, "fileHash", "file_hash", "md5", "checksum"));
                    fileSize = GetInt64(media, "fileSize", "file_size", "size");
                    mediaId = TrimToNull(GetString(media, "mediaId", "media_id", "id", "name"));
                }
                else if (media.ValueKind == JsonValueKind.String)
                {
                    var url = TrimToNull(media.GetString());
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        accessibleUrl ??= url;
                        downloadUrl ??= url;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(directMediaUrl))
            {
                accessibleUrl ??= directMediaUrl;
                downloadUrl ??= directMediaUrl;
            }

            accessibleUrl ??= TrimToNull(GetString(root, "accessibleUrl", "accessible_url", "url", "mediaUrl", "media_url", "playUrl", "play_url"));
            downloadUrl ??= TrimToNull(GetString(root, "downloadUrl", "download_url", "url"));
            coverUrl ??= TrimToNull(GetString(root, "coverUrl", "cover_url", "poster", "thumbnail"));
            fileHash ??= TrimToNull(GetString(root, "fileHash", "file_hash", "md5", "checksum"));
            fileSize ??= GetInt64(root, "fileSize", "file_size", "size");
            mediaId ??= TrimToNull(GetString(root, "mediaId", "media_id", "id", "name"));

            if (string.IsNullOrWhiteSpace(accessibleUrl) && string.IsNullOrWhiteSpace(downloadUrl))
            {
                return null;
            }

            return new RemoteMediaInfo
            {
                AccessibleUrl = accessibleUrl,
                DownloadUrl = downloadUrl,
                CoverUrl = coverUrl,
                FileHash = fileHash,
                FileSize = fileSize,
                MediaId = mediaId,
                JobId = jobId
            };
        }

        private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] names)
        {
            foreach (var property in element.EnumerateObject())
            {
                foreach (var candidate in names)
                {
                    if (IsPropertyMatch(property.Name, candidate))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private static string? GetString(JsonElement element, params string[] names)
        {
            if (!TryGetProperty(element, out var value, names))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                _ => null
            };
        }

        private static long? GetInt64(JsonElement element, params string[] names)
        {
            if (!TryGetProperty(element, out var value, names))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.Number)
            {
                if (value.TryGetInt64(out var number))
                {
                    return number;
                }

                if (value.TryGetDouble(out var dbl))
                {
                    return (long)dbl;
                }
            }
            else if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }

            return null;
        }

        private static bool IsPropertyMatch(string actualName, string candidate)
        {
            return NormalizePropertyName(actualName) == NormalizePropertyName(candidate);
        }

        private static string NormalizePropertyName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(name.Length);
            foreach (var ch in name)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(char.ToLowerInvariant(ch));
                }
            }

            return builder.ToString();
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

                // 收到订阅消息时弹窗显示内容，便于测试。
                //MessageBox.Show(text, "MQTT 测试消息", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            if (dispatcher.CheckAccess())
            {
                ShowDialog();
            }
            else
            {
                dispatcher.BeginInvoke(ShowDialog);
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
