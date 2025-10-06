using System;
using System.IO;
using System.Text.Json;

namespace WpfVideoPet
{
    public sealed class AppConfig
    {
        public string Role { get; set; } = "client";
        public string Room { get; set; } = "default";
        public string SignalServer { get; set; } = string.Empty;
        public string? OperatorToken { get; set; }
        public string PageUrl { get; set; } = "https://localhost";

        public MqttConfig Mqtt { get; } = new();
        public AudioDuckingConfig AudioDucking { get; } = new();

        public bool IsOperator => string.Equals(Role, "operator", StringComparison.OrdinalIgnoreCase);

        public static AppConfig Load(string? roleOverride = null)
        {
            var config = new AppConfig();
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var configPath = Path.Combine(baseDir, "webrtcsettings.json");

            if (File.Exists(configPath))
            {
                try
                {
                    using var stream = File.OpenRead(configPath);
                    using var doc = JsonDocument.Parse(stream);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("role", out var roleElement))
                    {
                        var value = roleElement.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            config.Role = value.Trim();
                        }
                    }

                    if (root.TryGetProperty("room", out var roomElement))
                    {
                        var value = roomElement.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            config.Room = value.Trim();
                        }
                    }

                    if (root.TryGetProperty("signalServer", out var serverElement))
                    {
                        var value = serverElement.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            config.SignalServer = value.Trim();
                        }
                    }

                    if (root.TryGetProperty("operatorToken", out var tokenElement))
                    {
                        config.OperatorToken = tokenElement.GetString();
                    }

                    if (root.TryGetProperty("pageUrl", out var pageElement))
                    {
                        var value = pageElement.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            config.PageUrl = value.Trim();
                        }
                    }

                    if (root.TryGetProperty("mqtt", out var mqttElement) && mqttElement.ValueKind == JsonValueKind.Object)
                    {
                        var mqtt = config.Mqtt;

                        if (mqttElement.TryGetProperty("enabled", out var enabledElement) && enabledElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            mqtt.Enabled = enabledElement.GetBoolean();
                        }

                        if (mqttElement.TryGetProperty("serverUri", out var serverUriElement))
                        {
                            var value = serverUriElement.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                mqtt.ServerUri = value.Trim();
                            }
                        }

                        if (mqttElement.TryGetProperty("clientId", out var clientIdElement))
                        {
                            var value = clientIdElement.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                mqtt.ClientId = value.Trim();
                            }
                        }

                        if (mqttElement.TryGetProperty("username", out var usernameElement))
                        {
                            mqtt.Username = usernameElement.GetString();
                        }

                        if (mqttElement.TryGetProperty("password", out var passwordElement))
                        {
                            mqtt.Password = passwordElement.GetString();
                        }

                        if (mqttElement.TryGetProperty("qos", out var qosElement) && qosElement.TryGetInt32(out var qosValue))
                        {
                            mqtt.Qos = Math.Clamp(qosValue, 0, 2);
                        }

                        if (mqttElement.TryGetProperty("cleanSession", out var cleanSessionElement) && cleanSessionElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            mqtt.CleanSession = cleanSessionElement.GetBoolean();
                        }

                        if (mqttElement.TryGetProperty("keepAliveInterval", out var keepAliveElement) && keepAliveElement.TryGetInt32(out var keepAliveValue))
                        {
                            mqtt.KeepAliveInterval = Math.Max(0, keepAliveValue);
                        }

                        if (mqttElement.TryGetProperty("connectionTimeout", out var connectionTimeoutElement) && connectionTimeoutElement.TryGetInt32(out var timeoutValue))
                        {
                            mqtt.ConnectionTimeout = Math.Max(0, timeoutValue);
                        }
                    }

                    if (root.TryGetProperty("audio", out var audioElement) && audioElement.ValueKind == JsonValueKind.Object)
                    {
                        if (audioElement.TryGetProperty("ducking", out var duckingElement) && duckingElement.ValueKind == JsonValueKind.Object)
                        {
                            var ducking = config.AudioDucking;

                            if (duckingElement.TryGetProperty("enabled", out var duckEnabledElement) && duckEnabledElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            {
                                ducking.Enabled = duckEnabledElement.GetBoolean();
                            }

                            if (duckingElement.TryGetProperty("restoreDelaySeconds", out var restoreDelayElement) && restoreDelayElement.TryGetInt32(out var restoreDelaySeconds))
                            {
                                ducking.RestoreDelaySeconds = Math.Max(0, restoreDelaySeconds);
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // ignore malformed json and fallback to defaults
                }
                catch (IOException)
                {
                    // ignore IO issues and fallback to defaults
                }
            }

            if (!string.IsNullOrWhiteSpace(roleOverride))
            {
                config.Role = roleOverride.Trim();
            }

            if (string.IsNullOrWhiteSpace(config.Role))
            {
                config.Role = "client";
            }

            if (string.IsNullOrWhiteSpace(config.Room))
            {
                config.Room = "default";
            }

            if (string.IsNullOrWhiteSpace(config.PageUrl))
            {
                config.PageUrl = "https://localhost";
            }

            var localPagePath = Path.Combine(baseDir, "Assets", "client2.html");
            if (File.Exists(localPagePath))
            {
                config.PageUrl = new Uri(localPagePath).AbsoluteUri;
            }

            return config;
        }

        public bool TryValidate(out string? error)
        {
            if (string.IsNullOrWhiteSpace(SignalServer))
            {
                error = "SIGNAL_SERVER 配置为空，请在 webrtcsettings.json 中正确配置。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(Room))
            {
                error = "房间号配置为空，请在 webrtcsettings.json 中正确配置。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(PageUrl))
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var localPagePath = Path.Combine(baseDir, "Assets", "client2.html");
                if (File.Exists(localPagePath))
                {
                    PageUrl = new Uri(localPagePath).AbsoluteUri;
                }
                else
                {
                    error = "找不到本地视频页面资源，请确认 Assets/client2.html 是否存在。";
                    return false;
                }
            }

            error = null;
            return true;
        }
    }
}

public sealed class MqttConfig
{
    /// <summary>
    /// 是否启用 MQTT 功能。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 服务器地址（例如 tcp://host:port）。
    /// </summary>
    public string ServerUri { get; set; } = string.Empty;

    /// <summary>
    /// 平台为终端分配的 ClientId，用于连接与 Topic 拼接。
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>
    /// 消息服务质量，允许 0/1/2。
    /// </summary>
    public int Qos { get; set; } = 1;

    public bool CleanSession { get; set; } = false;

    /// <summary>
    /// 心跳秒数。
    /// </summary>
    public int KeepAliveInterval { get; set; } = 60;

    /// <summary>
    /// 连接超时时间秒数。
    /// </summary>
    public int ConnectionTimeout { get; set; } = 10;

    /// <summary>
    /// 设备接收任务的 Topic（ts_{clientId}）。
    /// </summary>
    public string DownlinkTopic => string.IsNullOrWhiteSpace(ClientId) ? string.Empty : $"ts_{ClientId}";

    /// <summary>
    /// 设备上报执行结果的 Topic（tr_{clientId}）。
    /// </summary>
    public string UplinkTopic => string.IsNullOrWhiteSpace(ClientId) ? string.Empty : $"tr_{ClientId}";
}

public sealed class AudioDuckingConfig
{
    /// <summary>
    /// 是否在识别到人声时压低播放器音量。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 在恢复正常音量前的等待秒数，允许 0 表示立即恢复。
    /// </summary>
    public int RestoreDelaySeconds { get; set; } = 5;

    public TimeSpan RestoreDelay => TimeSpan.FromSeconds(Math.Max(0, RestoreDelaySeconds));
}
