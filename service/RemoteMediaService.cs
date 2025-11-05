using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WpfVideoPet;
using WpfVideoPet.model;
using WpfVideoPet.mqtt;
using static WpfVideoPet.mqtt.MqttCoreService;

namespace WpfVideoPet.service
{
    /// <summary>
    /// 负责监听 MQTT 下发的远程媒体任务，解析 JSON 载荷并向上层发布业务事件。
    /// </summary>
    public sealed class RemoteMediaService : IAsyncDisposable
    {
        private readonly MqttCoreService _bridge; // MQTT 核心桥接
        private readonly EventHandler<MqttBridgeMessage> _messageHandler; // MQTT 消息回调
        private readonly JsonSerializerOptions _jsonOptions; // JSON 解析配置
        public event EventHandler<RemoteMediaReceivedEventArgs>? RemoteMediaReceived; // 解析后的远程媒体任务事件

        private bool _started; // 是否已启动
        private bool _disposed; // 释放状态

        // [常量] 远程媒体任务默认主题
        private const string DefaultRemoteMediaTopic = "ts_lanmao001"; // 默认远程媒体 Topic


        //*************************************初始化*********************************************

        /// <summary>
        /// 初始化远程媒体服务，复用外部提供的 MQTT 桥接实例，并准备 JSON 解析配置。
        /// </summary>
        /// <param name="bridge">用于维持连接的 MQTT 桥接实例。</param>
        public RemoteMediaService(MqttCoreService bridge)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _messageHandler = OnBridgeMessageReceived;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }

        //*************************************接收消息*********************************************


        /// <summary>
        /// 当 MQTT 桥收到消息时触发，尝试解析远程媒体任务并向上层分发。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="message">桥接推送的 MQTT 消息。</param>
        private void OnBridgeMessageReceived(object? sender, MqttBridgeMessage message)
        {
            if (_disposed) return;

            var payloadText = Encoding.UTF8.GetString(message.Payload.Span); // 消息内容文本
            AppLogger.Info($"收到 MQTT 消息 -> Topic: {message.Topic}, 内容: {payloadText}");

            if (TryParseRemoteMediaTask(payloadText, message.Topic, out var task, out var jobType))
            {
                var args = new RemoteMediaReceivedEventArgs(task!, payloadText, jobType, message); // 解析事件参数
                RemoteMediaReceived?.Invoke(this, args);
            }
            else
            {
                AppLogger.Warn($"MQTT 消息未能解析为远程媒体任务，Topic: {message.Topic}");
            }
        }


        //*************************************发送消息*********************************************

        /// <summary>
        /// 向默认主题发送调试文本，可用于联通性测试。
        /// </summary>
        public Task SendHelloWorldTo118Async(CancellationToken cancellationToken = default)
        {
            return SendStringAsync("helloworld", topic: DefaultRemoteMediaTopic, retain: false, cancellationToken);
        }


        //*************************************封装发送方法*********************************************


        /// <summary>
        /// 【向主题发送】发送原始二进制数据，不限制消息长度。
        /// </summary>
        /// <param name="payload">任意长度的原始数据。</param>
        /// <param name="topic">可选的目标主题。</param>
        /// <param name="retain">是否保留消息。</param>
        /// <param name="cancellationToken">外部取消令牌。</param>
        public Task SendAsync(ReadOnlyMemory<byte> payload, string? topic = null, bool retain = false, CancellationToken cancellationToken = default)
        {
            return _bridge.SendAsync(payload, topic, retain, cancellationToken);
        }

        /// <summary>
        /// 【向主题发送】将字符串转换为字节后发送，保持内容完整。
        /// </summary>
        /// <param name="text">待发送的字符串。</param>
        /// <param name="topic">可选的目标主题。</param>
        /// <param name="retain">是否保留消息。</param>
        /// <param name="cancellationToken">外部取消令牌。</param>
        public Task SendStringAsync(string text, string? topic = null, bool retain = false, CancellationToken cancellationToken = default)
        {
            return _bridge.SendStringAsync(text, null, topic, retain, cancellationToken);
        }


        //**********************************************************************************


        /// <summary>
        /// 启动任务服务，确保桥接已连接并挂载消息回调。
        /// - 并不会自己启动 MQTT 客户端连接，是吧自己的“业务逻辑回调”挂载到这个主服务上。
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



        //************************************ 视频下发业务 **********************************************


        /// <summary>
        /// 尝试将 MQTT 文本载荷解析为远程媒体任务对象，失败时返回 false。
        /// </summary>
        /// <param name="payloadJson">MQTT 消息中的原始 JSON 字符串。</param>
        /// <param name="fallbackTopic">当消息体缺少 Topic 字段时使用的 Topic。</param>
        /// <param name="task">解析成功后的远程媒体任务。</param>
        /// <param name="jobType">任务类型标识。</param>
        /// <returns>解析成功返回 true，否则返回 false。</returns>
        private bool TryParseRemoteMediaTask(string? payloadJson, string fallbackTopic, out RemoteMediaTask? task, out string? jobType)
        {
            task = null;
            jobType = null;

            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                AppLogger.Warn("MQTT 消息体为空，无法解析远程媒体任务。");
                return false;
            }

            try
            {
                var envelope = JsonSerializer.Deserialize<RemoteMediaEnvelope>(payloadJson, _jsonOptions); // 反序列化包裹对象
                if (envelope == null)
                {
                    AppLogger.Warn("反序列化远程媒体任务失败，结果为空。");
                    return false;
                }

                jobType = envelope.JobType;
                if (!string.IsNullOrWhiteSpace(jobType) && !string.Equals(jobType, "REMOTE_MEDIA", StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Warn($"忽略非远程媒体任务，JobType: {jobType}");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(envelope.JobId))
                {
                    AppLogger.Warn("远程媒体任务缺少 JobId 字段。");
                    return false;
                }

                if (envelope.Media == null)
                {
                    AppLogger.Warn("远程媒体任务缺少媒体描述。");
                    return false;
                }

                var mediaInfo = new RemoteMediaInfo
                {
                    CoverUrl = envelope.Media.CoverUrl,
                    FileSize = envelope.Media.FileSize,
                    DownloadUrl = envelope.Media.DownloadUrl,
                    FileHash = envelope.Media.FileHash,
                    AccessibleUrl = envelope.Media.AccessibleUrl,
                    MediaId = envelope.Media.MediaId,
                    JobId = string.IsNullOrWhiteSpace(envelope.Media.JobId) ? envelope.JobId : envelope.Media.JobId
                }; // 媒体信息

                var topic = string.IsNullOrWhiteSpace(envelope.Topic) ? fallbackTopic : envelope.Topic;
                task = new RemoteMediaTask(
                    envelope.JobId!,
                    envelope.ScheduleTime,
                    envelope.JobStatus,
                    envelope.ClientId,
                    topic,
                    envelope.Timestamp,
                    mediaInfo);

                AppLogger.Info($"成功解析远程媒体任务，JobId: {task.JobId}, Topic: {task.Topic}");
                return true;
            }
            catch (JsonException jsonEx)
            {
                AppLogger.Error(jsonEx, "解析远程媒体任务 JSON 时发生异常。");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "构造远程媒体任务时发生异常。");
            }

            task = null;
            return false;
        }

        /// <summary>
        /// 表示远程媒体任务的 JSON 包裹结构，用于反序列化。
        /// </summary>
        private sealed class RemoteMediaEnvelope
        {
            public string? JobId { get; set; } // 任务 ID
            public string? ScheduleTime { get; set; } // 计划执行时间
            public string? JobStatus { get; set; } // 任务状态
            public string? ClientId { get; set; } // 客户端标识
            public string? Topic { get; set; } // 来源主题
            public long? Timestamp { get; set; } // 时间戳
            public string? JobType { get; set; } // 任务类型
            public RemoteMediaEnvelopeMedia? Media { get; set; } // 媒体信息
        }

        /// <summary>
        /// 媒体字段的临时结构。
        /// </summary>
        private sealed class RemoteMediaEnvelopeMedia
        {
            public string? CoverUrl { get; set; } // 封面地址
            public long? FileSize { get; set; } // 文件大小
            public string? DownloadUrl { get; set; } // 下载地址
            public string? FileHash { get; set; } // 哈希
            public string? AccessibleUrl { get; set; } // 可访问地址
            public string? MediaId { get; set; } // 媒体 ID
            public string? JobId { get; set; } // 媒体关联任务 ID
        }
    }

    /// <summary>
    /// 远程媒体服务解析后的任务事件参数，包含原始 MQTT 消息与 JSON。
    /// </summary>
    public sealed class RemoteMediaReceivedEventArgs : EventArgs
    {
        public RemoteMediaReceivedEventArgs(RemoteMediaTask task, string rawJson, string? jobType, MqttBridgeMessage rawMessage)
        {
            Task = task ?? throw new ArgumentNullException(nameof(task));
            RawJson = rawJson ?? string.Empty;
            JobType = jobType; // 任务类型
            RawMessage = rawMessage ?? throw new ArgumentNullException(nameof(rawMessage));
        }

        public RemoteMediaTask Task { get; } // 解析后的任务

        public string RawJson { get; } // 原始 JSON 字符串

        public string? JobType { get; } // 任务类型

        public MqttBridgeMessage RawMessage { get; } // 原始 MQTT 消息
    }
}