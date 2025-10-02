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
                error = "视频页地址配置为空，请在 webrtcsettings.json 中正确配置。";
                return false;
            }

            error = null;
            return true;
        }
    }
}