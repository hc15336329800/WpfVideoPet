﻿using S7.Net;
using System;
using System.Collections.Generic;
 using System.Linq;
using System.Text;
using System.Text.Json;
 using System.Threading.Tasks;
using WpfVideoPet.mqtt;
using static WpfVideoPet.mqtt.MqttCoreService;

namespace WpfVideoPet.service
{
    /// <summary>
    /// 西门子 S7 PLC 访问服务，提供按需的读写接口，并复用通用 MQTT 桥处理控制消息。
    /// </summary>
    public sealed class SiemensS7Service : IAsyncDisposable
    {
        private readonly PlcConfig _config; // PLC 配置信息
        private readonly MqttCoreService _mqttBridge; // MQTT 桥接实例
        private readonly SemaphoreSlim _plcLock = new(1, 1); // PLC 访问锁
        private readonly EventHandler<MqttBridgeMessage> _controlHandler; // 控制消息处理器
  
        private Plc? _plc; // PLC 客户端实例
        private bool _disposed; // 释放标记
        private bool _controlSubscribed; // 控制订阅状态
        private CancellationTokenSource? _pollingCts; // 状态轮询取消源
        private Task? _statusPollingTask; // 状态轮询后台任务

        /// <summary>
        /// 使用应用配置和共享 MQTT 桥接实例初始化服务。
        /// </summary>
        /// <param name="appConfig">应用配置实例。</param>
        /// <param name="mqttBridge">共享的 MQTT 桥接服务。</param>
        public SiemensS7Service(AppConfig appConfig, MqttCoreService mqttBridge)
            : this(appConfig?.Plc ?? throw new ArgumentNullException(nameof(appConfig)), mqttBridge)
        {
        }


        /// <summary>
        /// 使用指定的 PLC 配置与 MQTT 桥接服务初始化实例。
        /// </summary>
        /// <param name="plcConfig">PLC 配置信息。</param>
        /// <param name="mqttBridge">MQTT 桥接服务。</param>
        public SiemensS7Service(PlcConfig plcConfig, MqttCoreService mqttBridge)
        {
            _config = plcConfig ?? throw new ArgumentNullException(nameof(plcConfig));
            _mqttBridge = mqttBridge ?? throw new ArgumentNullException(nameof(mqttBridge));
            _controlHandler = OnControlMessageReceived;
            AppLogger.Info($"Siemens S7 服务初始化: IP={_config.IpAddress}, Rack={_config.Rack}, Slot={_config.Slot}, CPU={_config.CpuType ?? "未指定"}");
        }

        /// <summary>
        /// 启动 PLC 服务：按需建立 MQTT 连接并订阅控制主题，同时确保 PLC 可以在首次访问前准备就绪。
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

            await _mqttBridge.StartAsync(cancellationToken).ConfigureAwait(false);

            if (!_controlSubscribed && !string.IsNullOrWhiteSpace(_config.ControlSubscribeTopic))
            {
                await _mqttBridge.SubscribeAdditionalTopicAsync(_config.ControlSubscribeTopic, cancellationToken).ConfigureAwait(false);
                _mqttBridge.MessageReceived += _controlHandler;
                _controlSubscribed = true;
                AppLogger.Info($"已挂载 PLC 控制消息回调，监听主题: {_config.ControlSubscribeTopic}");
            }
            else if (string.IsNullOrWhiteSpace(_config.ControlSubscribeTopic))
            {
                AppLogger.Warn("未配置 PLC 控制订阅主题，控制指令将被忽略。");
            }

            await EnsurePlcConnectedAsync(cancellationToken).ConfigureAwait(false);

            StartStatusPollingLoop(cancellationToken);
        }

        /// <summary>
        /// 停止 PLC 服务，解除 MQTT 回调并断开 PLC 连接。
        /// </summary>
        public async Task StopAsync()
        {
            if (_disposed)
            {
                return;
            }

            if (_controlSubscribed)
            {
                _mqttBridge.MessageReceived -= _controlHandler;
                _controlSubscribed = false;
            }
            await StopStatusPollingLoopAsync().ConfigureAwait(false);
            await ClosePlcAsync().ConfigureAwait(false);
            AppLogger.Info("Siemens S7 服务已停止并关闭 PLC 连接。");
        }

        /// <summary>
        /// 读取配置中状态区域的原始字节数据，供外部按需获取 PLC 当前状态。
        /// </summary>
        /// <param name="cancellationToken">外部取消令牌。</param>
        /// <returns>状态区域的原始字节数组。</returns>
        public Task<byte[]> ReadStatusAreaAsync(CancellationToken cancellationToken = default)
        {
            if (_config.StatusArea == null)
            {
                return Task.FromResult(Array.Empty<byte>());
            }

            return ReadAreaAsync(_config.StatusArea, cancellationToken);
        }

        /// <summary>
        /// 按指定区域从 PLC 读取原始字节数据，调用者可以自行解释返回内容。
        /// </summary>
        /// <param name="area">目标数据区域配置。</param>
        /// <param name="cancellationToken">外部取消令牌。</param>
        /// <returns>指定区域的原始字节数组。</returns>
        public async Task<byte[]> ReadAreaAsync(PlcAreaConfig area, CancellationToken cancellationToken = default)
        {
            if (area == null)
            {
                throw new ArgumentNullException(nameof(area));
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SiemensS7Service));
            }

            if (!_config.Enabled)
            {
                return Array.Empty<byte>();
            }

            await EnsurePlcConnectedAsync(cancellationToken).ConfigureAwait(false);

            await _plcLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_plc == null)
                {
                    return Array.Empty<byte>();
                }

                return await Task.Run(() =>
                    _plc.ReadBytes(DataType.DataBlock, area.DbNumber, area.StartByte, area.ByteLength) ?? Array.Empty<byte>(),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _plcLock.Release();
            }
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

            var area = _config.ControlArea;
            var maxBits = area.ByteLength * 8;
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

                var byteOffset = bitIndex / 8; // 目标字节偏移
                var bitOffset = (byte)(bitIndex % 8); // 位偏移量
                var targetByte = area.StartByte + byteOffset; // 实际字节地址

                await Task.Run(() => _plc!.WriteBit(DataType.DataBlock, area.DbNumber, targetByte, bitOffset, value), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _plcLock.Release();
            }
        }
        /// <summary>
        /// 批量写入控制区的布尔位数据，按照位顺序组包并一次性写入 PLC。
        /// </summary>
        /// <param name="bitValues">按照位顺序排列的布尔数组。</param>
        /// <param name="cancellationToken">可选的取消令牌。</param>
        public async Task WriteControlBitsAsync(bool[] bitValues, CancellationToken cancellationToken = default)
        {
            if (bitValues == null)
            {
                throw new ArgumentNullException(nameof(bitValues));
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SiemensS7Service));
            }

            if (!_config.Enabled)
            {
                return;
            }

            var area = _config.ControlArea; // 控制区配置
            if (area.ByteLength <= 0)
            {
                AppLogger.Warn("控制区字节长度未配置，批量写入已跳过。");
                return;
            }

            var maxBits = area.ByteLength * 8; // 控制区最大位数
            if (bitValues.Length > maxBits)
            {
                throw new ArgumentOutOfRangeException(nameof(bitValues), $"位数组长度超出控制区范围: {bitValues.Length}/{maxBits}");
            }

            await EnsurePlcConnectedAsync(cancellationToken).ConfigureAwait(false);

            await _plcLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_plc == null)
                {
                    return;
                }

                var buffer = new byte[area.ByteLength]; // 控制区写入缓冲
                for (var i = 0; i < bitValues.Length; i++)
                {
                    if (!bitValues[i])
                    {
                        continue;
                    }

                    var byteOffset = i / 8; // 目标字节偏移
                    var bitOffset = i % 8; // 位偏移
                    buffer[byteOffset] |= (byte)(1 << bitOffset);
                }

                await Task.Run(() => _plc.WriteBytes(DataType.DataBlock, area.DbNumber, area.StartByte, buffer), cancellationToken)
                    .ConfigureAwait(false);
                AppLogger.Info($"已批量写入 PLC 控制位，写入位数: {bitValues.Length}。");
            }
            finally
            {
                _plcLock.Release();
            }
        }

        /// <summary>
        /// 释放所占资源，并在必要时记录释放过程中的异常信息。
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "释放 Siemens S7 服务时发生异常。");
            }
            finally
            {
                _disposed = true;
                _plcLock.Dispose();
            }
        }

        /// <summary>
        /// 确保 PLC 连接可用，必要时自动创建并打开连接。
        /// </summary>
        /// <param name="cancellationToken">外部取消令牌。</param>
        private async Task EnsurePlcConnectedAsync(CancellationToken cancellationToken)
        {
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
                    AppLogger.Info("Siemens S7 服务已建立 PLC 连接。");
                }
            }
            finally
            {
                _plcLock.Release();
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
                        AppLogger.Info("Siemens S7 服务已断开 PLC 连接。");
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
        /// 处理来自 MQTT 控制主题的指令，将 01 字符串转换为字节并写入 PLC DB 块。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="message">定长消息内容。</param>
        private void OnControlMessageReceived(object? sender, MqttBridgeMessage message)
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

            var bitString = Encoding.UTF8.GetString(message.Payload.Span).Trim();
            if (string.IsNullOrWhiteSpace(bitString))
            {
                AppLogger.Warn("PLC 控制消息为空，已忽略。");
                return;
            }
            AppLogger.Info($"接收到 PLC 控制原始消息，主题: {message.Topic}, 内容长度: {bitString.Length}, 内容(BIT): {bitString}");

            var sanitizedBits = new List<bool>(); // 解析后的位序列
            foreach (var ch in bitString)
            {
                if (ch == '0')
                {
                    sanitizedBits.Add(false);
                }
                else if (ch == '1')
                {
                    sanitizedBits.Add(true);
                }
                else if (!char.IsWhiteSpace(ch))
                {
                    AppLogger.Warn($"检测到非法的 PLC 控制位字符: {ch}");
                    return;
                }
            }

            if (sanitizedBits.Count == 0)
            {
                AppLogger.Warn("PLC 控制消息未包含有效位信息，已忽略。");
                return;
            }

            var area = _config.ControlArea; // 控制区域配置
            if (area.ByteLength <= 0)
            {
                AppLogger.Warn("PLC 控制区域字节长度未配置，写入操作已跳过。");
                return;
            }

            var maxBits = area.ByteLength * 8; // 控制区位容量
            if (sanitizedBits.Count > maxBits)
            {
                AppLogger.Warn($"控制位数量超出配置容量，将截断至 {maxBits} 位。");
                sanitizedBits = sanitizedBits.Take(maxBits).ToList();
            }

            if (sanitizedBits.Count < maxBits)
            {
                var paddingCount = maxBits - sanitizedBits.Count; // 需要补齐的位数
                sanitizedBits.AddRange(Enumerable.Repeat(false, paddingCount));
                AppLogger.Info($"控制位数量不足 {maxBits} 位，已在尾部补齐 {paddingCount} 位 0。");
            }

            var finalBitString = new string(sanitizedBits.Select(b => b ? '1' : '0').ToArray()); // 用于日志输出的最终位串
            var controlBuffer = BuildControlBuffer(sanitizedBits, area.ByteLength); // 转换后的控制区字节
            var hexPayload = BitConverter.ToString(controlBuffer).Replace("-", string.Empty); // 十六进制展示

            AppLogger.Info($"准备写入 PLC 控制区，字节数: {controlBuffer.Length}, 数据(HEX): {hexPayload}, 数据(BIT): {finalBitString}");

            _ = Task.Run(async () =>
            {
                try
                {
                    await WriteControlBytesAsync(controlBuffer, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "批量写入 PLC 控制位失败。");
                }
            });
        }
        /// <summary>
        /// 将处理后的字节缓冲写入 PLC 控制区 DB 块。
        /// </summary>
        /// <param name="buffer">目标写入数据。</param>
        /// <param name="cancellationToken">外部取消令牌。</param>
        public async Task WriteControlBytesAsync(byte[] buffer, CancellationToken cancellationToken = default)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SiemensS7Service));
            }

            if (!_config.Enabled)
            {
                return;
            }

            var area = _config.ControlArea; // 控制区配置
            if (area.ByteLength <= 0)
            {
                AppLogger.Warn("PLC 控制区域字节长度未配置，写入操作已跳过。");
                return;
            }

            byte[] normalizedBuffer; // 规范化后的写入数据
            if (buffer.Length == area.ByteLength)
            {
                normalizedBuffer = buffer;
            }
            else
            {
                normalizedBuffer = new byte[area.ByteLength];
                var copyLength = Math.Min(buffer.Length, normalizedBuffer.Length); // 实际复制长度
                Array.Copy(buffer, normalizedBuffer, copyLength);

                if (buffer.Length != area.ByteLength)
                {
                    AppLogger.Warn($"控制区写入数据长度与配置不符，已调整为 {area.ByteLength} 字节 (输入 {buffer.Length} 字节)。");
                }
            }

            await EnsurePlcConnectedAsync(cancellationToken).ConfigureAwait(false);

            await _plcLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_plc == null)
                {
                    return;
                }

                await Task.Run(() => _plc.WriteBytes(DataType.DataBlock, area.DbNumber, area.StartByte, normalizedBuffer), cancellationToken)
                    .ConfigureAwait(false);
                var hexPayload = BitConverter.ToString(normalizedBuffer).Replace("-", string.Empty); // 写入数据的十六进制表示
                AppLogger.Info($"已向 PLC 控制区写入 {normalizedBuffer.Length} 字节，数据(HEX): {hexPayload}");
            }
            finally
            {
                _plcLock.Release();
            }
        }

        /// <summary>
        /// 将布尔位集合转换为 PLC 写入所需的字节数组，低位在前按位拼接。
        /// </summary>
        /// <param name="bits">已经清理与补齐的布尔位集合。</param>
        /// <param name="byteLength">目标输出字节长度。</param>
        /// <returns>与控制区字节长度匹配的缓冲区。</returns>
        private static byte[] BuildControlBuffer(IReadOnlyList<bool> bits, int byteLength)
        {
            if (bits == null)
            {
                throw new ArgumentNullException(nameof(bits));
            }

            if (byteLength <= 0)
            {
                return Array.Empty<byte>();
            }

            var buffer = new byte[byteLength]; // 输出缓冲
            var maxBits = byteLength * 8; // 最大位数

            for (var i = 0; i < maxBits && i < bits.Count; i++)
            {
                if (!bits[i])
                {
                    continue;
                }

                var byteIndex = i / 8; // 目标字节索引
                var bitOffset = i % 8; // 位偏移
                buffer[byteIndex] |= (byte)(1 << bitOffset);
            }

            return buffer;
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
        /// 启动后台轮询线程，从 PLC 读取状态并发布到指定主题。
        /// </summary>
        /// <param name="startupToken">外部启动时传入的取消标记。</param>
        private void StartStatusPollingLoop(CancellationToken startupToken)
        {
            if (_statusPollingTask != null && !_statusPollingTask.IsCompleted)
            {
                return;
            }

            if (_config.StatusArea == null || _config.StatusArea.ByteLength <= 0)
            {
                AppLogger.Warn("未配置 PLC 状态区域，后台轮询任务未启动。");
                return;
            }

            if (string.IsNullOrWhiteSpace(_config.StatusPublishTopic))
            {
                AppLogger.Warn("未配置 PLC 状态上报主题，后台轮询任务未启动。");
                return;
            }

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(startupToken);
            _pollingCts = linkedCts;
            _statusPollingTask = Task.Run(() => RunStatusPollingLoopAsync(linkedCts.Token), CancellationToken.None);
            AppLogger.Info("PLC 状态轮询任务已启动。");
        }

        /// <summary>
        /// 停止后台状态轮询任务，确保资源正确释放。
        /// </summary>
        private async Task StopStatusPollingLoopAsync()
        {
            if (_statusPollingTask == null)
            {
                return;
            }

            try
            {
                _pollingCts?.Cancel();
                await _statusPollingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore cancellation
            }
            finally
            {
                _pollingCts?.Dispose();
                _pollingCts = null;
                _statusPollingTask = null;
                AppLogger.Info("PLC 状态轮询任务已停止。");
            }
        }

        /// <summary>
        /// 按照固定周期轮询 PLC 状态并通过 MQTT 发布字符串报文。
        /// </summary>
        /// <param name="cancellationToken">用于终止任务的取消标记。</param>
        private async Task RunStatusPollingLoopAsync(CancellationToken cancellationToken)
        {
            var interval = Math.Max(100, _config.PollingIntervalMilliseconds); // 轮询间隔

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var statusBytes = await ReadStatusAreaAsync(cancellationToken).ConfigureAwait(false);
                    var payload = BuildStatusPayload(statusBytes); // 字符串报文

                    if (payload != null)
                    {
                        await _mqttBridge.SendStringAsync(payload, Encoding.UTF8, _config.StatusPublishTopic, false, cancellationToken)
                            .ConfigureAwait(false);
                        //AppLogger.Info($"已发布 PLC 状态，字节数: {statusBytes.Length}");
                        // 新增日志输出，显示十六进制与字符串
                        var hexString = BitConverter.ToString(Encoding.UTF8.GetBytes(payload)).Replace("-", "");
                        AppLogger.Info($"已发送 MQTT 消息，主题: {_config.StatusPublishTopic}, 长度: {payload.Length}, 内容(HEX): {hexString}, 内容(STR): {payload}");

                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException ex)
                {
                    AppLogger.Warn( "PLC 服务已被释放，状态轮询任务即将退出。"+ex);
                    break;
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "PLC 状态轮询过程中发生异常。");
                }

                try
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        private const int StatusDataBitLength = 16; // PLC 状态位固定长度
        /// <summary>
        /// 将 PLC 状态字节整理为业务所需的位字符串，避免额外的 JSON 封装。
        /// </summary>
        /// <param name="statusBytes">PLC 返回的状态字节。</param>
        /// <returns>按位编码的字符串，如果无有效数据则返回 null。</returns>
        private string? BuildStatusPayload(byte[] statusBytes)
        {
            if (statusBytes == null || statusBytes.Length == 0)
            {
                AppLogger.Warn("PLC 状态轮询返回空数据。");
                return null;
            }

            var bitString = ConvertToBitString(statusBytes); // 状态位字符串
            var normalizedData = NormalizeBitString(bitString, StatusDataBitLength); // 裁剪后的状态位

            return normalizedData;
        }


        /// <summary>
        /// 将字节数组转换为 01 字符串，便于前后端传输与解析。
        /// </summary>
        /// <param name="bytes">待转换的字节数组。</param>
        /// <returns>由 0 和 1 组成的位字符串。</returns>
        private static string ConvertToBitString(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 8); // 字符串构造器
            foreach (var b in bytes)
            {
                builder.Append(Convert.ToString(b, 2).PadLeft(8, '0'));
            }

            return builder.ToString();
        }


        /// <summary>
        /// 根据指定长度裁剪或填充状态位字符串，确保下行数据满足业务期望。
        /// </summary>
        /// <param name="bitString">原始的状态位字符串。</param>
        /// <param name="requiredLength">期望输出的位数。</param>
        /// <returns>满足长度要求的状态位字符串。</returns>
        private static string NormalizeBitString(string bitString, int requiredLength)
        {
            var safeLength = Math.Max(0, requiredLength); // 期望长度

            if (string.IsNullOrEmpty(bitString))
            {
                return new string('0', safeLength);
            }

            if (bitString.Length >= safeLength)
            {
                return bitString.Substring(0, safeLength);
            }

            var paddingLength = safeLength - bitString.Length; // 需要填充的位数
            return bitString + new string('0', paddingLength);
        }

    }
}