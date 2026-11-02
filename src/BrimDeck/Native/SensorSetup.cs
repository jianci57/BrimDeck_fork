using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ServiceProcess;
using BrimDeck.Core;
using Microsoft.Win32;

namespace BrimDeck.Native;

// Enhanced sensors (the settings page "增强传感器"): the processor's temperature and power read through the PawnIO driver, which
// opens only for SYSTEM and administrators. The user installs PawnIO itself (winget, or its own installer); BrimDeck then sets up
// its sensor service once, with one administrator prompt, and stays a normal process that asks the service for readings
// (SensorClient). Neither is in BrimDeck's installer: the service is downloaded from this version's GitHub release and must match
// the SHA-256 the release build wrote into BrimDeck (SensorsSha256); PawnIO's two modules come from PawnIO's own signed release,
// pinned by its SHA-256 here. The service and its modules go to Program Files, which only administrators may change: BrimDeck
// itself sits in the user's own folders, and a SYSTEM service run from there could be replaced by anything the user runs.
internal enum SensorSetupState { NoPawnIo, NotEnabled, Enabled }

internal static class SensorSetup
{
    internal const string ElevatedSwitch = "--sensor-service";
    public const string ServiceName = "BrimDeckSensors", ExeName = "BrimDeck.Sensors.exe";
    private const string ModulesUrl = "https://github.com/namazso/PawnIO.Modules/releases/download/0.2.11/release_0_2_11.zip";
    private const string ModulesSha256 = "43608cb89bc84247fef1368a139013f7d043e17db6d6c8dfc9b46bf0905a81f4";
    private static readonly string[] Modules = ["IntelMSR.bin", "AMDFamily17.bin", "COPYING"];
    // Interactive users may start the service and read its state (RP, LC); only administrators and SYSTEM may change or stop it.
    private const string ServiceSecurity = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPLOCRRC;;;IU)";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2), DefaultRequestHeaders = { { "User-Agent", "BrimDeck" } } };

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BrimDeck Sensors");
    public static string ServiceExe => Path.Combine(Folder, ExeName);
    private static string? ExpectedSha256 => typeof(SensorSetup).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "SensorsSha256")?.Value is { Length: 64 } hash ? hash.ToLowerInvariant() : null;

    // PawnIO's version as its installer registered it, or null when it is not installed. Installed by other software (FanControl,
    // LibreHardwareMonitor) it is the same installer, so the user is not asked to install it again. Its driver's service alone does
    // not count: PawnIO's uninstaller leaves it registered (and the driver package in the store) with the device gone.
    public static string? PawnIoVersion
    {
        get
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                return key is null ? null : key.GetValue("DisplayVersion") as string ?? "";
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }
    }

    public static bool ServiceInstalled
    {
        get
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName);
                return key is not null && File.Exists(ServiceExe);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }
    }

    // Raised by one each time the service was set up or removed from here, so the monitor looks whether it is installed again
    // (SensorClient) instead of asking all the time.
    private static int _generation;
    public static int Generation => Volatile.Read(ref _generation);

    public static SensorSetupState State => PawnIoVersion is null ? SensorSetupState.NoPawnIo : ServiceInstalled ? SensorSetupState.Enabled : SensorSetupState.NotEnabled;

    // Installs or removes PawnIO with winget in a window of its own, so its agreements and progress are the user's to see; winget
    // asks for administrator rights itself. False without winget (the page then offers PawnIO's own download).
    public static bool RunWinget(bool install)
    {
        try
        {
            var info = new ProcessStartInfo("winget.exe") { UseShellExecute = true };
            foreach (var argument in new[] { install ? "install" : "uninstall", "--id", "namazso.PawnIO", "--exact" }) info.ArgumentList.Add(argument);
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Win32Exception) { return false; }
    }
    public const string PawnIoDownload = "https://github.com/namazso/PawnIO.Setup/releases/latest";

    // Downloads the service and the modules into a folder of the user's own, then starts BrimDeck elevated to put them in place and
    // register the service; the elevated copy checks both files again where only administrators can change them.
    public static async Task EnableAsync(CancellationToken cancel)
    {
        string staging = Path.Combine(Path.GetTempPath(), "BrimDeck-sensors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string exe = Path.Combine(staging, ExeName);
            if (ExpectedSha256 is null)
            {
                // A build without a release's hash (a developer's): the service built beside it.
                string beside = Path.Combine(AppContext.BaseDirectory, ExeName);
                if (!File.Exists(beside)) throw new InvalidOperationException(Loc.T("本地编译的版本需要把 BrimDeck.Sensors.exe 放在 BrimDeck.exe 旁边",
                    "A local build needs BrimDeck.Sensors.exe beside BrimDeck.exe"));
                File.Copy(beside, exe);
            }
            else await DownloadAsync(new Uri($"{Updates.GitHubUpdates.RepositoryUrl}/releases/download/v{ApplicationVersion.Current}/{ExeName}"), exe, ExpectedSha256, cancel);
            await DownloadAsync(new Uri(ModulesUrl), Path.Combine(staging, "modules.zip"), ModulesSha256, cancel);
            await RunElevatedAsync(["install", staging], cancel);
        }
        finally { try { Directory.Delete(staging, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    public static Task DisableAsync(CancellationToken cancel) => RunElevatedAsync(["remove"], cancel);

    private static async Task DownloadAsync(Uri uri, string path, string sha256, CancellationToken cancel)
    {
        try
        {
            using (var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(path);
                await response.Content.CopyToAsync(file, cancel);
            }
            if (Sha256(path) != sha256) throw new InvalidOperationException(Loc.T("下载的文件校验不符", "The downloaded file did not check out"));
        }
        catch (HttpRequestException ex) { throw new InvalidOperationException(Loc.T("下载失败：", "Download failed: ") + ex.Message, ex); }
    }

    private static async Task RunElevatedAsync(string[] arguments, CancellationToken cancel)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        info.ArgumentList.Add(ElevatedSwitch);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        Process? process;
        try { process = Process.Start(info); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new InvalidOperationException(Loc.T("未取得管理员权限", "Administrator rights were not given")); }
        using (process)
        {
            if (process is null) throw new InvalidOperationException(Loc.T("无法以管理员身份运行", "Could not run as administrator"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            // Looked at again even when the wait ends early: the elevated copy may have registered the service by then.
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { Interlocked.Increment(ref _generation); }
            if (process.ExitCode != 0) throw new InvalidOperationException(ElevatedFailure(process.ExitCode));
        }
    }
    private static string ElevatedFailure(int code) => code switch
    {
        3 => Loc.T("传感器服务校验不符", "The sensor service did not check out"),
        4 => Loc.T("PawnIO 模块校验不符", "The PawnIO modules did not check out"),
        5 => Loc.T("无法注册服务", "Could not register the service"),
        _ => Loc.T("无法完成设置", "Could not finish the setup")
    };

    private static string Sha256(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    // The elevated instance: "install <staging folder>" or "remove". Exit codes: 0 done, 2 bad arguments, 3 the service's hash,
    // 4 the modules' hash, 5 the service manager refused, 1 anything else.
    public static int RunElevated(string[] arguments)
    {
        try
        {
            switch (arguments)
            {
                case ["install", var staging] when Directory.Exists(staging): return Install(staging);
                case ["remove"]: Remove(); return 0;
                default: return 2;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine(ex);
            return ex is Win32Exception ? 5 : 1;
        }
    }

    private static int Install(string staging)
    {
        StopService();
        Directory.CreateDirectory(Folder);
        // Copied first and checked where it now lies, so nothing can change it between the check and its use.
        string exe = ServiceExe + ".new";
        File.Copy(Path.Combine(staging, ExeName), exe, true);
        if (ExpectedSha256 is { } expected && Sha256(exe) != expected) { File.Delete(exe); return 3; }
        string zip = Path.Combine(Folder, "modules.zip");
        File.Copy(Path.Combine(staging, "modules.zip"), zip, true);
        try
        {
            if (Sha256(zip) != ModulesSha256) { File.Delete(exe); return 4; }
            string modules = Path.Combine(Folder, "modules");
            Directory.CreateDirectory(modules);
            using var archive = ZipFile.OpenRead(zip);
            foreach (var name in Modules)
                (archive.GetEntry(name) ?? throw new InvalidDataException(name)).ExtractToFile(Path.Combine(modules, name), true);
        }
        finally { File.Delete(zip); }
        File.Move(exe, ServiceExe, true);
        Register();
        return 0;
    }

    private static void Remove()
    {
        StopService();
        IntPtr manager = OpenSCManagerW(null, null, ScManagerAll);
        if (manager == IntPtr.Zero) throw new Win32Exception();
        try
        {
            IntPtr service = OpenServiceW(manager, ServiceName, Delete);
            if (service != IntPtr.Zero) { DeleteService(service); CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
        for (int attempt = 0; attempt < 10 && Directory.Exists(Folder); attempt++)
        {
            try { Directory.Delete(Folder, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }

    private static void StopService()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            if (service.Status != ServiceControllerStatus.Stopped) { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10)); }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or System.ServiceProcess.TimeoutException) { }
    }

    // The service: its own process, started on demand, as LocalSystem (the driver opens for SYSTEM), with the permissions above.
    private static void Register()
    {
        IntPtr manager = OpenSCManagerW(null, null, ScManagerAll);
        if (manager == IntPtr.Zero) throw new Win32Exception();
        try
        {
            string command = "\"" + ServiceExe + "\"";
            IntPtr service = OpenServiceW(manager, ServiceName, ServiceAll);
            if (service == IntPtr.Zero)
                service = CreateServiceW(manager, ServiceName, "BrimDeck Sensors", ServiceAll, OwnProcess, DemandStart, ErrorNormal, command, null, IntPtr.Zero, null, null, null);
            else if (!ChangeServiceConfigW(service, OwnProcess, DemandStart, ErrorNormal, command, null, IntPtr.Zero, null, null, null, "BrimDeck Sensors"))
                throw new Win32Exception();
            if (service == IntPtr.Zero) throw new Win32Exception();
            try
            {
                if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(ServiceSecurity, 1, out var descriptor, IntPtr.Zero)) throw new Win32Exception();
                try { if (!SetServiceObjectSecurity(service, DaclSecurityInformation, descriptor)) throw new Win32Exception(); }
                finally { LocalFree(descriptor); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private const uint ScManagerAll = 0xF003F, ServiceAll = 0xF01FF, Delete = 0x10000, OwnProcess = 0x10, DemandStart = 3, ErrorNormal = 1, DaclSecurityInformation = 4;
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateServiceW(IntPtr manager, string name, string display, uint access, uint type, uint start, uint error, string command,
        string? group, IntPtr tag, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfigW(IntPtr service, uint type, uint start, uint error, string command, string? group, IntPtr tag,
        string? dependencies, string? account, string? password, string display);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DeleteService(IntPtr service);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetServiceObjectSecurity(IntPtr service, uint information, IntPtr descriptor);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, IntPtr size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
