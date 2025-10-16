using System;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using NAudio.Wave;
using System.Web;

namespace XfyunConsoleDemo
{
    internal class Program
    {
        const int STATUS_FIRST_FRAME = 0;
        const int STATUS_CONTINUE_FRAME = 1;
        const int STATUS_LAST_FRAME = 2;

        static bool isClosed = false; // 全局状态控制 // 控制录音停止标志
        static readonly StringBuilder FinalText = new();
        static int lastPrintedLen = 0;

        // ====== 用时统计相关 ======
        static readonly System.Diagnostics.Stopwatch swLatency = new(); // 统计“说完→最终文本”的耗时
        static bool waitingLatency = false;                             // 是否已进入等待返回阶段（已发送最后一帧）
        static long lastLatencyMs = -1;                                 // 本轮统计到的耗时（ms）

        static async Task Main(string[] args)
        {
            // 设置编码（控制台显示表情/中文）
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false)
                .Build();

            var appId = config["Xfyun:AppId"];
            var apiKey = config["Xfyun:ApiKey"];
            var apiSecret = config["Xfyun:ApiSecret"];
            var hostUrl = config["Xfyun:HostUrl"];

            Console.WriteLine("🎤 启动麦克风语音识别（户外短句循环模式）...");
            var wsUrl = CreateUrl(hostUrl, apiKey, apiSecret);

            // 🔁 主循环：一次识别结束后等待用户指令
            while (true)
            {
                FinalText.Clear();
                lastPrintedLen = 0;
                isClosed = false;

                // 每轮开始前复位计时器与标志
                swLatency.Reset();
                waitingLatency = false;
                lastLatencyMs = -1;

                await StartMicRecognitionAsync(wsUrl, appId);

                // 在输出文本前读取用时
                swLatency.Stop();
                if (waitingLatency && lastLatencyMs < 0) lastLatencyMs = swLatency.ElapsedMilliseconds;

                Console.WriteLine("\n✅ 本次识别结束，结果如下：");
                Console.WriteLine("————————————————————————————————————————");
                Console.WriteLine(FinalText.ToString());
                Console.WriteLine("————————————————————————————————————————");

                // 打印用时（从“检测到静音并发送最终帧”到“接收到首个返回文本”）
                if (lastLatencyMs >= 0)
                    Console.WriteLine($"⏱️ 翻译用时：{lastLatencyMs} ms");

                Console.WriteLine("👉 输入 1 继续录音；其他键退出：");

                string input = Console.ReadLine()?.Trim() ?? "";
                if (input != "1") break;
                Console.Clear();
                Console.WriteLine("🎤 重新开始录音...");
            }

            Console.WriteLine("👋 程序已结束。");
        }

        // 创建带签名的 WebSocket 地址
        static string CreateUrl(string hostUrl, string apiKey, string apiSecret)
        {
            var uri = new Uri(hostUrl);
            var date = DateTime.UtcNow.ToString("r");

            string signatureOrigin = $"host: {uri.Host}\n" +
                                     $"date: {date}\n" +
                                     $"GET {uri.AbsolutePath} HTTP/1.1";

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiSecret));
            var signatureSha = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureOrigin)));

            var authorizationOrigin =
                $"api_key=\"{apiKey}\", algorithm=\"hmac-sha256\", headers=\"host date request-line\", signature=\"{signatureSha}\"";

            var authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(authorizationOrigin));
            var query = $"authorization={HttpUtility.UrlEncode(authorization)}&date={HttpUtility.UrlEncode(date)}&host={uri.Host}";
            return $"{hostUrl}?{query}";
        }

        // 核心：麦克风实时采音 + 自动静音检测 + WebSocket 流式识别
        static async Task StartMicRecognitionAsync(string wsUrl, string appId)
        {
            using var ws = new ClientWebSocket();
            ws.Options.RemoteCertificateValidationCallback = (a, b, c, d) => true;
            await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
            Console.WriteLine("✅ WebSocket 已连接，开始录音（户外短句模式）");

            var waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 20 // 低延迟
            };

            int status = STATUS_FIRST_FRAME;

            int silenceMs = 0;
            int rmsAvg = 0, rmsCount = 0;
            bool calibrated = false;
            int silenceThreshold = 800;
            const int CalibWindowMs = 500;
            int calibElapsedMs = 0;
            const int SilenceHoldMs = 700;
            const int MaxSessionMs = 6000;
            var startTs = Environment.TickCount;

            // 【新增】说话检测/无人说话超时（仅在会话内生效）
            bool voiceDetected = false;     // 一旦检测到声音超过阈值就置为 true
            const int MaxIdleMs = 15000;    // 若一直没人说话，最多等 15s 自动结束（可调）

            var common = new { app_id = appId };
            var business = new { domain = "iat", language = "zh_cn", accent = "mandarin", vinfo = 1, vad_eos = 500, ptt = 1 };

            waveIn.DataAvailable += async (s, e) =>
            {
                if (isClosed) return;

                int rms = CalcRms(e.Buffer, e.BytesRecorded);
                calibElapsedMs += waveIn.BufferMilliseconds;

                // 环境噪声自校准
                if (!calibrated)
                {
                    rmsAvg += rms; rmsCount++;
                    if (calibElapsedMs >= CalibWindowMs)
                    {
                        int ambient = Math.Max(1, rmsAvg / Math.Max(1, rmsCount));
                        silenceThreshold = Math.Clamp(ambient * 3 + 400, 400, 2200);
                        calibrated = true;
                    }
                }

                // 判断静音/语音活动（只有在出现过“语音活动”后才累计静音）
                if (calibrated)
                {
                    if (rms >= silenceThreshold)
                    {
                        voiceDetected = true; // 出现过语音活动
                        silenceMs = 0;        // 有声音时重置静音累计
                    }
                    else
                    {
                        if (voiceDetected) silenceMs += waveIn.BufferMilliseconds;
                        else silenceMs = 0; // 未开口不累计静音，避免提前收尾
                    }
                }

                // 音频帧发送
                string audioBase64 = Convert.ToBase64String(e.Buffer, 0, e.BytesRecorded);
                object frameObj = status == STATUS_FIRST_FRAME
                    ? new
                    {
                        common,
                        business,
                        data = new
                        {
                            status = 0,
                            format = "audio/L16;rate=16000",
                            audio = audioBase64,
                            encoding = "raw"
                        }
                    }
                    : new
                    {
                        data = new
                        {
                            status = 1,
                            format = "audio/L16;rate=16000",
                            audio = audioBase64,
                            encoding = "raw"
                        }
                    };
                if (status == STATUS_FIRST_FRAME) status = STATUS_CONTINUE_FRAME;

                string json = JsonSerializer.Serialize(frameObj);
                await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

                // 自动结束条件：
                // ① 说过话且静音达到阈值；② 会话超时；③ 一直无人说话超时
                bool timeout = (Environment.TickCount - startTs) >= MaxSessionMs;
                bool idleTimeout = (!voiceDetected) && ((Environment.TickCount - startTs) >= MaxIdleMs);

                if ((voiceDetected && calibrated && silenceMs >= SilenceHoldMs) || timeout || idleTimeout)
                {
                    if (timeout) Console.WriteLine("\n⏱️ 超过最长时长，自动结束。");
                    if (idleTimeout) Console.WriteLine("\n（长时间未检测到说话，自动结束）");

                    // 只有出现过“语音活动”的结束，才有“说完→结果”的延迟计时意义
                    if (!waitingLatency && voiceDetected)
                    {
                        swLatency.Restart();
                        waitingLatency = true;
                    }

                    await SendLastFrameAndCloseAsync(ws, waveIn);
                    isClosed = true;
                }
            };

            waveIn.StartRecording();

            // 接收线程
            _ = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    HandleMessage(msg);
                }
            });

            // 等待录音结束
            while (!isClosed)
            {
                await Task.Delay(100);
            }

            await Task.Delay(800); // 稍等讯飞返回尾包
        }

        // 计算 RMS
        static int CalcRms(byte[] buffer, int count)
        {
            if (count < 2) return 0;
            long sum = 0; int samples = count / 2;
            for (int i = 0; i < samples; i++)
            {
                short s = BitConverter.ToInt16(buffer, i * 2);
                sum += (long)s * s;
            }
            double mean = sum / Math.Max(1, samples);
            return (int)Math.Sqrt(mean);
        }

        // 发送最后一帧并收尾
        static async Task SendLastFrameAndCloseAsync(ClientWebSocket ws, WaveInEvent waveIn)
        {
            if (ws.State != WebSocketState.Open) return;
            var lastFrame = new { data = new { status = 2, format = "audio/L16;rate=16000", audio = "", encoding = "raw" } };
            string lastJson = JsonSerializer.Serialize(lastFrame);
            await ws.SendAsync(Encoding.UTF8.GetBytes(lastJson), WebSocketMessageType.Text, true, CancellationToken.None);
            waveIn.StopRecording();
            Console.WriteLine("\n🛑 检测到静音，已结束本次识别。");
        }

        // 实时输出识别文本（单行刷新）
        static void PrintOnSameLine(string text)
        {
            Console.Write("\r" + new string(' ', lastPrintedLen) + "\r");
            Console.Write(text);
            lastPrintedLen = text.Length;
        }

        static void HandleMessage(string message)
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;
                int code = root.GetProperty("code").GetInt32();
                if (code != 0)
                {
                    Console.WriteLine($"\n❌ 错误: {root.GetProperty("message").GetString()}");
                    return;
                }

                if (root.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("ws", out var wsArray))
                {
                    var sb = new StringBuilder();
                    foreach (var wsItem in wsArray.EnumerateArray())
                        foreach (var cw in wsItem.GetProperty("cw").EnumerateArray())
                            sb.Append(cw.GetProperty("w").GetString());

                    FinalText.Append(sb);
                    PrintOnSameLine("🗣️ " + FinalText.ToString());

                    // 一旦收到收尾后的首个文本，立即停止计时并记录耗时
                    if (waitingLatency && swLatency.IsRunning)
                    {
                        swLatency.Stop();
                        lastLatencyMs = swLatency.ElapsedMilliseconds;
                    }
                }
            }
            catch { }
        }
    }
}
