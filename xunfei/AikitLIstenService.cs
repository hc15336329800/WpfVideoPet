using System;
using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace WpfVideoPet.xunfei
{
    /// <summary>
    /// 负责驱动麦克风采集与讯飞实时语音识别的服务，识别语音并转为文字。 在线API
    /// </summary>
    public sealed class AikitLIstenService : IDisposable
    {
        private const int StatusFirstFrame = 0;
        private const int StatusContinueFrame = 1;
        private const int StatusLastFrame = 2;

        private readonly AikitListenSettings _settings;
         private bool _disposed;
         private ClientWebSocket? _webSocket;
        /// <summary>
        /// 当前使用的麦克风采集实例。
        /// </summary>
        private WaveInEvent? _waveIn;
        /// <summary>
        /// 标记麦克风是否处于录音状态，避免访问不存在的 RecordingState 属性。
        /// </summary>
        private bool _isRecording;
        private CancellationTokenSource? _internalCts;
        private Task? _receiveTask;
        private TaskCompletionSource<string?>? _resultSource;
        private readonly Stopwatch _latencyStopwatch = new();
        private bool _waitingLatency;
        // 标记是否已经触发最终识别结果事件。
        private bool _finalResultEmitted;
        /// <summary>
        /// 控制语音识别流程串行执行的同步原语，避免上一轮尚未释放资源时返回旧结果。
        /// </summary>
        private readonly SemaphoreSlim _recognitionLock = new(1, 1);

        /// <summary>
        /// 构造函数，注入基础配置。
        /// </summary>
        /// <param name="settings">包含讯飞接口鉴权信息的配置对象。</param>
        public AikitLIstenService(AikitListenSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// 识别过程中的实时文本事件，便于界面逐步展示。
        /// </summary>
        public event EventHandler<string>? InterimResultReceived;

        /// <summary>
        /// 识别任务完成事件，返回最终文本。
        /// </summary>
        public event EventHandler<string>? RecognitionCompleted;

        /// <summary>
        /// 识别异常事件，便于界面层提示。
        /// </summary>
        public event EventHandler<string>? RecognitionFailed;

        /// <summary>
        /// 启动一次语音识别任务，结束后返回完整文本。
        /// </summary>
        /// <param name="cancellationToken">外部取消信号。</param>
        /// <returns>识别成功时的文本，失败或取消时返回 null。</returns>
        public async Task<string?> TranscribeOnceAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            AppLogger.Info("准备启动新的语音识别任务，等待内部同步锁释放。");
            await _recognitionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            AppLogger.Info("已获取语音识别内部锁，开始构建语音识别管线。");


            try
            {
                _finalResultEmitted = false;
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _internalCts = linkedCts;

                _resultSource = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

                await InitializeWebSocketAsync(linkedCts.Token).ConfigureAwait(false);
                SetupMicrophone(linkedCts.Token);

                _receiveTask = Task.Run(() => ReceiveLoopAsync(linkedCts.Token), CancellationToken.None);

                _waveIn?.StartRecording();
                if (_waveIn != null)
                {
                    _isRecording = true;
                    AppLogger.Info("讯飞播报服务已开始录音，已更新录音状态标记。");
                }
                var result = await _resultSource.Task.ConfigureAwait(false);

                return result;
            }
            catch (OperationCanceledException)
            {
                AppLogger.Warn("语音识别任务已被取消。");
                return null;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"语音识别任务执行失败: {ex.Message}");
                RecognitionFailed?.Invoke(this, ex.Message);
                return null;
            }
            finally
            {
                await CleanupAsync().ConfigureAwait(false);
                _recognitionLock.Release();

            }
        }

        /// <summary>
        /// 释放底层资源，确保麦克风与网络连接被关闭。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _internalCts?.Cancel();
            _waveIn?.Dispose();
            _webSocket?.Dispose();
            _internalCts?.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 建立与讯飞 WebSocket 服务的连接。
        /// </summary>
        /// <param name="cancellationToken">用于取消连接的令牌。</param>
        private async Task InitializeWebSocketAsync(CancellationToken cancellationToken)
        {
            string url = CreateSignedUrl();
            _webSocket = new ClientWebSocket();
            _webSocket.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
            await _webSocket.ConnectAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            AppLogger.Info("已连接讯飞语音识别 WebSocket 服务。");
        }

        /// <summary>
        /// 初始化麦克风采集并注册音频帧处理逻辑。
        /// </summary>
        /// <param name="cancellationToken">用于响应外部取消的令牌。</param>
        private void SetupMicrophone(CancellationToken cancellationToken)
        {
            _isRecording = false;
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 20
            };
            AppLogger.Info("麦克风采集器已创建，录音状态重置为未启动。");

            int status = StatusFirstFrame;
            int silenceMs = 0;
            int rmsAvg = 0;
            int rmsCount = 0;
            bool calibrated = false;
            int silenceThreshold = 800;
            const int calibrationWindowMs = 500;
            int calibrationElapsed = 0;
            const int silenceHoldMs = 700;
            const int maxSessionMs = 6000;
            const int maxIdleMs = 15000;
            bool voiceDetected = false;
            bool closing = false;
            var startTick = Environment.TickCount;

            _waveIn.DataAvailable += async (_, args) =>
            {
                if (closing || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    int rms = CalcRms(args.Buffer, args.BytesRecorded);
                    calibrationElapsed += _waveIn.BufferMilliseconds;

                    if (!calibrated)
                    {
                        rmsAvg += rms;
                        rmsCount++;
                        if (calibrationElapsed >= calibrationWindowMs)
                        {
                            int ambient = Math.Max(1, rmsAvg / Math.Max(1, rmsCount));
                            silenceThreshold = Math.Clamp(ambient * 3 + 400, 400, 2200);
                            calibrated = true;
                            AppLogger.Info($"环境噪声自适应完成，静音阈值: {silenceThreshold}");
                        }
                    }

                    if (calibrated)
                    {
                        if (rms >= silenceThreshold)
                        {
                            voiceDetected = true;
                            silenceMs = 0;
                        }
                        else
                        {
                            if (voiceDetected)
                            {
                                silenceMs += _waveIn.BufferMilliseconds;
                            }
                        }
                    }

                    await SendFrameAsync(args.Buffer, args.BytesRecorded, status).ConfigureAwait(false);
                    if (status == StatusFirstFrame)
                    {
                        status = StatusContinueFrame;
                    }

                    bool timeout = Environment.TickCount - startTick >= maxSessionMs;
                    bool idleTimeout = !voiceDetected && Environment.TickCount - startTick >= maxIdleMs;

                    if (voiceDetected && calibrated && silenceMs >= silenceHoldMs || timeout || idleTimeout)
                    {
                        if (!closing)
                        {
                            closing = true;
                            if (timeout)
                            {
                                AppLogger.Info("语音识别达到最长会话时长，准备收尾。");
                            }
                            if (idleTimeout)
                            {
                                AppLogger.Info("长时间未检测到说话，将结束本次识别。");
                            }

                            if (!_waitingLatency && voiceDetected)
                            {
                                _latencyStopwatch.Restart();
                                _waitingLatency = true;
                            }

                            await SendLastFrameAndStopAsync().ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, $"处理麦克风数据时发生异常: {ex.Message}");
                    RecognitionFailed?.Invoke(this, ex.Message);
                    await AbortRecognitionAsync().ConfigureAwait(false);
                }
            };

            _waveIn.RecordingStopped += async (_, _) =>
            {
                _isRecording = false;
                AppLogger.Info("麦克风录音已停止，状态标记已复位。");
                if (!closing)
                {
                    closing = true;
                    await SendLastFrameAndStopAsync().ConfigureAwait(false);
                }
            };
        }

        /// <summary>
        /// 持续从 WebSocket 接收识别结果并拼装文本。
        /// </summary>
        /// <param name="cancellationToken">外部取消令牌。</param>
        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            if (_webSocket == null)
            {
                return;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            var finalText = new StringBuilder();

            try
            {
                while (!cancellationToken.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
                {
                    var result = await _webSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    HandleMessage(message, finalText);
                }
            }
            catch (OperationCanceledException)
            {
                // 外部取消属于正常流程，不需要额外处理。
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"接收语音识别结果时发生异常: {ex.Message}");
                RecognitionFailed?.Invoke(this, ex.Message);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                if (_waitingLatency && _latencyStopwatch.IsRunning)
                {
                    _latencyStopwatch.Stop();
                    AppLogger.Info($"识别尾包耗时: {_latencyStopwatch.ElapsedMilliseconds} ms");
                }

                string? resultText = finalText.Length > 0 ? finalText.ToString() : null;
                if (!_finalResultEmitted && resultText != null)
                {
                    RecognitionCompleted?.Invoke(this, resultText);
                }

                if (!_finalResultEmitted)
                {
                    _resultSource?.TrySetResult(resultText);
                }
            }
        }

        /// <summary>
        /// 解析讯飞返回的 JSON 数据并拼接最终文本。
        /// </summary>
        /// <param name="message">服务端返回的原始 JSON 字符串。</param>
        /// <param name="finalText">最终文本缓存。</param>
        private void HandleMessage(string message, StringBuilder finalText)
        {
            try
            {
                using var document = JsonDocument.Parse(message);
                var root = document.RootElement;
                int code = root.GetProperty("code").GetInt32();
                if (code != 0)
                {
                    string error = root.GetProperty("message").GetString() ?? "未知错误";
                    AppLogger.Warn($"讯飞返回错误: {error}");
                    RecognitionFailed?.Invoke(this, error);
                    _resultSource?.TrySetResult(null);
                    return;
                }

                if (root.TryGetProperty("data", out var dataElement) &&
                    dataElement.ValueKind == JsonValueKind.Object &&
                    dataElement.TryGetProperty("result", out var resultElement) &&
                    resultElement.ValueKind == JsonValueKind.Object &&
                    resultElement.TryGetProperty("ws", out var wsArray) &&
                    wsArray.ValueKind == JsonValueKind.Array)
                {
                    int status = dataElement.TryGetProperty("status", out var statusElement) &&
                               statusElement.ValueKind == JsonValueKind.Number
                      ? statusElement.GetInt32()
                      : StatusContinueFrame;
                    var builder = new StringBuilder();
                    foreach (var wsItem in wsArray.EnumerateArray())
                    {
                        if (!wsItem.TryGetProperty("cw", out var cwArray) || cwArray.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        foreach (var cwItem in cwArray.EnumerateArray())
                        {
                            if (cwItem.TryGetProperty("w", out var wElement) && wElement.ValueKind == JsonValueKind.String)
                            {
                                builder.Append(wElement.GetString());
                            }
                        }
                    }
                    string? currentText = null;
                    if (builder.Length > 0)
                    {
                        finalText.Append(builder);
                        currentText = finalText.ToString();
                        InterimResultReceived?.Invoke(this, currentText);
                        if (_waitingLatency && _latencyStopwatch.IsRunning)
                        {
                            _latencyStopwatch.Stop();
                            AppLogger.Info($"收到识别结果，尾包耗时: {_latencyStopwatch.ElapsedMilliseconds} ms");
                        }
                    }
                    else if (finalText.Length > 0)
                    {
                        currentText = finalText.ToString();
                    }

                    if (status == StatusLastFrame && !_finalResultEmitted)
                    {
                        if (currentText != null)
                        {
                            AppLogger.Info("检测到讯飞识别尾包，立即触发最终识别结果事件。");
                            _finalResultEmitted = true;
                            RecognitionCompleted?.Invoke(this, currentText);
                            _resultSource?.TrySetResult(currentText);
                        }
                        else
                        {
                            AppLogger.Warn("讯飞识别尾包到达但未拼接出有效文本。");
                            _resultSource?.TrySetResult(null);
                        }

                        if (_internalCts != null && !_internalCts.IsCancellationRequested)
                        {
                            AppLogger.Info("尾包处理完成，准备取消内部识别令牌以加速结束。");
                            _internalCts.Cancel();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"解析讯飞返回结果时发生异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 按照讯飞接口协议发送音频帧。
        /// </summary>
        /// <param name="buffer">PCM 缓冲区。</param>
        /// <param name="length">有效字节数。</param>
        /// <param name="status">当前帧状态。</param>
        private async Task SendFrameAsync(byte[] buffer, int length, int status)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            {
                return;
            }

            string audio = Convert.ToBase64String(buffer, 0, length);
            object frame = status == StatusFirstFrame
                ? new
                {
                    common = new { app_id = _settings.AppId },
                    business = new { domain = "iat", language = "zh_cn", accent = "mandarin", vinfo = 1, vad_eos = 500, ptt = 1 },
                    data = new { status = StatusFirstFrame, format = "audio/L16;rate=16000", audio, encoding = "raw" }
                }
                : new
                {
                    data = new { status = StatusContinueFrame, format = "audio/L16;rate=16000", audio, encoding = "raw" }
                };

            string json = JsonSerializer.Serialize(frame);
            await _webSocket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// 发送最后一帧并停止录音。
        /// </summary>
        private async Task SendLastFrameAndStopAsync()
        {
            if (_webSocket == null)
            {
                return;
            }

            try
            {
                var frame = new { data = new { status = StatusLastFrame, format = "audio/L16;rate=16000", audio = string.Empty, encoding = "raw" } };
                string json = JsonSerializer.Serialize(frame);
                await _webSocket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
                if (_waveIn != null && _isRecording)
                {
                    _waveIn.StopRecording();
                    _isRecording = false;
                    AppLogger.Info("已发送最后一帧并停止录音，触发主动停止流程。");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"发送结束帧时发生异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 在出现异常时终止识别流程。
        /// </summary>
        private async Task AbortRecognitionAsync()
        {
            try
            {
                _internalCts?.Cancel();
                if (_waveIn != null && _isRecording)
                {
                    _waveIn.StopRecording();
                    _isRecording = false;
                    AppLogger.Info("异常终止时检测到仍在录音，已主动停止麦克风。");
                }
                if (_webSocket != null && _webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "abort", CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"终止语音识别时发生异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 清理语音识别相关资源，确保连接与设备被正确关闭。
        /// </summary>
        private async Task CleanupAsync()
        {
            try
            {
                if (_receiveTask != null)
                {
                    try
                    {
                        await _receiveTask.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error(ex, $"等待语音识别接收循环结束时发生异常: {ex.Message}");
                    }
                }

                if (_waveIn != null && _isRecording)
                {
                    _waveIn.StopRecording();
                    AppLogger.Info("清理阶段检测到录音仍在进行，已停止麦克风。");
                }
                if (_waveIn != null)
                {
                    _waveIn.Dispose();
                    _waveIn = null;
                }
                _isRecording = false;

                if (_webSocket != null)
                {
                    if (_webSocket.State == WebSocketState.Open)
                    {
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);
                    }

                    _webSocket.Dispose();
                    _webSocket = null;
                }

                _internalCts?.Dispose();
                _internalCts = null;
            }
            finally
            {
                _receiveTask = null;
                _resultSource = null;
                _waitingLatency = false;
                _latencyStopwatch.Reset();
            }
        }

        /// <summary>
        /// 构建带鉴权信息的 WebSocket 请求地址。
        /// </summary>
        /// <returns>可直接连接的 WebSocket 地址。</returns>
        private string CreateSignedUrl()
        {
            var uri = new Uri(_settings.HostUrl);
            string date = DateTime.UtcNow.ToString("r");
            string signatureOrigin = $"host: {uri.Host}\n" +
                                     $"date: {date}\n" +
                                     $"GET {uri.AbsolutePath} HTTP/1.1";

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_settings.ApiSecret));
            string signatureSha = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureOrigin)));

            string authorizationOrigin = $"api_key=\"{_settings.ApiKey}\", algorithm=\"hmac-sha256\", headers=\"host date request-line\", signature=\"{signatureSha}\"";
            string authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(authorizationOrigin));

            string query = $"authorization={Uri.EscapeDataString(authorization)}&date={Uri.EscapeDataString(date)}&host={uri.Host}";
            return $"{_settings.HostUrl}?{query}";
        }

        /// <summary>
        /// 计算音频缓冲区的 RMS 值，用于静音检测。
        /// </summary>
        /// <param name="buffer">PCM 数据缓冲区。</param>
        /// <param name="count">有效字节数。</param>
        /// <returns>RMS 数值。</returns>
        private static int CalcRms(byte[] buffer, int count)
        {
            if (count < 2)
            {
                return 0;
            }

            long sum = 0;
            int samples = count / 2;
            for (int i = 0; i < samples; i++)
            {
                short sample = BitConverter.ToInt16(buffer, i * 2);
                sum += (long)sample * sample;
            }

            double mean = sum / Math.Max(1, samples);
            return (int)Math.Sqrt(mean);
        }

        /// <summary>
        /// 检查服务是否已被释放。
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AikitLIstenService));
            }
        }
    }
}