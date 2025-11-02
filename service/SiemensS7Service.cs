using HandyControl.Data;
using MQTTnet.Client;
using S7.Net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WpfVideoPet;


namespace WpfVideoPet.service
{
    /// <summary>
    /// 西门子 S7 PLC 轮询与控制服务，负责维持后台长连与 MQTT 协调。
    /// </summary>
    public sealed class SiemensS7Service : IAsyncDisposable
    {
        private readonly PlcConfig _config; // PLC 配置信息
        private readonly MqttSingletonService _mqttService; // MQTT 单例服务
        private readonly SemaphoreSlim _plcLock = new(1, 1); // PLC 连接锁
        private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web); // JSON 选项
        private readonly Func<MqttApplicationMessageReceivedEventArgs, Task> _controlHandler; // 控制消息处理器
        private CancellationTokenSource? _pollingCts; // 轮询任务取消源
        private Task? _pollingTask; // 轮询后台任务
        private Plc? _plc; // PLC 客户端实例
        private bool _disposed; // 释放状态标记
        private bool _initialStateLogged; // 是否已输出初始化日志


        /// <summary>
        /// 通过 AppConfig 创建 PLC 服务实例，自动复用 MQTT 单例。
        /// </summary>
        /// <param name="appConfig">应用配置实例。</param>
        public SiemensS7Service(AppConfig appConfig)
            : this(appConfig?.Plc ?? throw new ArgumentNullException(nameof(appConfig)),
                  MqttSingletonService.GetOrCreate(appConfig.Mqtt))
        {
        }

        /// <summary>
        /// 使用指定的 PLC 配置与 MQTT 服务初始化实例。
        /// </summary>
        /// <param name="plcConfig">PLC 配置。</param>
        /// <param name="mqttService">MQTT 单例服务。</param>
        public SiemensS7Service(PlcConfig plcConfig, MqttSingletonService mqttService)
        {
            _config = plcConfig ?? throw new ArgumentNullException(nameof(plcConfig));
            _mqttService = mqttService ?? throw new ArgumentNullException(nameof(mqttService));
            _controlHandler = HandleControlMessageAsync;
            AppLogger.Info($"Siemens S7 服务初始化: IP={_config.IpAddress}, Rack={_config.Rack}, Slot={_config.Slot}, CPU={_config.CpuType ?? "未指定"}");
        }

        /// <summary>
        /// 启动后台轮询任务，并订阅控制主题。
        /// </summary>
        /// <param name="cancellationToken">外部取消令牌。</param>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SiemensS7Service));
            }

            if (!_config.Enabled)
            {
                AppLogger.Info("Siemens S7 服务配置为禁用状态，跳过启动请求。");
                return;
            }

            if (_pollingTask != null)
            {
                AppLogger.Info("Siemens S7 服务检测到重复启动请求，已忽略。");
                return;
            }

            AppLogger.Info("Siemens S7 服务开始启动后台轮询任务。");


            if (!string.IsNullOrWhiteSpace(_config.ControlSubscribeTopic))
            {
                await _mqttService.SubscribeAsync(_config.ControlSubscribeTopic, _controlHandler, cancellationToken).ConfigureAwait(false);
            }

            _pollingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pollingTask = Task.Run(async () => await RunPollingLoopAsync(_pollingCts.Token).ConfigureAwait(false));
            AppLogger.Info("Siemens S7 服务已调度后台轮询任务。");

        }

        /// <summary>
        /// 停止后台轮询并释放资源。
        /// </summary>
        public async Task StopAsync()
        {
            if (_disposed)
            {
                return;
            }

            var cts = _pollingCts;
            if (cts != null)
            {
                cts.Cancel();
                try
                {
                    if (_pollingTask != null)
                    {
                        await _pollingTask.ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 忽略取消异常。
                }
                finally
                {
                    cts.Dispose();
                }
            }

            _pollingCts = null;
            _pollingTask = null;

            if (!string.IsNullOrWhiteSpace(_config.ControlSubscribeTopic))
            {
                await _mqttService.UnsubscribeAsync(_config.ControlSubscribeTopic, _controlHandler, CancellationToken.None).ConfigureAwait(false);
            }

            await ClosePlcAsync().ConfigureAwait(false);
            AppLogger.Info("Siemens S7 服务已停止并关闭 PLC 连接。");

        }

        /// <summary>
        /// 写入控制区的布尔位，用于外部直接控制 PLC。
        /// </summary>
        /// <param name="bitIndex">目标位索引（从 0 开始）。</param>
        /// <param name="value">写入的布尔值。</param>
        /// <param name="cancellationToken">可选的取消令牌。</param>
        public async Task WriteControlBitAsync(int bitIndex, bool value, CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SiemensS7Service));
            }

            if (!_config.Enabled)
            {
                return;
            }

            var maxBits = _config.ControlArea.ByteLength * 8;
            if (bitIndex < 0 || bitIndex >= maxBits)
            {
                throw new ArgumentOutOfRangeException(nameof(bitIndex), $"位索引超出范围: 0-{maxBits - 1}");
            }

            await EnsurePlcConnectedAsync(cancellationToken).ConfigureAwait(false);

            await _plcLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_plc == null)
                {
                    return;
                }

                var area = _config.ControlArea;
                var byteOffset = bitIndex / 8;
                var bitOffset = (byte)(bitIndex % 8);
                var targetByte = area.StartByte + byteOffset;

                await Task.Run(() => _plc!.WriteBit(DataType.DataBlock, area.DbNumber, targetByte, bitOffset, value), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _plcLock.Release();
            }
        }

        /// <summary>
        /// 释放所占资源。
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopAsync().ConfigureAwait(false);
            _plcLock.Dispose();
        }

        /// <summary>
        /// 执行 PLC 数据轮询主循环。
        /// </summary>
        /// <param name="cancellationToken">取消令牌。</param>
        private async Task RunPollingLoopAsync(CancellationToken cancellationToken)
        {
            var delay = TimeSpan.FromMilliseconds(Math.Max(100, _config.PollingIntervalMilliseconds));

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await EnsurePlcConnectedAsync(cancellationToken).ConfigureAwait(false);

                    if (_plc == null || !_plc.IsConnected)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var area = _config.StatusArea;
                    var bytes = await ReadStatusBytesAsync(area, cancellationToken).ConfigureAwait(false);
                    if (bytes.Length == 0)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var bits = ExtractBits(bytes, area.ByteLength * 8);
                    Console.WriteLine($"[PLC] DB{area.DbNumber} 起始{area.StartByte} 字节 {BitConverter.ToString(bytes)} Bits: {string.Join(',', bits.Select(b => b ? 1 : 0))}");

                    await PublishStatusAsync(area, bytes, bits, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"PLC 轮询失败: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 将轮询结果发布到 MQTT 主题。
        /// </summary>
        /// <param name="area">数据区域配置。</param>
        /// <param name="bytes">原始字节数据。</param>
        /// <param name="bits">拆分后的位集合。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        private async Task PublishStatusAsync(PlcAreaConfig area, byte[] bytes, IReadOnlyList<bool> bits, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_config.StatusPublishTopic))
            {
                return;
            }

            var payload = new PlcStatusPayload
            {
                Timestamp = DateTimeOffset.UtcNow,
                DbNumber = area.DbNumber,
                StartByte = area.StartByte,
                ByteLength = bytes.Length,
                Bytes = Convert.ToBase64String(bytes),
                Bits = bits.Select(x => x ? 1 : 0).ToArray()
            };

            var json = JsonSerializer.Serialize(payload, _serializerOptions);
            await _mqttService.PublishAsync(_config.StatusPublishTopic, json, false, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 从 PLC 读取指定区域的原始字节数据。
        /// </summary>
        /// <param name="area">数据区域配置。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>读取到的字节数组。</returns>
        private async Task<byte[]> ReadStatusBytesAsync(PlcAreaConfig area, CancellationToken cancellationToken)
        {
            await _plcLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_plc == null)
                {
                    return Array.Empty<byte>();
                }

                return await Task.Run(() => _plc.ReadBytes(DataType.DataBlock, area.DbNumber, area.StartByte, area.ByteLength) ?? Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _plcLock.Release();
            }
        }
        /// <summary>
        /// 确保 PLC 连接可用，必要时自动重连。
        /// </summary>
        /// <param name="cancellationToken">取消令牌。</param>
        private async Task EnsurePlcConnectedAsync(CancellationToken cancellationToken)
        {
            var shouldLog = false; // 是否需要输出初始化日志
            if (_plc != null && _plc.IsConnected)
            {
                return;
            }

            await _plcLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_plc == null)
                {
                    var cpuType = ResolveCpuType();
                    _plc = new Plc(cpuType, _config.IpAddress, (short)_config.Rack, (short)_config.Slot);
                }

                if (!_plc!.IsConnected)
                {
                    await Task.Run(() => _plc.Open(), cancellationToken).ConfigureAwait(false);
                }

                if (_plc.IsConnected && !_initialStateLogged)
                {
                    shouldLog = true;
                }
            }
            finally
            {
                _plcLock.Release();
            }

            if (shouldLog)
            {
                await LogInitialPlcStateAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 关闭 PLC 连接并清理实例。
        /// </summary>
        private async Task ClosePlcAsync()
        {
            await _plcLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_plc != null)
                {
                    if (_plc.IsConnected)
                    {
                        _plc.Close();
                    }

                    (_plc as IDisposable)?.Dispose();
                    _plc = null;
                }
            }
            finally
            {
                _plcLock.Release();
            }
        }
        /// <summary>
        /// 在 PLC 首次连接后记录初始化信息，并读取 DB100 的首字节作为连通性测试。
        /// </summary>
        /// <param name="cancellationToken">取消令牌，用于在应用停止时打断测试读取。</param>
        private async Task LogInitialPlcStateAsync(CancellationToken cancellationToken)
        {
            if (_initialStateLogged)
            {
                return;
            }

            AppLogger.Info("Siemens S7 服务已建立连接，准备读取 DB100[0] 进行初始化验证。");

            try
            {
                byte[] data; // PLC 返回的原始字节


                await _plcLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_plc == null || !_plc.IsConnected)
                    {
                        AppLogger.Warn("PLC 在初始化验证时未保持连接，跳过 DB100 测试读取。");
                        return;
                    }

                    data = await Task.Run(() => _plc.ReadBytes(DataType.DataBlock, 100, 0, 1) ?? Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _plcLock.Release();
                }
                if (data.Length > 0)
                {
                    var dbByte = data[0]; // DB100[0] 的字节值
                    var hexText = dbByte.ToString("X2"); // 十六进制文本
                    var decimalValue = dbByte; // 十进制数值
                    AppLogger.Info($"Siemens S7 服务初始化验证完成，DB100[0] = 0x{hexText} ({decimalValue})");
                }
                else
                {
                    AppLogger.Warn("Siemens S7 服务初始化验证未读取到 DB100 字节数据。");
                }
            }
            catch (OperationCanceledException)
            {
                AppLogger.Warn("初始化阶段读取 DB100 被取消。");
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "初始化阶段读取 DB100 失败。");
            }
            finally
            {
                _initialStateLogged = true;
            }
        }

        /// <summary>
        /// 处理来自 MQTT 控制主题的指令。
        /// </summary>
        /// <param name="args">MQTT 消息参数。</param>
        private Task HandleControlMessageAsync(MqttApplicationMessageReceivedEventArgs args)
        {
            try
            {
                var payload = args.ApplicationMessage?.PayloadSegment;
                if (payload is null || payload.Value.Count == 0)
                {
                    return Task.CompletedTask;
                }

                var text = Encoding.UTF8.GetString(payload.Value);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return Task.CompletedTask;
                }

                var command = JsonSerializer.Deserialize<PlcControlCommand>(text, _serializerOptions);
                if (command == null)
                {
                    return Task.CompletedTask;
                }

                var writeTask = WriteControlBitAsync(command.Bit, command.Value);
                if (!writeTask.IsCompleted)
                {
                    writeTask.ContinueWith(static t =>
                    {
                        if (t.Exception != null)
                        {
                            Debug.WriteLine($"控制消息写入失败: {t.Exception.InnerException?.Message}");
                        }
                    }, TaskContinuationOptions.OnlyOnFaulted);
                }
            }
            catch (JsonException jsonEx)
            {
                Debug.WriteLine($"控制消息 JSON 解析失败: {jsonEx.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理控制消息异常: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 将配置中的 CPU 字符串解析为枚举。
        /// </summary>
        /// <returns>解析到的 CPU 类型。</returns>
        private CpuType ResolveCpuType()
        {
            if (!string.IsNullOrWhiteSpace(_config.CpuType) && Enum.TryParse(_config.CpuType, true, out CpuType cpuType))
            {
                return cpuType;
            }

            return CpuType.S71200;
        }

        /// <summary>
        /// 将字节数组转换为位数组，按低位在前的顺序展开。
        /// </summary>
        /// <param name="bytes">原始字节序列。</param>
        /// <param name="bitCount">预期的位数量。</param>
        /// <returns>展开后的位数组。</returns>
        private static bool[] ExtractBits(IReadOnlyList<byte> bytes, int bitCount)
        {
            var result = new bool[bitCount];
            var index = 0;

            foreach (var value in bytes)
            {
                for (var bit = 0; bit < 8 && index < result.Length; bit++)
                {
                    result[index++] = ((value >> bit) & 0x01) == 0x01;
                }
            }

            return result;
        }

        /// <summary>
        /// 表示上传至 MQTT 的 PLC 状态消息体。
        /// </summary>
        private sealed class PlcStatusPayload
        {
            /// <summary>
            /// 消息时间戳。
            /// </summary>
            public DateTimeOffset Timestamp { get; set; }

            /// <summary>
            /// 数据块编号。
            /// </summary>
            public int DbNumber { get; set; }

            /// <summary>
            /// 起始字节偏移。
            /// </summary>
            public int StartByte { get; set; }

            /// <summary>
            /// 字节长度。
            /// </summary>
            public int ByteLength { get; set; }

            /// <summary>
            /// Base64 编码的原始字节。
            /// </summary>
            public string Bytes { get; set; } = string.Empty;

            /// <summary>
            /// 位数组，1 表示置位。
            /// </summary>
            public int[] Bits { get; set; } = Array.Empty<int>();
        }

        /// <summary>
        /// MQTT 控制消息的数据结构。
        /// </summary>
        private sealed class PlcControlCommand
        {
            /// <summary>
            /// 要写入的目标位索引。
            /// </summary>
            public int Bit { get; set; }

            /// <summary>
            /// 目标位的布尔值。
            /// </summary>
            public bool Value { get; set; }
        }
    }
}