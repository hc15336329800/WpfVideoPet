using S7.Net;
using System;
using System.Diagnostics;
using System.Threading;
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
                _mqttBridge.MessageReceived += _controlHandler;
                _controlSubscribed = true;
                AppLogger.Info($"已挂载 PLC 控制消息回调，监听主题: {_config.ControlSubscribeTopic}");
            }
            else if (string.IsNullOrWhiteSpace(_config.ControlSubscribeTopic))
            {
                AppLogger.Warn("未配置 PLC 控制订阅主题，控制指令将被忽略。");
            }

            await EnsurePlcConnectedAsync(cancellationToken).ConfigureAwait(false);
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
        /// 处理来自 MQTT 控制主题的指令。
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
    }
}