using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.IO.Ports;

namespace WpfVideoPet
{
    public sealed class AppConfig
    {
        public string Role { get; set; } = "client";
        public string Room { get; set; } = "default";
        public string SignalServer { get; set; } = string.Empty; 
        public string? OperatorToken { get; set; }
        public string PageUrl { get; set; } = "https://localhost";
        public float OverlayAnimationFrameRate { get; set; } = 30f; // 叠加层动画目标帧率（0 表示按系统节奏）
        public OverlayRenderConfig OverlayRender { get; } = new(); // 叠加层渲染配置

        public MqttConfig Mqtt { get; } = new();
        public AudioDuckingConfig AudioDucking { get; } = new();
        public WakeConfig Wake { get; } = new();
        /// <summary>
        /// 西门子 PLC 配置段。
        /// </summary>
        public PlcConfig Plc { get; } = new();

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
                    var rawText = File.ReadAllText(configPath);
                    var sanitized = RemoveJsonComments(rawText);
                    using var doc = JsonDocument.Parse(sanitized);

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
                    if (root.TryGetProperty("overlayAnimationFrameRate", out var frameRateElement) && frameRateElement.TryGetDouble(out var frameRateValue))
                    {
                        var clamped = Math.Clamp((float)frameRateValue, 0f, 120f);
                        config.OverlayAnimationFrameRate = clamped;
                    }
                    if (root.TryGetProperty("overlayRender", out var overlayRenderElement))
                    {
                        ApplyOverlayRenderConfig(overlayRenderElement, config.OverlayRender);
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
                            else
                            {
                                mqtt.ClientId = "lanmao001";
                            }
                        }

                        if (mqttElement.TryGetProperty("topic", out var topicElement))
                        {
                            var value = topicElement.GetString();
                            mqtt.DefaultTopic = string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
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
                        if (root.TryGetProperty("plc", out var plcElement) && plcElement.ValueKind == JsonValueKind.Object)
                        {
                            var plc = config.Plc;

                            if (plcElement.TryGetProperty("enabled", out var plcEnabledElement) && plcEnabledElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            {
                                plc.Enabled = plcEnabledElement.GetBoolean();
                            }

                            if (plcElement.TryGetProperty("cpuType", out var cpuTypeElement))
                            {
                                var value = cpuTypeElement.GetString();
                                if (!string.IsNullOrWhiteSpace(value))
                                {
                                    plc.CpuType = value.Trim();
                                }
                            }

                            if (plcElement.TryGetProperty("ipAddress", out var ipElement))
                            {
                                var value = ipElement.GetString();
                                if (!string.IsNullOrWhiteSpace(value))
                                {
                                    plc.IpAddress = value.Trim();
                                }
                            }

                            if (plcElement.TryGetProperty("rack", out var rackElement) && rackElement.TryGetInt32(out var rackValue))
                            {
                                plc.Rack = Math.Max(0, rackValue);
                            }

                            if (plcElement.TryGetProperty("slot", out var slotElement) && slotElement.TryGetInt32(out var slotValue))
                            {
                                plc.Slot = Math.Max(0, slotValue);
                            }

                            if (plcElement.TryGetProperty("pollingIntervalMilliseconds", out var intervalElement) && intervalElement.TryGetInt32(out var intervalValue))
                            {
                                plc.PollingIntervalMilliseconds = Math.Max(100, intervalValue);
                            }

                            if (plcElement.TryGetProperty("statusPublishTopic", out var statusTopicElement))
                            {
                                var value = statusTopicElement.GetString();
                                if (!string.IsNullOrWhiteSpace(value))
                                {
                                    plc.StatusPublishTopic = value.Trim();
                                }
                            }

                            if (plcElement.TryGetProperty("controlSubscribeTopic", out var controlTopicElement))
                            {
                                var value = controlTopicElement.GetString();
                                if (!string.IsNullOrWhiteSpace(value))
                                {
                                    plc.ControlSubscribeTopic = value.Trim();
                                }
                            }

                            if (plcElement.TryGetProperty("statusArea", out var statusAreaElement) && statusAreaElement.ValueKind == JsonValueKind.Object)
                            {
                                ApplyAreaConfig(statusAreaElement, plc.StatusArea);
                            }

                            if (plcElement.TryGetProperty("controlArea", out var controlAreaElement) && controlAreaElement.ValueKind == JsonValueKind.Object)
                            {
                                ApplyAreaConfig(controlAreaElement, plc.ControlArea);
                            }
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
                            if (duckingElement.TryGetProperty("targetVolumePercentage", out var targetVolumeElement) && targetVolumeElement.TryGetInt32(out var targetVolume))
                            {
                                ducking.TargetVolumePercentage = Math.Clamp(targetVolume, 0, 100);
                            }
                        }
                    }

                    if (root.TryGetProperty("wake", out var wakeElement) && wakeElement.ValueKind == JsonValueKind.Object)
                    {
                        if (wakeElement.TryGetProperty("sdkDirectory", out var sdkElement))
                        {
                            var value = sdkElement.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                config.Wake.SdkDirectory = value.Trim();
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
        /// <summary>
        /// 从 JSON 元素解析叠加层渲染配置，兼容对象与“宽x高”字符串两种写法。
        /// </summary>
        private static void ApplyOverlayRenderConfig(JsonElement element, OverlayRenderConfig target)
        {
            if (target == null)
            {
                return;
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("width", out var widthElement) && widthElement.TryGetInt32(out var widthValue))
                {
                    target.Width = Math.Max(1, widthValue);
                }

                if (element.TryGetProperty("height", out var heightElement) && heightElement.TryGetInt32(out var heightValue))
                {
                    target.Height = Math.Max(1, heightValue);
                }
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                var raw = element.GetString();
                if (TryParseResolution(raw, out var width, out var height))
                {
                    target.Width = width;
                    target.Height = height;
                }
            }
        }

        /// <summary>
        /// 尝试解析“宽x高”格式的分辨率文本，返回提取到的宽度与高度数值。
        /// </summary>
        private static bool TryParseResolution(string? value, out int width, out int height)
        {
            width = 0;
            height = 0;

            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var separators = new[] { 'x', 'X', '*', '×' };
            var parts = value.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length != 2)
            {
                return false;
            }

            if (!int.TryParse(parts[0], out var widthValue) || !int.TryParse(parts[1], out var heightValue))
            {
                return false;
            }

            width = Math.Max(1, widthValue);
            height = Math.Max(1, heightValue);
            return true;
        }
        private static string RemoveJsonComments(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return json;
            }

            var builder = new StringBuilder(json.Length);
            var inString = false;
            var inSingleLineComment = false;
            var inMultiLineComment = false;

            for (var i = 0; i < json.Length; i++)
            {
                var ch = json[i];
                var next = i + 1 < json.Length ? json[i + 1] : '\0';

                if (inSingleLineComment)
                {
                    if (ch == '\r' || ch == '\n')
                    {
                        inSingleLineComment = false;
                        builder.Append(ch);

                        if (ch == '\r' && next == '\n')
                        {
                            builder.Append(next);
                            i++;
                        }
                    }

                    continue;
                }

                if (inMultiLineComment)
                {
                    if (ch == '*' && next == '/')
                    {
                        inMultiLineComment = false;
                        i++;
                    }

                    continue;
                }

                if (ch == '"' && !IsEscaped(json, i))
                {
                    inString = !inString;
                    builder.Append(ch);
                    continue;
                }

                if (!inString && ch == '/' && next == '/')
                {
                    inSingleLineComment = true;
                    i++;
                    continue;
                }

                if (!inString && ch == '/' && next == '*')
                {
                    inMultiLineComment = true;
                    i++;
                    continue;
                }

                builder.Append(ch);
            }

            return builder.ToString();
        }
        /// <summary>
        /// 从 JSON 片段中提取 DB 区域配置并写入目标对象。
        /// </summary>
        /// <param name="element">包含 dbNumber、startByte、byteLength 字段的 JSON 元素。</param>
        /// <param name="target">待更新的区域配置实例。</param>
        private static void ApplyAreaConfig(JsonElement element, PlcAreaConfig target)
        {
            if (element.TryGetProperty("dbNumber", out var dbElement) && dbElement.TryGetInt32(out var dbValue))
            {
                target.DbNumber = Math.Max(1, dbValue);
            }

            if (element.TryGetProperty("startByte", out var startElement) && startElement.TryGetInt32(out var startValue))
            {
                target.StartByte = Math.Max(0, startValue);
            }

            if (element.TryGetProperty("byteLength", out var lengthElement) && lengthElement.TryGetInt32(out var lengthValue))
            {
                target.ByteLength = Math.Max(1, lengthValue);
            }
        }


        private static bool IsEscaped(string text, int index)
        {
            var backslashCount = 0;

            for (var i = index - 1; i >= 0 && text[i] == '\\'; i--)
            {
                backslashCount++;
            }

            return (backslashCount & 1) == 1;
        }
    }
}
/// <summary>
/// 定义叠加层的渲染输出尺寸，允许通过配置文件调整宽高。
/// </summary>
public sealed class OverlayRenderConfig
{
    public int Width { get; set; } = 1080; // 渲染目标宽度
    public int Height { get; set; } = 1920; // 渲染目标高度
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
    /// 默认值为 "lanmao001"，便于开箱即用地测试当前车辆。
    /// </summary>
    public string ClientId { get; set; } = "lanmao001";

    /// <summary>
    /// 默认的 MQTT 主题，收发消息均会使用该主题。
    /// 为空时会退回到基于 ClientId 的 ts_/tr_ 主题组合。
    /// </summary>
    public string? DefaultTopic { get; set; } = "ts_lanmao001";


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
    public string DownlinkTopic
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DefaultTopic))
            {
                return DefaultTopic!;
            }

            return string.IsNullOrWhiteSpace(ClientId) ? string.Empty : $"ts_{ClientId}";
        }
    }

    /// <summary>
    /// 设备上报执行结果的 Topic（tr_{clientId}）。
    /// </summary>
    public string UplinkTopic
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DefaultTopic))
            {
                return DefaultTopic!;
            }

            return string.IsNullOrWhiteSpace(ClientId) ? string.Empty : $"tr_{ClientId}";
        }
    }
}


/// <summary>
/// 描述西门子 PLC 的基础配置，包含连接参数、主题与数据区信息。
/// </summary>
public sealed class PlcConfig
{
    /// <summary>
    /// 是否启用 PLC 相关逻辑。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 目标 PLC 的 CPU 类型字符串，例如 S71200。
    /// </summary>
    public string CpuType { get; set; } = "S71200";

    /// <summary>
    /// PLC 的 IP 地址。
    /// </summary>
    public string IpAddress { get; set; } = "192.168.0.1";

    /// <summary>
    /// PLC 机架号。
    /// </summary>
    public int Rack { get; set; } = 0;

    /// <summary>
    /// PLC 槽位号。
    /// </summary>
    public int Slot { get; set; } = 1;

    /// <summary>
    /// 轮询周期（毫秒）。
    /// </summary>
    public int PollingIntervalMilliseconds { get; set; } = 3000;

    /// <summary>
    /// PLC 轮询结果发布的 MQTT 主题。
    /// </summary>
    public string StatusPublishTopic { get; set; } = "plc/status/777";

    /// <summary>
    /// PLC 控制指令订阅的 MQTT 主题。
    /// </summary>
    public string ControlSubscribeTopic { get; set; } = "plc/control/888";

    /// <summary>
    /// 轮询读取的 DB 区域信息。
    /// </summary>
    public PlcAreaConfig StatusArea { get; } = new();

    /// <summary>
    /// 可写入的 DB 区域信息。
    /// </summary>
    public PlcAreaConfig ControlArea { get; } = new();
}

/// <summary>
/// 代表 PLC 数据块中的一个区域配置。
/// </summary>
public sealed class PlcAreaConfig
{
    /// <summary>
    /// 数据块编号。
    /// </summary>
    public int DbNumber { get; set; } = 100;

    /// <summary>
    /// 起始字节偏移。
    /// </summary>
    public int StartByte { get; set; } = 0;

    /// <summary>
    /// 读取或写入的字节长度。
    /// </summary>
    public int ByteLength { get; set; } = 3;
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
    public int RestoreDelaySeconds { get; set; } = 3;

    /// <summary>
    /// 压制阶段的目标音量百分比，0 表示静音，100 表示保持原音量。
    /// </summary>
    public int TargetVolumePercentage { get; set; } = 10;

    public TimeSpan RestoreDelay => TimeSpan.FromSeconds(Math.Max(0, RestoreDelaySeconds));
}

public sealed class WakeConfig
{
    /// <summary>
    /// 可选的 Aikit SDK 源目录，支持包含中文及空格路径。
    /// 若指定，将在运行时自动复制缺失的原生文件到程序目录。
    /// </summary>
    public string? SdkDirectory { get; set; }
}