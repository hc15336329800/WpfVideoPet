using System;

namespace WpfVideoPet.model
{
    /// <summary>
    /// MQTT 下发的远程媒体任务实体，保留以兼容旧的播放与缓存逻辑。
    /// </summary>
    public sealed class RemoteMediaTask
    {
        /// <summary>
        /// 使用基本的任务信息与媒体描述初始化实例。
        /// </summary>
        /// <param name="jobId">任务唯一标识。</param>
        /// <param name="scheduleTime">计划执行时间。</param>
        /// <param name="jobStatus">任务状态文本。</param>
        /// <param name="clientId">下发端标识。</param>
        /// <param name="topic">来源主题。</param>
        /// <param name="timestamp">时间戳。</param>
        /// <param name="media">媒体描述对象。</param>
        public RemoteMediaTask(string jobId, string? scheduleTime, string? jobStatus, string? clientId, string? topic, long? timestamp, RemoteMediaInfo media)
        {
            JobId = jobId ?? throw new ArgumentNullException(nameof(jobId));
            ScheduleTime = scheduleTime;
            JobStatus = jobStatus;
            ClientId = clientId;
            Topic = topic;
            Timestamp = timestamp;
            Media = media ?? throw new ArgumentNullException(nameof(media));
        }

        /// <summary>
        /// 获取任务 ID。
        /// </summary>
        public string JobId { get; } // 任务标识

        /// <summary>
        /// 获取调度时间。
        /// </summary>
        public string? ScheduleTime { get; } // 调度时间

        /// <summary>
        /// 获取任务状态。
        /// </summary>
        public string? JobStatus { get; } // 任务状态

        /// <summary>
        /// 获取下发端标识。
        /// </summary>
        public string? ClientId { get; } // 客户端标识

        /// <summary>
        /// 获取来源主题。
        /// </summary>
        public string? Topic { get; } // 来源主题

        /// <summary>
        /// 获取时间戳。
        /// </summary>
        public long? Timestamp { get; } // 时间戳

        /// <summary>
        /// 获取媒体描述对象。
        /// </summary>
        public RemoteMediaInfo Media { get; } // 媒体信息
    }

    /// <summary>
    /// 媒体资源的描述信息。
    /// </summary>
    public sealed class RemoteMediaInfo
    {
        /// <summary>
        /// 封面图地址。
        /// </summary>
        public string? CoverUrl { get; set; } // 封面地址

        /// <summary>
        /// 媒体文件大小。
        /// </summary>
        public long? FileSize { get; set; } // 文件大小

        /// <summary>
        /// 下载地址。
        /// </summary>
        public string? DownloadUrl { get; set; } // 下载地址

        /// <summary>
        /// 文件哈希。
        /// </summary>
        public string? FileHash { get; set; } // 文件哈希

        /// <summary>
        /// 可直接访问的地址。
        /// </summary>
        public string? AccessibleUrl { get; set; } // 可访问地址

        /// <summary>
        /// 媒体 ID。
        /// </summary>
        public string? MediaId { get; set; } // 媒体标识

        /// <summary>
        /// 任务 ID（部分下发会附带）。
        /// </summary>
        public string? JobId { get; set; } // 关联任务
    }
}