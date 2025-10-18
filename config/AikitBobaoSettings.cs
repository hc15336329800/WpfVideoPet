using System.IO;
using System.Text.Json;

namespace WpfVideoPet.config
{
    /// <summary>
    /// 讯飞语音识别播报服务的基础配置，负责承载接口地址与鉴权信息。
    /// </summary>
    public sealed class AikitBobaoSettings
    {
        /// <summary>
        /// 讯飞接口的 WebSocket 地址，例如 <c>wss://iat-api.xfyun.cn/v2/iat</c>。
        /// </summary>
        public string HostUrl { get; init; } = string.Empty;

        /// <summary>
        /// 控制台应用分配的 ApiKey，用于请求签名。
        /// </summary>
        public string ApiKey { get; init; } = string.Empty;

        /// <summary>
        /// 控制台应用分配的 ApiSecret，用于请求签名。
        /// </summary>
        public string ApiSecret { get; init; } = string.Empty;

        /// <summary>
        /// 控制台应用的 AppId。
        /// </summary>
        public string AppId { get; init; } = string.Empty;

        /// <summary>
        /// 从 JSON 配置文件加载播报设置，若文件不存在或字段缺失则抛出异常。
        /// </summary>
        /// <param name="filePath">配置文件的绝对路径。</param>
        /// <returns>加载完成的 <see cref="AikitBobaoSettings"/> 实例。</returns>
        /// <exception cref="FileNotFoundException">当配置文件不存在时抛出。</exception>
        /// <exception cref="InvalidOperationException">当配置内容不完整时抛出。</exception>
        public static AikitBobaoSettings Load(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"未找到 Aikit 播报配置文件: {filePath}", filePath);
            }

            using var stream = File.OpenRead(filePath);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("Xfyun", out var xfyunElement) || xfyunElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("配置文件缺少 Xfyun 节点。");
            }

            string hostUrl = xfyunElement.GetPropertyOrDefault("HostUrl");
            string apiKey = xfyunElement.GetPropertyOrDefault("ApiKey");
            string apiSecret = xfyunElement.GetPropertyOrDefault("ApiSecret");
            string appId = xfyunElement.GetPropertyOrDefault("AppId");

            if (string.IsNullOrWhiteSpace(hostUrl) || string.IsNullOrWhiteSpace(apiKey) ||
                string.IsNullOrWhiteSpace(apiSecret) || string.IsNullOrWhiteSpace(appId))
            {
                throw new InvalidOperationException("Aikit 播报配置不完整，请检查 HostUrl、ApiKey、ApiSecret 与 AppId。");
            }

            return new AikitBobaoSettings
            {
                HostUrl = hostUrl,
                ApiKey = apiKey,
                ApiSecret = apiSecret,
                AppId = appId
            };
        }
    }

    /// <summary>
    /// 针对 <see cref="JsonElement"/> 的读取扩展，便于处理不存在的属性。
    /// </summary>
    internal static class JsonElementExtensions
    {
        /// <summary>
        /// 从 JSON 对象中读取指定属性，若不存在则返回空字符串。
        /// </summary>
        /// <param name="element">当前 JSON 元素。</param>
        /// <param name="propertyName">需要读取的属性名称。</param>
        /// <returns>属性值或空字符串。</returns>
        public static string GetPropertyOrDefault(this JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString() ?? string.Empty;
            }

            return string.Empty;
        }
    }
}