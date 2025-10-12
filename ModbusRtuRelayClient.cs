using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Text;

namespace WpfVideoPet
{
    /// <summary>
    /// 为 RS485 继电器模块提供 Modbus RTU 读写能力，支持读取与写入 8 路开关量。
    /// </summary>
    public sealed class ModbusRtuRelayClient : IAsyncDisposable
    {
        private readonly SerialPort _serialPort;
        private readonly byte _slaveAddress;
        private readonly SemaphoreSlim _ioLock = new(1, 1);
        private bool _disposed;

        public ModbusRtuRelayClient(ModbusConfig config)
        {
            if (config is null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            _slaveAddress = config.SlaveAddress;
            _serialPort = new SerialPort(config.PortName, config.BaudRate, config.Parity, config.DataBits, config.StopBits)
            {
                ReadTimeout = config.ReadTimeout <= 0 ? SerialPort.InfiniteTimeout : config.ReadTimeout,
                WriteTimeout = config.WriteTimeout <= 0 ? SerialPort.InfiniteTimeout : config.WriteTimeout,
                Handshake = Handshake.None
            };

            // 在实例化阶段输出关键 Modbus 参数，便于定位端口配置问题。
            AppLogger.Info(
                $"已创建 Modbus 客户端: 从站地址={_slaveAddress}, 串口={_serialPort.PortName}, 波特率={_serialPort.BaudRate}, 数据位={_serialPort.DataBits}, 校验={_serialPort.Parity}, 停止位={_serialPort.StopBits}, 读超时={_serialPort.ReadTimeout}, 写超时={_serialPort.WriteTimeout}。");
        }

        public async Task<bool[]> ReadAllChannelsAsync(CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();
            const int channelCount = 8;
            return await ReadCoilsAsync(0, channelCount, cancellationToken).ConfigureAwait(false);
        }

        public async Task SetChannelStateAsync(int channelIndex, bool isOn, CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();

            if (channelIndex is < 1 or > 8)
            {
                throw new ArgumentOutOfRangeException(nameof(channelIndex), channelIndex, "通道索引必须在 1-8 之间。");
            }

            var address = channelIndex - 1;
            var request = BuildWriteSingleCoilFrame(address, isOn);
            await SendAndValidateAsync(request, 0x05, cancellationToken).ConfigureAwait(false);
        }
        /// <summary>
        /// 读取指定继电器（线圈）的当前状态，返回 <c>true</c> 表示继电器处于导通/打开状态。
        /// </summary>
        /// <param name="channelIndex">继电器编号（1-8）。</param>
        /// <param name="cancellationToken">取消标记。</param>
        /// <returns>继电器的开关状态。</returns>
        /// <exception cref="ArgumentOutOfRangeException">当编号超出 1-8 范围时抛出。</exception>
        public async Task<bool> ReadChannelStateAsync(int channelIndex, CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();

            if (channelIndex is < 1 or > 8)
            {
                throw new ArgumentOutOfRangeException(nameof(channelIndex), channelIndex, "通道索引必须在 1-8 之间。");
            }

            // 设备文档说明：0000H 对应 1 号继电器，0001H 对应 2 号，以此类推。
            var address = channelIndex - 1;
            var coils = await ReadCoilsAsync(address, 1, cancellationToken).ConfigureAwait(false);
            return coils.Length > 0 && coils[0];
        }
        public async Task SetAllChannelsAsync(IReadOnlyList<bool> states, CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();

            if (states is null)
            {
                throw new ArgumentNullException(nameof(states));
            }

            if (states.Count != 8)
            {
                throw new ArgumentException("必须提供 8 个通道的状态。", nameof(states));
            }

            var request = BuildWriteMultipleCoilsFrame(0, states);
            await SendAndValidateAsync(request, 0x0F, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool[]> ReadCoilsAsync(int startAddress, int count, CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();

            if (startAddress < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startAddress), startAddress, "起始地址必须大于或等于 0。");
            }

            if (count is <= 0 or > 2000)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "读取数量需在 1-2000 之间。");
            }
            // 记录即将执行的读取参数，以便在日志中快速定位具体的读取指令。
            AppLogger.Info($"准备读取线圈: 起始地址={startAddress}, 数量={count}。");

            var request = BuildReadCoilsFrame(startAddress, count);
            var response = await SendAndValidateAsync(request, 0x01, cancellationToken).ConfigureAwait(false);
            var byteCount = response[2];
            var expectedByteCount = (count + 7) / 8;

            if (byteCount != expectedByteCount)
            {
                throw new InvalidOperationException($"返回的字节数 {byteCount} 与期望 {expectedByteCount} 不符。");
            }

            var result = new bool[count];
            for (var i = 0; i < count; i++)
            {
                var byteIndex = i / 8;
                var bitIndex = i % 8;
                var value = response[3 + byteIndex];
                result[i] = (value & (1 << bitIndex)) != 0;
            }

            return result;
        }

        private byte[] BuildReadCoilsFrame(int startAddress, int count)
        {
            var frame = new byte[8];
            frame[0] = _slaveAddress;
            frame[1] = 0x01;
            frame[2] = (byte)((startAddress >> 8) & 0xFF);
            frame[3] = (byte)(startAddress & 0xFF);
            frame[4] = (byte)((count >> 8) & 0xFF);
            frame[5] = (byte)(count & 0xFF);
            WriteCrc(frame);
            return frame;
        }

        private byte[] BuildWriteSingleCoilFrame(int address, bool isOn)
        {
            var frame = new byte[8];
            frame[0] = _slaveAddress;
            frame[1] = 0x05;
            frame[2] = (byte)((address >> 8) & 0xFF);
            frame[3] = (byte)(address & 0xFF);
            frame[4] = isOn ? (byte)0xFF : (byte)0x00;
            frame[5] = 0x00;
            WriteCrc(frame);
            return frame;
        }

        private byte[] BuildWriteMultipleCoilsFrame(int startAddress, IReadOnlyList<bool> states)
        {
            var byteCount = (states.Count + 7) / 8;
            var frame = new byte[7 + byteCount + 2];
            frame[0] = _slaveAddress;
            frame[1] = 0x0F;
            frame[2] = (byte)((startAddress >> 8) & 0xFF);
            frame[3] = (byte)(startAddress & 0xFF);
            frame[4] = (byte)((states.Count >> 8) & 0xFF);
            frame[5] = (byte)(states.Count & 0xFF);
            frame[6] = (byte)byteCount;

            for (var i = 0; i < states.Count; i++)
            {
                var byteIndex = i / 8;
                var bitIndex = i % 8;
                if (states[i])
                {
                    frame[7 + byteIndex] |= (byte)(1 << bitIndex);
                }
            }

            WriteCrc(frame);
            return frame;
        }

        private static ushort ComputeCrc(ReadOnlySpan<byte> frame, int length)
        {
            ushort crc = 0xFFFF;
            for (var i = 0; i < length; i++)
            {
                crc ^= frame[i];
                for (var bit = 0; bit < 8; bit++)
                {
                    var lsb = (crc & 0x0001) != 0;
                    crc >>= 1;
                    if (lsb)
                    {
                        crc ^= 0xA001;
                    }
                }
            }

            return crc;
        }

        private static void WriteCrc(Span<byte> frame)
        {
            var crc = ComputeCrc(frame, frame.Length - 2);
            frame[^2] = (byte)(crc & 0xFF);
            frame[^1] = (byte)((crc >> 8) & 0xFF);
        }

        private async Task<byte[]> SendAndValidateAsync(byte[] request, byte functionCode, CancellationToken cancellationToken)
        {
            await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureNotDisposed();
                EnsureOpen();

                cancellationToken.ThrowIfCancellationRequested();
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();

                // 记录原始帧内容，便于与硬件厂家核对收发数据是否正确。
                AppLogger.Info($"发送 Modbus 请求: 功能码=0x{functionCode:X2}, 帧={FormatFrame(request)}");
                _serialPort.Write(request, 0, request.Length);

                if (functionCode is 0x05 or 0x06 or 0x0F or 0x10)
                {
                    var expectedLength = functionCode switch
                    {
                        0x05 or 0x06 => 8,
                        0x0F or 0x10 => 8,
                        _ => request.Length
                    };

                    return ReadFixedLengthResponse(expectedLength, functionCode);
                }

                return ReadVariableLengthResponse(functionCode);
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private byte[] ReadFixedLengthResponse(int length, byte functionCode)
        {
            var buffer = new byte[length];
            ReadExact(buffer);
            ValidateResponse(buffer, functionCode);
            AppLogger.Info($"收到 Modbus 固定长度响应: 功能码=0x{functionCode:X2}, 帧={FormatFrame(buffer)}");
            return buffer;
        }

        private byte[] ReadVariableLengthResponse(byte functionCode)
        {
            var header = new byte[3];
            ReadExact(header);

            if (header[0] != _slaveAddress)
            {
                throw new InvalidOperationException($"收到的从站地址 {header[0]} 与期望 {_slaveAddress} 不一致。");
            }

            if ((header[1] & 0x80) != 0)
            {
                var exceptionTail = new byte[2];
                ReadExact(exceptionTail);
                var frame = Combine(header, exceptionTail);
                ValidateCrc(frame);
                throw new InvalidOperationException($"Modbus 设备返回异常代码: {header[2]}。");
            }

            if (header[1] != functionCode)
            {
                throw new InvalidOperationException($"收到的功能码 {header[1]} 与请求 {functionCode} 不一致。");
            }

            var byteCount = header[2];
            var payload = new byte[byteCount + 2];
            ReadExact(payload);
            var response = Combine(header, payload);
            ValidateCrc(response);
            AppLogger.Info($"收到 Modbus 可变长度响应: 功能码=0x{functionCode:X2}, 帧={FormatFrame(response)}");
            return response;
        }

        private void ValidateResponse(byte[] response, byte functionCode)
        {
            if (response.Length < 5)
            {
                throw new InvalidOperationException("响应长度不足。");
            }

            ValidateCrc(response);

            if (response[0] != _slaveAddress)
            {
                throw new InvalidOperationException($"收到的从站地址 {response[0]} 与期望 {_slaveAddress} 不一致。");
            }

            if ((response[1] & 0x80) != 0)
            {
                throw new InvalidOperationException($"Modbus 设备返回异常代码: {response[2]}。");
            }

            if (response[1] != functionCode)
            {
                throw new InvalidOperationException($"收到的功能码 {response[1]} 与请求 {functionCode} 不一致。");
            }
        }

        private static byte[] Combine(byte[] prefix, byte[] suffix)
        {
            var result = new byte[prefix.Length + suffix.Length];
            Buffer.BlockCopy(prefix, 0, result, 0, prefix.Length);
            Buffer.BlockCopy(suffix, 0, result, prefix.Length, suffix.Length);
            return result;
        }

        private void ValidateCrc(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < 3)
            {
                throw new InvalidOperationException("响应长度不足以包含 CRC。");
            }

            var lengthWithoutCrc = frame.Length - 2;
            var expectedCrc = (ushort)(frame[lengthWithoutCrc] | (frame[lengthWithoutCrc + 1] << 8));
            var actualCrc = ComputeCrc(frame, lengthWithoutCrc);
            if (expectedCrc != actualCrc)
            {
                throw new InvalidOperationException($"CRC 校验失败，期望 {expectedCrc:X4} 实际 {actualCrc:X4}。");
            }
        }

        private void ReadExact(byte[] buffer)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = _serialPort.Read(buffer, offset, buffer.Length - offset);
                if (read <= 0)
                {
                    AppLogger.Warn($"串口读取超时: 期望剩余字节={buffer.Length - offset}。");
                    throw new TimeoutException("读取 Modbus 响应超时。");
                }

                offset += read;
            }
        }

        /// <summary>
        /// 将帧数据转换为十六进制字符串，便于在日志中直观查看原始报文。
        /// </summary>
        private static string FormatFrame(ReadOnlySpan<byte> frame)
        {
            if (frame.IsEmpty)
            {
                return "(空)";
            }

            var builder = new StringBuilder(frame.Length * 5);
            for (var i = 0; i < frame.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(' ');
                }

                builder.Append("0x");
                builder.Append(frame[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
        /// <summary>
        /// 确保串口已开启，若为首次连接则输出详细日志，便于排查连接问题。
        /// </summary>
        private void EnsureOpen()
        {
            if (_serialPort.IsOpen)
            {
                return;
            }

            AppLogger.Info($"准备打开 Modbus 串口连接: 端口={_serialPort.PortName}, 波特率={_serialPort.BaudRate}, 数据位={_serialPort.DataBits}, 校验={_serialPort.Parity}, 停止位={_serialPort.StopBits}。");

            try
            {
                _serialPort.Open();
                AppLogger.Info("Modbus 串口连接已成功打开。");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"打开 Modbus 串口连接失败: {ex.Message}");
                throw;
            }
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ModbusRtuRelayClient));
            }
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;

            try
            {
                if (_serialPort.IsOpen)
                {
                    AppLogger.Info("正在关闭 Modbus 串口连接。");
                }

                _serialPort.Dispose();
                AppLogger.Info("Modbus 串口资源已释放。");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"释放 Modbus 串口资源时发生异常: {ex.Message}");
            }

            _ioLock.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}