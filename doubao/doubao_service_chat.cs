using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WpfVideoPet.doubao
{
    /// <summary>
    /// 豆包知识库 ServiceChat 流式聊天服务类（SSE）
    /// - 参数保持默认（与原 Program.cs 一致）
    /// - 封装消息历史、请求构造与流式解析
    /// - 通过 onDelta 回调增量输出，适合 WPF 界面层订阅
    /// </summary>
    internal class doubao_service_chat
    {
        // === 必填：API_KEY 和 服务ID（支持环境变量覆盖，与原代码一致） ===
        // DOUBAO_KB_SERVICE_ID  :  定制的服务  每一种配置都不同   有深度思考的  又快速的等
        private static string API_KEY =
            Environment.GetEnvironmentVariable("DOUBAO_KB_API_KEY")
            ?? "019d4e9a-0635-4aba-a540-462aa272941f";

        private static string SERVICE_RESOURCE_ID =
            Environment.GetEnvironmentVariable("DOUBAO_KB_SERVICE_ID")
            ?? "kb-service-37096abf1876536c";

        // === 固定接口信息（与文档/Java demo 对齐） ===
        private const string HOST = "api-knowledgebase.mlp.cn-beijing.volces.com";
        private const string SERVICE_CHAT_PATH = "/api/knowledge/service/chat";

        // JSON 选项（严格字段映射）
        private static readonly JsonSerializerOptions JsonOpt = new()
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // 简单多轮：保留 [user, assistant, user, ...] 消息历史
        private readonly List<MessageParam> _history = new();

        /// <summary>
        /// 追加一条 user 消息到历史
        /// </summary>
        public void AddUserMessage(string content)
        {
            if (!string.IsNullOrWhiteSpace(content))
                _history.Add(new MessageParam("user", content));
        }

        /// <summary>
        /// 追加一条 assistant 消息到历史
        /// </summary>
        public void AddAssistantMessage(string content)
        {
            if (!string.IsNullOrWhiteSpace(content))
                _history.Add(new MessageParam("assistant", content));
        }

        /// <summary>
        /// 清空历史
        /// </summary>
        public void ClearHistory() => _history.Clear();

        /// <summary>
        /// 单轮问答（内部自动维护历史），SSE 流式输出。
        /// onDelta: 每到一段增量文本即回调（在后台线程触发，WPF 请自行 Dispatcher.Invoke）。
        /// 返回值：本轮完整聚合的回答文本。
        /// </summary>
        public async Task<string> AskAsync(string query, Action<string>? onDelta = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return string.Empty;

            // 把 user 消息入历史
            _history.Add(new MessageParam("user", query));

            // 流式请求
            var answer = await StreamWithHistoryAsync(_history, onDelta, ct);

            // 把 assistant 回复入历史
            if (!string.IsNullOrEmpty(answer))
                _history.Add(new MessageParam("assistant", answer));

            return answer ?? string.Empty;
        }

        /// <summary>
        /// 使用调用方自带的消息历史进行流式请求（SSE），并通过 onDelta 输出增量；返回完整聚合文本。
        /// </summary>
        public async Task<string> StreamWithHistoryAsync(List<MessageParam> history, Action<string>? onDelta = null, CancellationToken ct = default)
        {
            if (history == null || history.Count == 0 || history.Last().Role != "user")
                throw new InvalidOperationException("消息历史为空，或最后一条不是 user。");

            var reqBody = BuildStreamRequest(history);
            var aggregated = new StringBuilder();

            using var http = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = System.Net.DecompressionMethods.All
            })
            { Timeout = TimeSpan.FromSeconds(240) };

            var uri = new UriBuilder("https", HOST, 443, SERVICE_CHAT_PATH).Uri;

            using var req = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(JsonSerializer.Serialize(reqBody, JsonOpt), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", API_KEY);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            req.Headers.Host = HOST;

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var raw = await resp.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.StatusCode}: {raw}");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1536 * 1024);

            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();

                var line = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(line)) continue;

                // SSE 行：以 "data:" 开头
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    var json = line.AsSpan(5).Trim().ToString();
                    if (json == "[DONE]") break;

                    try
                    {
                        var chunk = JsonSerializer.Deserialize<BaseResponse<CollectionServiceChatResponseData>>(json, JsonOpt);
                        var piece = chunk?.Data?.GeneratedAnswer;

                        if (!string.IsNullOrEmpty(piece))
                        {
                            aggregated.Append(piece);
                            onDelta?.Invoke(piece); // 增量回调（UI 侧自行切线程）
                        }

                        if (chunk?.Data?.End == true)
                            break;
                    }
                    catch
                    {
                        // 忽略单片解析错误，继续读取后续分片
                        continue;
                    }
                }
            }

            return aggregated.ToString();
        }

        // ====== 构造请求体：带历史，实现多轮 [user, assistant, user...] ======
        private static ServiceChatRequest BuildStreamRequest(List<MessageParam> history)
        {
            return new ServiceChatRequest
            {
                ServiceResourceId = SERVICE_RESOURCE_ID,
                Stream = true, // 默认流式
                Messages = history
            };
        }

        // ====== 请求/响应模型（字段名与文档/Java demo 保持一致） ======
        public sealed class ServiceChatRequest
        {
            [JsonPropertyName("service_resource_id")] public string ServiceResourceId { get; set; } = default!;
            [JsonPropertyName("stream")] public bool Stream { get; set; }
            [JsonPropertyName("messages")] public List<MessageParam> Messages { get; set; } = new();
            // 可选过滤：
            // [JsonPropertyName("query_param")] public QueryParam? QueryParam { get; set; }
        }

        public sealed class MessageParam
        {
            [JsonPropertyName("role")] public string Role { get; set; } = default!;
            [JsonPropertyName("content")] public object Content { get; set; } = default!;
            public MessageParam() { }
            public MessageParam(string role, object content) { Role = role; Content = content; }
        }

        public sealed class BaseResponse<T>
        {
            [JsonPropertyName("code")] public int? Code { get; set; }
            [JsonPropertyName("message")] public string? Message { get; set; }
            [JsonPropertyName("data")] public T? Data { get; set; }
        }

        public sealed class CollectionServiceChatResponseData
        {
            [JsonPropertyName("generated_answer")] public string? GeneratedAnswer { get; set; }
            [JsonPropertyName("End")] public bool? End { get; set; } // 注意大小写与 Java demo 对齐
            [JsonPropertyName("token_usage")] public TotalTokenUsage? TokenUsage { get; set; }
        }

        public sealed class TotalTokenUsage
        {
            [JsonPropertyName("embedding_token_usage")] public ModelTokenUsage? EmbeddingUsage { get; set; }
            [JsonPropertyName("rerank_token_usage")] public long? RerankUsage { get; set; }
            [JsonPropertyName("llm_token_usage")] public ModelTokenUsage? LlmUsage { get; set; }
            [JsonPropertyName("rewrite_token_usage")] public ModelTokenUsage? RewriteUsage { get; set; }
        }

        public sealed class ModelTokenUsage
        {
            [JsonPropertyName("prompt_tokens")] public int? PromptTokens { get; set; }
            [JsonPropertyName("completion_tokens")] public int? CompletionTokens { get; set; }
            [JsonPropertyName("total_tokens")] public int? TotalTokens { get; set; }
        }
    }
}
