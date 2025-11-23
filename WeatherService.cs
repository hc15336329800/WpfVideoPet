using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace WpfVideoPet.service
{
    /// <summary>
    /// 封装天气数据结构，便于在界面和日志中使用类型化信息。
    /// </summary>
    public sealed class WeatherInfo
    {
        public string City { get; }

        public string Description { get; }

        public double TemperatureCelsius { get; }

        public WeatherInfo(string city, string description, double temperatureCelsius)
        {
            City = city;
            Description = description;
            TemperatureCelsius = temperatureCelsius;
        }
    }

    /// <summary>
    /// 使用公共接口获取天气信息的服务，固定返回摄氏度温度和文字描述。
    /// </summary>
    public sealed class WeatherService
    {
        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        /// <summary>
        /// Open-Meteo 天气代码到中文描述的映射表。
        /// 参考官方 weathercode 说明：https://open-meteo.com/en/docs
        /// </summary>
        private static readonly IReadOnlyDictionary<int, string> WeatherCodeMap = new Dictionary<int, string>
        {
            { 0, "晴朗" },
            { 1, "大部晴朗" },
            { 2, "多云" },
            { 3, "阴" },
            { 45, "雾" },
            { 48, "雾并有霜" },
            { 51, "小毛毛雨" },
            { 53, "中毛毛雨" },
            { 55, "大毛毛雨" },
            { 56, "小冻毛毛雨" },
            { 57, "大冻毛毛雨" },
            { 61, "小雨" },
            { 63, "中雨" },
            { 65, "大雨" },
            { 66, "小冻雨" },
            { 67, "大冻雨" },
            { 71, "小雪" },
            { 73, "中雪" },
            { 75, "大雪" },
            { 77, "雪粒" },
            { 80, "小阵雨" },
            { 81, "中阵雨" },
            { 82, "大阵雨" },
            { 85, "小阵雪" },
            { 86, "大阵雪" },
            { 95, "雷阵雨" },
            { 96, "雷阵雨伴小冰雹" },
            { 99, "雷阵雨伴大冰雹" }
        };
        /// <summary>
        /// 拉取目标城市的天气信息，返回包含城市、天气描述和摄氏度温度的结构。
        /// </summary>
        public async Task<WeatherInfo?> GetWeatherAsync(string cityName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(cityName))
            {
                AppLogger.Warn("获取天气失败：城市名称为空。");
                return null;
            }

            // 内蒙准格尔旗大路新区（近鄂尔多斯）的近似坐标，避免中文城市名解析失败。
            const double latitude = 39.87;
            const double longitude = 110.03;

            var requestUrl = "https://api.open-meteo.com/v1/forecast"
                             + $"?latitude={latitude:F2}&longitude={longitude:F2}"
                             + "&current_weather=true&timezone=auto";

            AppLogger.Info($"开始请求 Open-Meteo 天气接口：{requestUrl}，城市显示名称：{cityName}");

            try
            {
                using var response = await HttpClient.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    AppLogger.Warn($"天气接口返回非成功状态码：{(int)response.StatusCode} {response.StatusCode}。");
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!document.RootElement.TryGetProperty("current_weather", out var currentWeather))
                {
                    AppLogger.Warn("天气接口响应缺少 current_weather 字段。");
                    return null;
                }

                if (!currentWeather.TryGetProperty("temperature", out var temperatureElement))
                {
                    AppLogger.Warn("天气接口响应缺少温度字段 temperature。");
                    return null;
                }

                if (!currentWeather.TryGetProperty("weathercode", out var weatherCodeElement))
                {
                    AppLogger.Warn("天气接口响应缺少天气代码字段 weathercode。");
                    return null;
                }

                var temperature = temperatureElement.GetDouble();
                var weatherCode = weatherCodeElement.GetInt32();
                var description = WeatherCodeMap.TryGetValue(weatherCode, out var mapped)
                    ? mapped
                    : $"代码 {weatherCode}";

                AppLogger.Info($"天气接口解析成功：{cityName} weathercode={weatherCode} 描述={description} 温度={temperature}℃。");
                return new WeatherInfo(cityName, description, temperature);
            }
            catch (TaskCanceledException ex) when (ex.CancellationToken == cancellationToken)
            {
                AppLogger.Warn("天气请求被取消。");
                return null;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "天气请求过程出现异常。");
                return null;
            }
        }
    }
}