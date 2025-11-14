﻿using S7.Net;
using System;
using System.Text;
using WpfVideoPet.mqtt;
using System.Text.Json;
using static WpfVideoPet.mqtt.MqttCoreService;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

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
            
        private const int StatusDataBitLength = 16; // PLC 状态上报固定输出16 位  

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
            : this(appConfig?.MqttPlc ?? throw new ArgumentNullException(nameof(appConfig)), mqttBridge)
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
            if (!TryValidateArea(area, out var validationError))
            {
                // 配置参数不合法时立刻终止调用，避免向 PLC 发送无效请求。
                throw new ArgumentOutOfRangeException(nameof(area), validationError);
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
                {
                    try
                    {
                        return _plc.ReadBytes(DataType.DataBlock, area.DbNumber, area.StartByte, area.ByteLength) ?? Array.Empty<byte>();
                    }
                    catch (PlcException ex)
                    {
                        // 捕获底层 PLC 异常并加入区域信息，便于定位配置错误或越界问题。
                        AppLogger.Error(ex,
                            $"读取 PLC 区域失败，DB={area.DbNumber}, Start={area.StartByte}, Length={area.ByteLength}。");
                        throw;
                    }
                }, cancellationToken).ConfigureAwait(false);
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
        /// 处理来自 MQTT 控制主题的指令，将消息中的位字符串解析为控制位批量写入 PLC。
        /// 支持同时置位与复位多个通道，遇到非法字符时立即忽略本次写入。
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

            var bitString = Encoding.UTF8.GetString(message.Payload.Span).Trim(); // 原始控制字符串
            if (string.IsNullOrWhiteSpace(bitString))
            {
                AppLogger.Warn("PLC 控制消息为空，已忽略。");
                return;
            }
            AppLogger.Info($"接收到 PLC 控制原始消息，主题: {message.Topic}, 内容长度: {bitString.Length}, 内容(BIT): {bitString}");

            var area = _config.ControlArea; // 控制区域配置
            if (area.ByteLength <= 0)
            {
                AppLogger.Warn("PLC 控制区域字节长度未配置，写入操作已跳过。");
                return;
            }

            var maxBits = area.ByteLength * 8; // 控制区位容量
            if (TryParseSingleDeviceCommand(bitString, out var singleDeviceCommand))
            {
                var (deviceCode, powerOn) = singleDeviceCommand.Value;
                var bitIndex = deviceCode; // 目前设备编码与控制位索引一一对应
                if (bitIndex < 0 || bitIndex >= maxBits)
                {
                    AppLogger.Warn($"单点 PLC 控制位索引越界，指令已忽略。设备编码: {deviceCode}, 合法范围: 0-{maxBits - 1}");
                    return;
                }

                AppLogger.Info($"解析到单点 PLC 控制指令，设备编码: {deviceCode}, 目标位: {bitIndex}, 电源状态: {powerOn}");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await WriteControlBitAsync(bitIndex, powerOn, CancellationToken.None).ConfigureAwait(false);
                        AppLogger.Info($"单点 PLC 控制指令执行成功，设备编码: {deviceCode}, 位: {bitIndex}, 状态: {powerOn}");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error(ex, $"单点 PLC 控制指令执行失败，设备编码: {deviceCode}, 位: {bitIndex}, 状态: {powerOn}");
                    }
                });
                return;
            }

            var parsedBits = new List<bool>(maxBits); // 已解析的控制位集合
            var truncated = false; // 是否出现截断
            foreach (var ch in bitString)
            {
                if (ch == '0' || ch == '1')
                {
                    if (parsedBits.Count < maxBits)
                    {
                        parsedBits.Add(ch == '1');
                    }
                    else
                    {
                        truncated = true;
                    }
                    continue;
                }

                if (!char.IsWhiteSpace(ch))
                {
                    AppLogger.Warn($"检测到非法的 PLC 控制位字符: {ch}");
                    return;
                }
            }

            if (parsedBits.Count == 0)
            {
                AppLogger.Warn("PLC 控制消息未包含有效位信息，已忽略。");
                return;
            }

            if (truncated)
            {
                AppLogger.Warn($"PLC 控制消息长度超出配置容量，已仅保留前 {maxBits} 位。");
            }

            var targetBits = new bool[maxBits]; // 最终写入位缓冲
            for (var i = 0; i < parsedBits.Count; i++)
            {
                targetBits[i] = parsedBits[i];
            }

            var normalizedPayload = string.Concat(targetBits.Select(bit => bit ? '1' : '0')); // 日志使用的最终位串
            AppLogger.Info($"准备批量写入 PLC 控制位，DB: {area.DbNumber}, 起始字节: {area.StartByte}, 写入位数: {parsedBits.Count}, 最终(BIT): {normalizedPayload}");

            _ = Task.Run(async () =>
            {
                try
                {
                    await WriteControlBitsAsync(targetBits, CancellationToken.None).ConfigureAwait(false);
                    AppLogger.Info($"已批量写入 PLC 控制位，长度: {targetBits.Length}。");
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "批量写入 PLC 控制位失败。");
                }
            });
        }

        /// <summary>
        /// 尝试将 MQTT 载荷解析为单设备控制指令（JSON 格式）。
        /// </summary>
        /// <param name="payload">原始 MQTT 消息文本。</param>
        /// <param name="command">成功解析时输出的设备编码与目标电源状态。</param>
        /// <returns>当载荷为单设备控制 JSON 指令时返回 true。</returns>
        private static bool TryParseSingleDeviceCommand(string payload, out (int DeviceCode, bool PowerOn)? command)
        {
            command = null;
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            var trimmed = payload.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
            {
                return false; // 明确只处理 JSON 文本，避免对位串产生噪声日志。
            }

            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;

                if (!root.TryGetProperty("deviceCode", out var deviceCodeElement) ||
                    !root.TryGetProperty("powerOn", out var powerOnElement))
                {
                    return false; // 非预期 JSON 结构，交由位串逻辑继续处理。
                }

                if (deviceCodeElement.ValueKind != JsonValueKind.Number)
                {
                    return false;
                }

                var deviceCode = deviceCodeElement.GetInt32();
                var powerOn = powerOnElement.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String when bool.TryParse(powerOnElement.GetString(), out var parsedBool) => parsedBool,
                    JsonValueKind.Number => powerOnElement.GetInt32() != 0,
                    _ => (bool?)null
                };

                if (powerOn is null)
                {
                    return false;
                }

                command = (deviceCode, powerOn.Value);
                return true;
            }
            catch (JsonException)
            {
                return false; // JSON 解析失败时回退到位串逻辑。
            }
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

                await Task.Run(() =>
                {
                    try
                    {
                        _plc.WriteBytes(DataType.DataBlock, area.DbNumber, area.StartByte, normalizedBuffer);
                    }
                    catch (PlcException ex)
                    {
                        // 写入失败时输出详细区域信息，帮助快速定位越界或地址错误。
                        AppLogger.Error(ex,
                            $"写入 PLC 区域失败，DB={area.DbNumber}, Start={area.StartByte}, Length={normalizedBuffer.Length}。");
                        throw;
                    }
                }, cancellationToken).ConfigureAwait(false);
                var hexPayload = BitConverter.ToString(normalizedBuffer).Replace("-", string.Empty); // 写入数据的十六进制表示
                AppLogger.Info($"已向 PLC 控制区写入 {normalizedBuffer.Length} 字节，数据(HEX): {hexPayload}");
            }
            finally
            {
                _plcLock.Release();
            }
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
            if (!TryValidateArea(_config.StatusArea, out var validationError))
            {
                AppLogger.Error($"PLC 状态区域配置无效，后台轮询任务未启动。原因: {validationError}");
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


        //private const int StatusDataBitLength = 16; // 固定上报的状态位数量

        /// <summary>
        /// 按固定周期轮询 PLC 状态并将整理后的位字符串通过 MQTT 发布，同时保留原始 HEX 日志便于排查。
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
                    var payload = BuildStatusPayload(statusBytes); // 状态位字符串

                    if (!string.IsNullOrEmpty(payload))
                    {
                        var topic = _config.StatusPublishTopic; // 发布主题
                        if (!string.IsNullOrWhiteSpace(topic))
                        {
                            await _mqttBridge.SendStringAsync(payload, Encoding.UTF8, topic, false, cancellationToken).ConfigureAwait(false);
                            var hexString = statusBytes.Length > 0 ? Convert.ToHexString(statusBytes) : string.Empty; // HEX 日志
                            AppLogger.Info($"已发布 PLC 状态，主题: {topic}, 字节数: {statusBytes.Length}, 内容(HEX): {hexString}, 内容(BIT): {payload}");
                        }
                        else
                        {
                            AppLogger.Warn("未配置 PLC 状态发布主题，状态消息已跳过。");
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException ex)
                {
                    AppLogger.Warn($"PLC 服务已被释放，状态轮询任务即将退出。{ex}");
                    break;
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex,
                                         $"PLC 状态轮询过程中发生异常，DB={_config.StatusArea?.DbNumber}, Start={_config.StatusArea?.StartByte}, Length={_config.StatusArea?.ByteLength}。");
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

        /// <summary>
        /// 将 PLC 状态字节整理为业务期望的位字符串，缺失时自动补零以保证长度一致。
        /// </summary>
        /// <param name="statusBytes">PLC 返回的状态字节。</param>
        /// <returns>整理后的位字符串，若无有效数据则返回 null。</returns>
        private string? BuildStatusPayload(byte[] statusBytes)
        {
            if (statusBytes == null || statusBytes.Length == 0)
            {
                AppLogger.Warn("PLC 状态轮询返回空数据。");
                return null;
            }

            var bitString = ConvertToBitString(statusBytes); // 原始状态位
            if (bitString.Length < StatusDataBitLength)
            {
                AppLogger.Warn($"PLC 状态字节不足，期望 {StatusDataBitLength} 位，实际 {bitString.Length} 位。将自动补齐零位。");
            }
            else if (bitString.Length > StatusDataBitLength)
            {
                AppLogger.Info($"PLC 状态字节包含 {bitString.Length} 位数据，按照配置裁剪至 {StatusDataBitLength} 位。");
            }

            return NormalizeBitString(bitString, StatusDataBitLength); // 固定长度补齐/裁剪
        }



        /// <summary>
        /// 将字节数组转换为 01 字符串，便于前后端传输与解析。
        /// </summary>
        /// <param name="bytes">待转换的字节数组。</param>
        /// <returns>由 0 和 1 组成的位字符串。</returns>
        private static string ConvertToBitString(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(bytes.Length * 8); // 字符串构造器
            var diagnostic = new StringBuilder(bytes.Length * 28); // 记录每个字节的转换明细，便于排查位序问题

            for (var byteIndex = 0; byteIndex < bytes.Length; byteIndex++)
            {
                var currentByte = bytes[byteIndex];
                var segment = new char[8];

                for (var bitIndex = 0; bitIndex < 8; bitIndex++)
                {
                    var bitValue = (currentByte >> bitIndex) & 0x01; // LSB 在左
                    var bitChar = bitValue == 1 ? '1' : '0';
                    segment[bitIndex] = bitChar;
                    builder.Append(bitChar);
                }

                if (diagnostic.Length > 0)
                {
                    diagnostic.Append(' ');
                }

                diagnostic.Append('[')
                          .Append(byteIndex)
                          .Append("] = 0x")
                          .Append(currentByte.ToString("X2"))
                          .Append(" -> ")
                          .Append(segment);
            }

            var result = builder.ToString();
            AppLogger.Info($"PLC 状态字节按 LSB→MSB 顺序转换完成，合并位串: {result}，逐字节明细: {diagnostic}。");
            return result;
        }

        /// <summary>
        /// 根据指定长度裁剪或补齐状态位字符串，统一 MQTT 下行格式。
        /// </summary>
        /// <param name="bitString">原始状态位字符串。</param>
        /// <param name="requiredLength">期望输出长度。</param>
        /// <returns>满足长度要求的状态位字符串。</returns>
        private static string NormalizeBitString(string bitString, int requiredLength)
        {
            var safeLength = Math.Max(0, requiredLength); // 目标长度

            if (string.IsNullOrEmpty(bitString))
            {
                return new string('0', safeLength);
            }

            if (bitString.Length >= safeLength)
            {
                return bitString.Substring(0, safeLength);
            }

            var paddingLength = safeLength - bitString.Length; // 需要补足的位数
            return bitString + new string('0', paddingLength);
        }

        /// <summary>
        /// 检查 PLC 区域配置是否合法，防止发送越界请求导致 PLC 返回错误码。
        /// </summary>
        /// <param name="area">待验证的 PLC 区域。</param>
        /// <param name="error">当验证失败时返回的错误描述。</param>
        /// <returns>配置有效返回 true，否则为 false。</returns>
        private static bool TryValidateArea(PlcAreaConfig area, out string error)
        {
            if (area.DbNumber < 1)
            {
                error = $"DB 编号必须大于 0，当前值为 {area.DbNumber}";
                return false;
            }

            if (area.StartByte < 0)
            {
                error = $"起始字节不能为负数，当前值为 {area.StartByte}";
                return false;
            }

            if (area.ByteLength <= 0)
            {
                error = $"字节长度必须大于 0，当前值为 {area.ByteLength}";
                return false;
            }

            if (area.StartByte + area.ByteLength > ushort.MaxValue)
            {
                error = $"起始字节与长度之和超出 S7 请求允许的最大范围 ({ushort.MaxValue})。";
                return false;
            }

            error = string.Empty;
            return true;
        }

    }
}