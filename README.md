# WpfVideoPet

## Modbus RTU �̵���ģ��

��Ŀ���� `ModbusRtuRelayClient`������ͨ�� RS-485 �ӿڶ� 8 ·�̵������ж�д����Ӧ��ͨ�Ų������� `webrtcsettings.json` �� `modbus` �������ã��������ںš������ʡ�У��λ��ֹͣλ�Լ���վ��ַ�ȡ�

ʾ���÷���

```csharp
var appConfig = AppConfig.Load();
await using var modbus = new ModbusRtuRelayClient(appConfig.Modbus);

// ��ȡ 8 ·�̵�����ǰ״̬
var states = await modbus.ReadAllChannelsAsync();

// �򿪵� 3 ·�̵���
await modbus.SetChannelStateAsync(3, true);

// һ����д��ȫ�� 8 ·״̬
await modbus.SetAllChannelsAsync(new [] { true, true, false, false, true, false, true, false });
```

`ModbusConfig.Enabled` Ĭ��Ϊ `false`�������������������ļ�����ʽ��Ϊ `true` ����д��ȷ�Ĵ��ڲ�����