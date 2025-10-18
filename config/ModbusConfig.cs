using System.IO.Ports;

namespace WpfVideoPet.config
{
    public sealed class ModbusConfig
    {
        public bool Enabled { get; set; } = false;

        public string PortName { get; set; } = "COM1";

        public int BaudRate { get; set; } = 38400;

        public Parity Parity { get; set; } = Parity.None;

        public int DataBits { get; set; } = 8;

        public StopBits StopBits { get; set; } = StopBits.One;

        public byte SlaveAddress { get; set; } = 1;

        public int ReadTimeout { get; set; } = 1000;

        public int WriteTimeout { get; set; } = 1000;
    }
}