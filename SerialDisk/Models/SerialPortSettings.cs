﻿using System.IO.Ports;

namespace AtariST.SerialDisk.Models
{
    public class SerialPortSettings
    {
        public string PortName { get; set; } = "COM1";
        public Handshake Handshake { get; set; } = Handshake.None;
        public int BaudRate { get; set; } = 9600;
        public int DataBits { get; set; } = 8;
        public StopBits StopBits { get; set; } = StopBits.One;
        public Parity Parity { get; set; } = Parity.None;
    }
}
