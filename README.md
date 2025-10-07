# WpfVideoPet

## Modbus RTU 继电器模块

项目内置 `ModbusRtuRelayClient`，用于通过 RS-485 接口对 8 路继电器进行读写。对应的通信参数可在 `webrtcsettings.json` 的 `modbus` 段中配置，包括串口号、波特率、校验位、停止位以及从站地址等。

示例用法：

```csharp
var appConfig = AppConfig.Load();
await using var modbus = new ModbusRtuRelayClient(appConfig.Modbus);

// 读取 8 路继电器当前状态
var states = await modbus.ReadAllChannelsAsync();

// 打开第 3 路继电器
await modbus.SetChannelStateAsync(3, true);

// 一次性写入全部 8 路状态
await modbus.SetAllChannelsAsync(new [] { true, true, false, false, true, false, true, false });
```

`ModbusConfig.Enabled` 默认为 `false`，如需启用请在配置文件中显式设为 `true` 并填写正确的串口参数。