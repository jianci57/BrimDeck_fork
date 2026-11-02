using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32.SafeHandles;

namespace BrimDeck.Sensors;

// One PawnIO module, loaded into a handle of its own (the driver keeps one module per handle). The driver's device opens only
// for SYSTEM and administrators; it loads only modules signed by PawnIO's author, so the module files are the official ones.
// A function is called by its name (32 bytes, NUL-padded) followed by its 64-bit inputs, and answers with its 64-bit outputs
// (PawnIOLib's pawnio_execute). The control codes are CTL_CODE(41394, 0x821 / 0x841, METHOD_BUFFERED, FILE_ANY_ACCESS).
internal sealed class PawnIo : IDisposable
{
    private const uint DeviceType = 41394, LoadBinary = DeviceType << 16 | 0x821 << 2, ExecuteFunction = DeviceType << 16 | 0x841 << 2;
    private const int NameLength = 32;
    private readonly SafeFileHandle _handle;

    private PawnIo(SafeFileHandle handle) => _handle = handle;

    // The module, or null when the driver is not there (not installed, or it would not start) or refuses the module (it is
    // not for this processor).
    public static PawnIo? Load(byte[] module, out bool driver)
    {
        StartDriver();
        var handle = CreateFileW(@"\\?\GLOBALROOT\Device\PawnIO", GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        driver = !handle.IsInvalid;
        if (!driver) { handle.Dispose(); return null; }
        if (!DeviceIoControl(handle, LoadBinary, module, module.Length, null, 0, out _, IntPtr.Zero)) { handle.Dispose(); return null; }
        return new PawnIo(handle);
    }

    // The driver is a service that starts on demand; the installer usually leaves it running.
    private static void StartDriver()
    {
        try
        {
            using var service = new ServiceController("PawnIO");
            if (service.Status == ServiceControllerStatus.Stopped) { service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5)); }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or System.ServiceProcess.TimeoutException) { }
    }

    public ulong[]? Execute(string name, ulong[] input, int outputs)
    {
        var request = new byte[NameLength + input.Length * 8];
        System.Text.Encoding.ASCII.GetBytes(name, 0, Math.Min(name.Length, NameLength - 1), request, 0);
        Buffer.BlockCopy(input, 0, request, NameLength, input.Length * 8);
        var reply = new byte[outputs * 8];
        if (!DeviceIoControl(_handle, ExecuteFunction, request, request.Length, reply, reply.Length, out int read, IntPtr.Zero) || read < reply.Length) return null;
        var output = new ulong[outputs];
        Buffer.BlockCopy(reply, 0, output, 0, reply.Length);
        return output;
    }

    public ulong? ReadMsr(uint msr) => Execute("ioctl_read_msr", [msr], 1)?[0];
    public uint? ReadSmn(uint address) => Execute("ioctl_read_smn", [address], 1) is { } value ? (uint)value[0] : null;

    public void Dispose() => _handle.Dispose();

    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[]? output, int outputSize, out int returned, IntPtr overlapped);
}
