using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BrimDeck.Core;

namespace BrimDeck.Native;

public sealed class DesktopSources(MiMoAccount? mimo = null) : IDesktopSources
{
    public string? ReadMiMoSession() => mimo?.Read();
    public void SaveMiMoSession(string session) => mimo?.Save(session);
    public ClaudeDesktopState ReadClaudeDesktop(DataLocations locations) => ClaudeDesktopReader.Read(locations);
    public GrokBotSignIn ReadGrokBotDesktop(DataLocations locations) => GrokBotDesktopReader.Read(locations);

    // Starts `codeagentcli auth status` without a window, with the environment the codeagent launcher sets (its certificates,
    // no proxy) and the data folder BrimDeck reads, and waits until the sign-in file holds a later expiry or the CLI ends. The
    // CLI writes the renewed sign-in within a few seconds; it is ended once that is written, or after 30 seconds.
    public async Task<bool> RenewCodeAgentAsync(string home, CancellationToken cancellation)
    {
        var cli = CodeAgentUsage.FindCli(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetEnvironmentVariable("PATH"));
        if (cli is null) return false;
        long before = CodeAgentUsage.StoredExpiry(home);
        var install = Path.GetDirectoryName(Path.GetDirectoryName(cli))!;
        var start = new ProcessStartInfo(cli, "auth status --json")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        var cert = Path.Combine(install, "custom", "cert.pem");
        if (File.Exists(cert)) { start.Environment["NODE_EXTRA_CA_CERTS"] = cert; start.Environment["SSL_CERT_FILE"] = cert; }
        foreach (var name in new[] { "http_proxy", "https_proxy", "HTTP_PROXY", "HTTPS_PROXY" }) start.Environment.Remove(name);
        start.Environment["CODEAGENT3_CONFIG_DIR"] = home;
        using var process = Process.Start(start);
        if (process is null) return false;
        process.StandardInput.Close();
        _ = process.StandardOutput.ReadToEndAsync(cancellation); _ = process.StandardError.ReadToEndAsync(cancellation);
        bool Renewed() { try { return CodeAgentUsage.StoredExpiry(home) > before; } catch (IOException) { return false; } }
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!Renewed() && !process.HasExited && DateTime.UtcNow < deadline) await Task.Delay(300, cancellation);
            // The CLI may still be writing the file when it ends; give it a moment.
            if (!Renewed() && process.HasExited) await Task.Delay(1000, cancellation);
            // A renewed CLI may still be writing the rest of the sign-in; it is left a few seconds to end by itself.
            if (Renewed()) for (int wait = 0; wait < 10 && !process.HasExited; wait++) await Task.Delay(300, cancellation);
            return Renewed();
        }
        finally { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
    }

    public string? UnprotectUserData(string base64)
    {
        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(Convert.FromBase64String(base64), null, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return null; }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    // Newer Cursor clients store the access token as "cursor-safe-storage:v1:" and the base64 of a value encrypted with
    // Cursor's own os_crypt key (Local State in %APPDATA%\Cursor); older clients store the token itself.
    private const string CursorSafeStorage = "cursor-safe-storage:v1:";

    public string? ReadCursorToken(string database)
    {
        if (!File.Exists(database)) return null;
        string? value;
        IntPtr db = IntPtr.Zero, statement = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(database, out db, 1, IntPtr.Zero) != 0) return null; // SQLITE_OPEN_READONLY
            sqlite3_busy_timeout(db, 1000);
            if (sqlite3_prepare_v2(db, "SELECT value FROM ItemTable WHERE key='cursorAuth/accessToken' LIMIT 1", -1, out statement, IntPtr.Zero) != 0) return null;
            value = sqlite3_step(statement) == 100 ? Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 0)) : null;
        }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (db != IntPtr.Zero) sqlite3_close(db);
        }
        if (value is null || !value.StartsWith(CursorSafeStorage, StringComparison.Ordinal)) return value;
        // database is %APPDATA%\Cursor\User\globalStorage\state.vscdb.
        var folder = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(database)))!;
        byte[]? key = null, plain = null;
        try
        {
            key = ChromiumSecrets.Key(Path.Combine(folder, "Local State"));
            plain = ChromiumSecrets.Decrypt(key, value[CursorSafeStorage.Length..]);
            return System.Text.Encoding.UTF8.GetString(plain).Trim();
        }
        catch (CryptographicException ex) { throw new IOException("The Cursor sign-in cannot be decrypted.", ex); }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    public IReadOnlyList<string?[]>? QueryDatabase(string database, string sql)
    {
        if (!File.Exists(database)) return null;
        IntPtr db = IntPtr.Zero, statement = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(database, out db, 1, IntPtr.Zero) != 0) return null; // SQLITE_OPEN_READONLY
            sqlite3_busy_timeout(db, 1000);
            if (sqlite3_prepare_v2(db, sql, -1, out statement, IntPtr.Zero) != 0) return null;
            var rows = new List<string?[]>();
            int columns = sqlite3_column_count(statement), step;
            while ((step = sqlite3_step(statement)) == 100) // SQLITE_ROW
            {
                var row = new string?[columns];
                for (int i = 0; i < columns; i++) row[i] = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, i));
                rows.Add(row);
            }
            return step == 101 ? rows : null; // SQLITE_DONE
        }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (db != IntPtr.Zero) sqlite3_close(db);
        }
    }

    // Discovery reads the language servers' service files, otherwise asks WMI for their command lines. Its result is kept for
    // as long as that process lives and still listens on the same ports, so each refresh then costs one read of the system's
    // listener table.
    private (int Id, DateTime Started, IReadOnlyList<LocalEndpoint> Endpoints)[] _antigravity = [];
    public bool AntigravityUnreadable { get; private set; }

    public async Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation)
    {
        AntigravityUnreadable = false;
        var listeners = TcpListeners.Read();
        var known = _antigravity.Where(p => Alive(p.Id, p.Started) &&
            p.Endpoints.All(e => listeners.Any(l => l.ProcessId == p.Id && l.Port == e.Port))).ToArray();
        if (known.Length > 0 && known.Length == _antigravity.Length) return known.SelectMany(p => p.Endpoints).ToList();
        // No language server running (Antigravity closed): nothing to look for.
        if (!ProcessSnapshot.Any(name => name.Contains("language_server", StringComparison.OrdinalIgnoreCase))) { _antigravity = []; return []; }
        var found = FromServiceFiles(listeners);
        if (found.Count == 0)
        {
            // A WMI query has no timeout of its own; a stalled service must not hold the refresh.
            var processes = await Task.Run(FindAntigravityProcesses, cancellation).WaitAsync(TimeSpan.FromSeconds(15), cancellation);
            foreach (var (pid, command) in processes)
            {
                var match = Regex.Match(command, "--csrf_token(?:=|\\s+)\"?([^\\s\"]+)");
                // WMI returns no command line for a process that runs as administrator when BrimDeck does not.
                if (!match.Success) { AntigravityUnreadable |= command.Length == 0; continue; }
                if (StartTime(pid) is not { } started) continue;
                var endpoints = listeners.Where(l => l.ProcessId == pid).Select(l => l.Port).Distinct()
                    .SelectMany(port => new LocalEndpoint[] { new(port, "https", match.Groups[1].Value), new(port, "http", match.Groups[1].Value) }).ToList();
                if (endpoints.Count > 0) found.Add((pid, started, endpoints));
            }
        }
        _antigravity = [.. found];
        return found.SelectMany(p => p.Endpoints).ToList();
    }

    // A service file is used while its process is a language server listening on the ports it names; the files of servers
    // that have ended stay behind.
    private static List<(int Id, DateTime Started, IReadOnlyList<LocalEndpoint> Endpoints)> FromServiceFiles(List<TcpListeners.Listener> listeners)
    {
        var found = new List<(int, DateTime, IReadOnlyList<LocalEndpoint>)>();
        var folder = AntigravityUsage.DaemonFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        foreach (var server in AntigravityUsage.ReadLanguageServers(folder).GroupBy(s => s.ProcessId))
        {
            int pid = server.Key;
            var endpoints = server.SelectMany(s => new LocalEndpoint[] { new(s.HttpsPort, "https", s.Token), new(s.HttpPort, "http", s.Token) })
                .Where(e => listeners.Any(l => l.ProcessId == pid && l.Port == e.Port)).Distinct().ToList();
            if (endpoints.Count == 0 || !Path.GetFileName(AppIdentity.ImagePath(pid)).Contains("language_server", StringComparison.OrdinalIgnoreCase)
                || StartTime(pid) is not { } started) continue;
            found.Add((pid, started, endpoints));
        }
        return found;
    }

    private static DateTime? StartTime(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.StartTime; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
    private static bool Alive(int pid, DateTime started) => StartTime(pid) == started;

    private static List<(int Id, string Command)> FindAntigravityProcesses()
    {
        // WMI reads only the matching local process metadata; credentials never leave this method's result in memory.
        var processes = new List<(int Id, string Command)>();
        var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
        if (locatorType is null) return processes;
        dynamic locator = Activator.CreateInstance(locatorType)!;
        dynamic? service = null, rows = null;
        try
        {
            service = locator.ConnectServer(".", "root\\cimv2");
            rows = service.ExecQuery("SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process WHERE Name LIKE '%language_server%'");
            foreach (dynamic row in rows)
            {
                try
                {
                    int pid = Convert.ToInt32(WmiProperty(row, "ProcessId"));
                    // The process handle still gives the path of a process that runs as administrator, which WMI leaves empty.
                    string path = WmiProperty(row, "ExecutablePath") as string is { Length: > 0 } listed ? listed : AppIdentity.ImagePath(pid);
                    string command = WmiProperty(row, "CommandLine") as string ?? "";
                    if (path.Contains("antigravity", StringComparison.OrdinalIgnoreCase)) processes.Add((pid, command));
                }
                finally { Marshal.FinalReleaseComObject(row); }
            }
        }
        finally
        {
            if (rows is not null) Marshal.FinalReleaseComObject(rows);
            if (service is not null) Marshal.FinalReleaseComObject(service);
            Marshal.FinalReleaseComObject(locator);
        }
        return processes;
    }

    private static object? WmiProperty(object row, string name)
    {
        // Use named SWbemPropertySet access. Dynamic property access on SWbemObject
        // failed after the first query in repeated-refresh testing (0x80004005).
        dynamic? properties = null, property = null;
        try
        {
            properties = ((dynamic)row).Properties_;
            property = properties.Item(name);
            return property.Value;
        }
        finally
        {
            if (property is not null) Marshal.FinalReleaseComObject(property);
            if (properties is not null) Marshal.FinalReleaseComObject(properties);
        }
    }

    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr statement);
}
