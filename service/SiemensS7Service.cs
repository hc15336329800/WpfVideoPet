using HandyControl.Data;
using S7.Net;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WpfVideoPet.mqtt;
using static WpfVideoPet.mqtt.FixedLengthMqttBridge;

namespace WpfVideoPet.service
{
    /// <summary>
    /// 西门子 S7 PLC 轮询与控制服务 
    /// </summary>
    public sealed class SiemensS7Service : IAsyncDisposable
    {
        private readonly PlcConfig _config; // PLC 配置信息
        private readonly FixedLengthMqttBridge _mqttBridge; // 16 字节 MQTT 桥
        private readonly SemaphoreSlim _plcLock = new(1, 1); // PLC 连接锁
        private readonly EventHandler<FixedLengthMqttMessage> _controlHandler; // 控制消息处理器
        private CancellationTokenSource? _pollingCts; // 轮询任务取消源
        private Task? _pollingTask; // 轮询后台任务
        private Plc? _plc; // PLC 客户端实例
        private bool _disposed; // 释放状态标记
        private bool _initialStateLogged; // 是否已输出初始化日志
        private bool _controlSubscribed; // 是否已挂载控制回调

        /// <summary>
        /// 使用应用配置和共享 MQTT 桥接实例初始化服务。
        /// </summary>
        /// <param name="appConfig">应用配置实例。</param>
        /// <param name="mqttBridge">共享的 16 字节 MQTT 桥接服务。</param>
        public SiemensS7Service(AppConfig appConfig, FixedLengthMqttBridge mqttBridge)
            : this(appConfig?.Plc ?? throw new ArgumentNullException(nameof(appConfig)), mqttBridge)
        {
        }

        /// <summary>
        /// 使用指定的 PLC 配置与 MQTT 桥接服务初始化实例。
        /// </summary>
        /// <param name="plcConfig">PLC 配置。</param>
        /// <param name="mqttBridge">MQTT 桥接服务。</param>
        public SiemensS7Service(PlcConfig plcConfig, FixedLengthMqttBridge mqttBridge)
        {
            _config = plcConfig ?? throw new ArgumentNullException(nameof(plcConfig));
            _mqttBridge = mqttBridge ?? throw new ArgumentNullException(nameof(mqttBridge));
            _controlHandler = OnControlMessageReceived;
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

            await _mqttBridge.StartAsync(cancellationToken).ConfigureAwait(false);

            if (!_controlSubscribed && !string.IsNullOrWhiteSpace(_config.ControlSubscribeTopic))
            {
                _mqttBridge.MessageReceived += _controlHandler;
                _controlSubscribed = true;
                AppLogger.Info($"已挂载 PLC 控制消息回调，监听主题: {_config.ControlSubscribeTopic}");
            }
            else if (string.IsNullOrWhiteSpace(_config.ControlSubscribeTopic))
            {
                AppLogger.Warn("未配置 PLC 控制订阅主题，控制指令将被忽略。");
            }

            AppLogger.Info("Siemens S7 服务开始启动后台轮询任务。");

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

            if (_controlSubscribed)
            {
                _mqttBridge.MessageReceived -= _controlHandler;
                _controlSubscribed = false;
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

                    await PublishStatusAsync(area, bytes, cancellationToken).ConfigureAwait(false);
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
        /// <param name="cancellationToken">取消令牌。</param>
        private async Task PublishStatusAsync(PlcAreaConfig area, byte[] bytes, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_config.StatusPublishTopic))
            {
                return;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(16); // MQTT 发送缓冲区
            try
            {
                Array.Clear(buffer, 0, 16);
                var copyLength = Math.Min(16, bytes.Length);
                Array.Copy(bytes, buffer, copyLength);

                if (copyLength < 16)
                {
                    for (var i = copyLength; i < 16; i++)
                    {
                        buffer[i] = 0x00;
                    }
                }

                await _mqttBridge.SendAsync(buffer.AsMemory(0, 16), _config.StatusPublishTopic, false, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
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
        /// <param name="sender">事件源。</param>
        /// <param name="message">定长消息内容。</param>
        private void OnControlMessageReceived(object? sender, FixedLengthMqttMessage message)
        {
            if (_disposed)
            {
                return;
            }

            var targetTopic = _config.ControlSubscribeTopic;
            if (!string.IsNullOrWhiteSpace(targetTopic) &&
                !string.Equals(message.Topic, targetTopic, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var payload = message.Payload.Span;
            if (payload.Length < 2)
            {
                AppLogger.Warn("PLC 控制消息长度不足 2 字节，已忽略。");
                return;
            }

            var bitIndex = payload[0]; // 目标位索引
            var bitValue = payload[1] != 0; // 写入值
            AppLogger.Info($"收到 PLC 控制指令，位索引: {bitIndex}, 值: {(bitValue ? 1 : 0)}");

            _ = Task.Run(async () =>
            {
                try
                {
                    await WriteControlBitAsync(bitIndex, bitValue, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"处理 PLC 控制消息失败: {ex.Message}");
                }
            });
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
                    result[index++] = (value >> bit & 0x01) == 0x01;
                }
            }

            return result;
        }

        // 控制与状态简化为 16 字节协议，暂不需要额外的数据结构。

    }
}