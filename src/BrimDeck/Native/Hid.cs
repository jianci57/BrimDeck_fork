using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BrimDeck.Native;

// Windows HID access for the devices page: listing the HID collections, and reading and writing reports with timeouts. Ported
// from device-battery-viewer by CharlesGool (https://github.com/CharlesGool/device-battery-viewer, src/Hid.cs, MIT License;
// see ThirdPartyNotices.txt): enumeration through SetupAPI, collections opened with no access just to read their attributes
// (Windows keeps keyboards and mice to itself), reports read with overlapped I/O and an event per handle, and a request
// answered by the first report that matches it. Adapted: a reply whose first byte is 0 (a device without report ids) can be
// handed over without it, as hidapi does, so the byte offsets the vendors' protocols are documented with apply as written;
// the battery usages' value capabilities are read too (HidBattery).

// One HID collection.
internal sealed class HidInfo
{
    public required string Path;
    public int Vendor, Product, UsagePage, Usage, InputLength, OutputLength, FeatureLength;
    public string ProductName = "", Serial = "";
    // The battery usages the collection's feature reports carry (HidBattery.IsBattery): report id, page, usage, link collection
    // and logical range.
    public List<(byte ReportId, ushort Page, ushort Usage, ushort Link, int Min, int Max)> FeatureBatteries = [];
    // MI_xx in the path: the USB interface; -1 when the path has none.
    public int Interface => System.Text.RegularExpressions.Regex.Match(Path, "mi_([0-9a-f]{2})", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } m
        ? Convert.ToInt32(m.Groups[1].Value, 16) : -1;
    // COLxx in the path: the top-level collection where the interface has several; -1 when the path has none.
    public int Collection => System.Text.RegularExpressions.Regex.Match(Path, "&col([0-9a-f]{2})", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } m
        ? Convert.ToInt32(m.Groups[1].Value, 16) : -1;
    // Reached through Bluetooth (classic or LE) rather than USB.
    public bool Bluetooth => Hid.IsBluetooth(Path);
}

internal static class Hid
{
    // Every present HID collection's path. The paths carry the vendor and product (Ids) and the link (IsBluetooth), so callers
    // filter on them before opening anything.
    public static List<string> Paths()
    {
        var paths = new List<string>();
        HidD_GetHidGuid(out var guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == Invalid) return paths;
        try
        {
            for (int i = 0; ; i++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int need, IntPtr.Zero);
                if (need <= 0) continue;
                IntPtr buffer = Marshal.AllocHGlobal(need + 4);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is 8 in a 64-bit process.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, need, out need, IntPtr.Zero)) paths.Add(Marshal.PtrToStringUni(buffer + 4) ?? "");
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return paths;
    }

    // The vendor and product a path names, without opening it; (0, 0) when it names none.
    public static (int Vendor, int Product) Ids(string path)
    {
        var match = System.Text.RegularExpressions.Regex.Match(path, @"vid[_&][0-9a-f]*?([0-9a-f]{4})[_&]pid[_&]([0-9a-f]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? (Convert.ToInt32(match.Groups[1].Value, 16), Convert.ToInt32(match.Groups[2].Value, 16)) : (0, 0);
    }

    // Whether a path is a collection reached through Bluetooth (classic or LE) rather than USB.
    public static bool IsBluetooth(string path) => path.Contains("{00001124-", StringComparison.OrdinalIgnoreCase) || path.Contains("{00001812-", StringComparison.OrdinalIgnoreCase)
        || path.Contains("bthledevice", StringComparison.OrdinalIgnoreCase);

    // A collection's attributes, opened with no access; null when it cannot be opened. batteries: read the battery usages too.
    // The product name and serial go to the device itself, with no timeout (a Bluetooth device asleep can hold them), which is
    // why callers open only the Bluetooth collections they need (HidBatteries.Spoken).
    public static HidInfo? Query(string path, bool batteries = false)
    {
        IntPtr handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle == Invalid) return null;
        try
        {
            var attributes = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            if (!HidD_GetAttributes(handle, ref attributes)) return null;
            var info = new HidInfo { Path = path, Vendor = attributes.VendorID, Product = attributes.ProductID };
            if (HidD_GetPreparsedData(handle, out var preparsed))
            {
                try
                {
                    var caps = new ushort[32];
                    HidP_GetCaps(preparsed, caps);
                    info.Usage = caps[0]; info.UsagePage = caps[1]; info.InputLength = caps[2]; info.OutputLength = caps[3]; info.FeatureLength = caps[4];
                    if (batteries && caps[30] > 0) info.FeatureBatteries = Batteries(preparsed, caps[30]);
                }
                finally { HidD_FreePreparsedData(preparsed); }
            }
            info.ProductName = Text(handle, HidD_GetProductString);
            info.Serial = Text(handle, HidD_GetSerialNumberString);
            return info;
        }
        finally { CloseHandle(handle); }
    }

    // HIDP_VALUE_CAPS is 72 bytes: the page at 0, the report id at 2, the link collection at 6, IsRange at 12, the logical
    // minimum and maximum at 40 and 44, and the usage (or the first of a range) at 56.
    private static List<(byte, ushort, ushort, ushort, int, int)> Batteries(IntPtr preparsed, int count)
    {
        var found = new List<(byte, ushort, ushort, ushort, int, int)>();
        var buffer = new byte[72 * count];
        ushort length = (ushort)count;
        if (HidP_GetValueCaps(2, buffer, ref length, preparsed) != HIDP_STATUS_SUCCESS) return found;
        for (int i = 0; i < length; i++)
        {
            int at = i * 72;
            ushort page = BitConverter.ToUInt16(buffer, at), usage = BitConverter.ToUInt16(buffer, at + 56);
            if (!Core.HidBattery.IsBattery(page, usage)) continue;
            found.Add((buffer[at + 2], page, usage, BitConverter.ToUInt16(buffer, at + 6), BitConverter.ToInt32(buffer, at + 40), BitConverter.ToInt32(buffer, at + 44)));
        }
        return found;
    }

    // A battery usage's value from a feature report read on the collection.
    public static int? FeatureValue(HidDevice device, (byte ReportId, ushort Page, ushort Usage, ushort Link, int Min, int Max) battery, int length)
    {
        var report = device.GetFeature(battery.ReportId, length, timeoutMs: 300);
        if (report is null) return null;
        IntPtr handle = device.Handle;
        if (!HidD_GetPreparsedData(handle, out var preparsed)) return null;
        try { return HidP_GetUsageValue(2, battery.Page, battery.Link, battery.Usage, out uint value, preparsed, report, (uint)report.Length) == HIDP_STATUS_SUCCESS ? (int)value : null; }
        finally { HidD_FreePreparsedData(preparsed); }
    }

    private delegate bool StringReader(IntPtr handle, byte[] buffer, int length);
    private static string Text(IntPtr handle, StringReader read)
    {
        var buffer = new byte[256];
        if (!read(handle, buffer, buffer.Length)) return "";
        var text = Encoding.Unicode.GetString(buffer);
        int end = text.IndexOf('\0');
        return (end >= 0 ? text[..end] : text).Trim();
    }

    // Flushes the devices, sends, and reads them until match returns a result or the time runs out (null). Reports that do
    // not match (a movement, another program's reply) are passed over.
    public static T? Transact<T>(IList<HidDevice> devices, Action send, Func<byte[], T?> match, int timeoutMs) where T : class
    {
        foreach (var device in devices) device.Flush();
        send();
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            long left = deadline - Environment.TickCount64;
            if (left <= 0) return null;
            if (ReadAny(devices, (int)left, out var data) is not null && data is not null && match(data) is { } result) return result;
        }
    }

    // Waits on several collections at once; returns the one that delivered a report first, or null on timeout.
    public static HidDevice? ReadAny(IList<HidDevice> devices, int timeoutMs, out byte[]? data)
    {
        data = null;
        var events = new IntPtr[devices.Count];
        for (int i = 0; i < devices.Count; i++) { devices[i].ArmRead(); events[i] = devices[i].ReadEvent; }
        uint result = WaitForMultipleObjects((uint)events.Length, events, false, (uint)Math.Max(0, timeoutMs));
        if (result >= events.Length) return null;
        var device = devices[(int)result];
        data = device.CollectRead();
        return device;
    }

    // ----- Interop -----
    internal static readonly IntPtr Invalid = new(-1);
    private const int DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    internal const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    internal const int ERROR_IO_PENDING = 997;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)] private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    // hid.dll's HidD_ functions return BOOLEAN, one byte: read as such, not as a four-byte BOOL.
    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(IntPtr handle, ref HIDD_ATTRIBUTES attributes);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(IntPtr handle, out IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, [Out] ushort[] caps);
    [DllImport("hid.dll")] private static extern int HidP_GetValueCaps(int reportType, [Out] byte[] caps, ref ushort length, IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetUsageValue(int reportType, ushort page, ushort link, ushort usage, out uint value, IntPtr data, byte[] report, uint length);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FlushQueue(IntPtr handle);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetProductString(IntPtr handle, byte[] buffer, int length);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetSerialNumberString(IntPtr handle, byte[] buffer, int length);

    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid cls, IntPtr enumerator, IntPtr window, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid cls, int index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int need, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    internal static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr CreateEvent(IntPtr security, bool manual, bool initial, string? name);
    [DllImport("kernel32.dll")] internal static extern bool ResetEvent(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ReadFile(IntPtr handle, IntPtr buffer, int length, IntPtr read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool WriteFile(IntPtr handle, IntPtr buffer, int length, IntPtr written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetOverlappedResult(IntPtr handle, IntPtr overlapped, out int done, bool wait);
    [DllImport("kernel32.dll")] internal static extern bool CancelIoEx(IntPtr handle, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeviceIoControl(IntPtr handle, uint code, IntPtr input, int inputLength, IntPtr output, int outputLength, IntPtr returned, IntPtr overlapped);
    [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool all, uint milliseconds);

    // OVERLAPPED in unmanaged memory, so it stays put while I/O is under way; its event at offset 2 × pointer + 8.
    internal static IntPtr NewOverlapped()
    {
        int size = 2 * IntPtr.Size + 8 + IntPtr.Size;
        IntPtr overlapped = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i++) Marshal.WriteByte(overlapped, i, 0);
        Marshal.WriteIntPtr(overlapped, 2 * IntPtr.Size + 8, CreateEvent(IntPtr.Zero, true, false, null));
        return overlapped;
    }
    internal static IntPtr OverlappedEvent(IntPtr overlapped) => Marshal.ReadIntPtr(overlapped, 2 * IntPtr.Size + 8);
    internal static void FreeOverlapped(IntPtr overlapped) { CloseHandle(OverlappedEvent(overlapped)); Marshal.FreeHGlobal(overlapped); }
}

// A collection opened for reading and writing, overlapped; one read and one write at a time. Opened without access (readWrite
// false) it can still send and fetch feature reports where Windows keeps the collection to itself (Razer's mouse collection).
internal sealed class HidDevice : IDisposable
{
    public readonly string Path;
    public readonly int InputLength, OutputLength, FeatureLength;
    // Hands over input reports without a leading zero report id, as hidapi does (Hid's header).
    public bool StripZero { get; init; }
    private IntPtr _handle, _readOverlapped, _readBuffer;
    private bool _reading;

    public HidDevice(HidInfo info, bool readWrite = true)
    {
        Path = info.Path;
        _handle = Hid.CreateFile(info.Path, readWrite ? Hid.GENERIC_READ | Hid.GENERIC_WRITE : 0, Hid.FILE_SHARE_READ | Hid.FILE_SHARE_WRITE, IntPtr.Zero, Hid.OPEN_EXISTING,
            Hid.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (_handle == Hid.Invalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        InputLength = info.InputLength > 0 ? info.InputLength : 64;
        OutputLength = info.OutputLength > 0 ? info.OutputLength : 64;
        FeatureLength = info.FeatureLength > 0 ? info.FeatureLength : 64;
        _readOverlapped = Hid.NewOverlapped();
        _readBuffer = Marshal.AllocHGlobal(InputLength);
    }

    internal IntPtr Handle => _handle;
    internal IntPtr ReadEvent => Hid.OverlappedEvent(_readOverlapped);

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        CancelRead();
        Hid.CloseHandle(_handle);
        if (!_abandoned) { Hid.FreeOverlapped(_readOverlapped); Marshal.FreeHGlobal(_readBuffer); }
        _handle = IntPtr.Zero;
    }

    // Drops the input reports still queued in the driver.
    public void Flush() { CancelRead(); Hid.HidD_FlushQueue(_handle); }

    // Writes an output report, data[0] its id, padded with zeros to the report's length.
    public void Write(byte[] data, int timeoutMs = 1000)
    {
        var buffer = new byte[Math.Max(OutputLength, data.Length)];
        Array.Copy(data, buffer, data.Length);
        IntPtr overlapped = Hid.NewOverlapped(), memory = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            Marshal.Copy(buffer, 0, memory, buffer.Length);
            if (!Hid.WriteFile(_handle, memory, buffer.Length, IntPtr.Zero, overlapped))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != Hid.ERROR_IO_PENDING) throw new Win32Exception(error);
                if (Hid.WaitForSingleObject(Hid.OverlappedEvent(overlapped), (uint)timeoutMs) != 0)
                {
                    // As Control: cancelled, waited for a second at most, and left to a driver that ignores the cancel.
                    Hid.CancelIoEx(_handle, overlapped);
                    if (Hid.WaitForSingleObject(Hid.OverlappedEvent(overlapped), 1000) != 0) { memory = IntPtr.Zero; overlapped = IntPtr.Zero; }
                    throw new TimeoutException();
                }
            }
            if (!Hid.GetOverlappedResult(_handle, overlapped, out _, true)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory); if (overlapped != IntPtr.Zero) Hid.FreeOverlapped(overlapped); }
    }

    // Reads one input report, or null after the timeout.
    public byte[]? Read(int timeoutMs = 1000) { Hid.ReadAny([this], timeoutMs, out var data); return data; }

    // Feature reports go as overlapped IOCTLs with a timeout (hidapi's way), not HidD_GetFeature/SetFeature, which wait as long
    // as a device takes: a sleeping device must not hold the reading of every other one. A report that times out is cancelled
    // and throws TimeoutException.
    public byte[]? GetFeature(byte reportId, int length, int timeoutMs = 500)
    {
        var buffer = new byte[Math.Max(length, 2)];
        buffer[0] = reportId;
        return Control(IOCTL_HID_GET_FEATURE, buffer, output: true, timeoutMs) ? buffer : null;
    }
    public bool SetFeature(byte[] data, int timeoutMs = 500)
    {
        var buffer = new byte[Math.Max(FeatureLength, data.Length)];
        Array.Copy(data, buffer, data.Length);
        return Control(IOCTL_HID_SET_FEATURE, buffer, output: false, timeoutMs);
    }
    // An input report got on request (HidD_GetInputReport's IOCTL, with the same timeout), for a device that answers through
    // the report it holds rather than by sending one (Rapoo's first generation).
    public byte[]? GetInput(byte reportId, int length, int timeoutMs = 500)
    {
        var buffer = new byte[Math.Max(length, 2)];
        buffer[0] = reportId;
        return Control(IOCTL_HID_GET_INPUT_REPORT, buffer, output: true, timeoutMs) ? buffer : null;
    }
    private const uint IOCTL_HID_SET_FEATURE = 0xB0191, IOCTL_HID_GET_FEATURE = 0xB0192, IOCTL_HID_GET_INPUT_REPORT = 0xB01A2;
    private bool Control(uint code, byte[] buffer, bool output, int timeoutMs)
    {
        if (_abandoned) throw new IOException("A cancelled read is still pending.");
        IntPtr overlapped = Hid.NewOverlapped(), memory = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            Marshal.Copy(buffer, 0, memory, buffer.Length);
            // The report in both buffers, as hidapi and libusb send it: GET_FEATURE fills the output buffer, and SET_FEATURE
            // (METHOD_IN_DIRECT) takes the report from it.
            if (!Hid.DeviceIoControl(_handle, code, memory, buffer.Length, memory, buffer.Length, IntPtr.Zero, overlapped))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != Hid.ERROR_IO_PENDING) return false;
                if (Hid.WaitForSingleObject(Hid.OverlappedEvent(overlapped), (uint)timeoutMs) != 0)
                {
                    // Cancelled, the request ends at once; a driver that ignores the cancel keeps the buffers, which are then left
                    // to it rather than freed under it.
                    Hid.CancelIoEx(_handle, overlapped);
                    if (Hid.WaitForSingleObject(Hid.OverlappedEvent(overlapped), 1000) != 0) { memory = IntPtr.Zero; overlapped = IntPtr.Zero; }
                    throw new TimeoutException();
                }
            }
            if (!Hid.GetOverlappedResult(_handle, overlapped, out _, true)) return false;
            if (output) Marshal.Copy(memory, buffer, 0, buffer.Length);
            return true;
        }
        finally { if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory); if (overlapped != IntPtr.Zero) Hid.FreeOverlapped(overlapped); }
    }

    internal void ArmRead()
    {
        // A read the driver kept after its cancel still owns the buffer: this collection is not read again.
        if (_abandoned) throw new IOException("A cancelled read is still pending.");
        if (_reading) return;
        Hid.ResetEvent(ReadEvent);
        if (!Hid.ReadFile(_handle, _readBuffer, InputLength, IntPtr.Zero, _readOverlapped))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != Hid.ERROR_IO_PENDING) throw new Win32Exception(error);
        }
        _reading = true;
    }

    internal byte[]? CollectRead()
    {
        bool ok = Hid.GetOverlappedResult(_handle, _readOverlapped, out int done, false);
        _reading = false;
        if (!ok || done <= 0) return null;
        int skip = StripZero && Marshal.ReadByte(_readBuffer) == 0 ? 1 : 0;
        var data = new byte[done - skip];
        Marshal.Copy(_readBuffer + skip, data, 0, data.Length);
        return data;
    }

    // A pending read is cancelled and waited for a second at most; a driver that ignores the cancel keeps the read's buffer,
    // which is then left to it (Dispose does not free it).
    private bool _abandoned;
    private void CancelRead()
    {
        if (!_reading) return;
        Hid.CancelIoEx(_handle, _readOverlapped);
        if (Hid.WaitForSingleObject(ReadEvent, 1000) == 0) Hid.GetOverlappedResult(_handle, _readOverlapped, out _, false);
        else _abandoned = true;
        _reading = false;
    }
}
