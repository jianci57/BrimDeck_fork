using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using BrimDeck.Native;

internal static class CaptureChecks
{
    public static void Run(Action<string, bool> check)
    {
        var window = new Window { Title = "BrimDeck capture test", Width = 160, Height = 80,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Black,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true };
        window.SourceInitialized += (_, _) => WindowsHost.ConfigureOverlay(window);
        try
        {
            window.Show();
            var handle = new WindowInteropHelper(window).Handle;
            check("overlay delivers mouse clicks without activation", SendMessage(handle, 0x21, IntPtr.Zero, new IntPtr(0x02010001)) == new IntPtr(3));
            long style = GetWindowLongPtrW(handle, -20).ToInt64();
            check("overlay has no WGC-incompatible no-activate style", (style & 0x08000000) == 0);
            check("transparent overlay supports WGC", CreateItem(handle) == 0);
        }
        finally { window.Close(); }
    }

    private static int CreateItem(IntPtr handle)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var text));
        CaptureInterop factory;
        try { var iid = typeof(CaptureInterop).GUID; Marshal.ThrowExceptionForHR(RoGetActivationFactory(text, ref iid, out factory)); }
        finally { WindowsDeleteString(text); }
        try
        {
            var iid = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            int hr = factory.CreateForWindow(handle, ref iid, out var item);
            if (item != IntPtr.Zero) Marshal.Release(item);
            return hr;
        }
        finally { Marshal.ReleaseComObject(factory); }
    }
    [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface CaptureInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr handle, ref Guid iid, out IntPtr item);
        [PreserveSig] int CreateForMonitor(IntPtr handle, ref Guid iid, out IntPtr item);
    }
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out IntPtr text);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr text);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr text, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out CaptureInterop factory);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
}
