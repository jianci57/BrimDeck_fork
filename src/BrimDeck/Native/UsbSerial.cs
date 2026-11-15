using System.IO;
using System.IO.Ports;
using Microsoft.Win32;

namespace BrimDeck.Native;

// A USB device's own serial port (a CDC interface of the same vendor and product), for the few devices whose battery is read
// there rather than over HID (AtkHeadsets.SerialRequest). Only reads: the port is opened for one exchange and closed again,
// with no modem line raised (DTR and RTS stay off), and a port another program holds (the vendor's app) is left alone.
internal static class UsbSerial
{
    // The COM ports Windows has given the device's serial interface, from the registry entries of its USB instances
    // (HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_vvvv&PID_pppp[&MI_nn]\<instance>\Device Parameters\PortName), kept to those
    // present now.
    public static IReadOnlyList<string> Ports(int vendor, int product)
    {
        var found = new List<string>();
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb is null) return found;
            string prefix = $"VID_{vendor:X4}&PID_{product:X4}";
            var present = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in usb.GetSubKeyNames().Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                using var device = usb.OpenSubKey(name);
                if (device is null) continue;
                foreach (var instance in device.GetSubKeyNames())
                {
                    using var parameters = device.OpenSubKey(instance + @"\Device Parameters");
                    if (parameters?.GetValue("PortName") is string port && present.Contains(port) && !found.Contains(port, StringComparer.OrdinalIgnoreCase))
                        found.Add(port);
                }
            }
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return found;
    }

    // One request and its answer: the request written, then the packets read (each packetLength bytes) until one parses or
    // timeoutMs passes. Null when the port could not be opened (held by another program, gone) or nothing answered.
    public static T? Exchange<T>(string port, int baud, byte[] request, int packetLength, Func<byte[], T?> parse, int timeoutMs) where T : struct
    {
        try
        {
            using var serial = new SerialPort(port, baud, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None, DtrEnable = false, RtsEnable = false, ReadTimeout = 100, WriteTimeout = 1000,
            };
            serial.Open();
            serial.DiscardInBuffer();
            serial.Write(request, 0, request.Length);
            var packet = new byte[packetLength];
            int filled = 0;
            long deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                int read;
                try { read = serial.Read(packet, filled, packetLength - filled); }
                catch (TimeoutException) { continue; }
                filled += read;
                if (filled < packetLength) continue;
                if (parse(packet) is { } value) return value;
                filled = 0;
            }
            return null;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            return null;
        }
    }
}
