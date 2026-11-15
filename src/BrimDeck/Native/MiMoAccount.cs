using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck.Native;

// The Xiaomi account BrimDeck signs in to for the Xiaomi MiMo Desktop membership quota (MiMoUsage). The membership has no API key:
// only the MiMo server's own cookies, set once a browser has signed in to the Xiaomi account, read it. So the sign-in runs on
// Xiaomi's own page, with every way it offers (password, text message, WeChat, Alipay and the others), in the default browser
// started with a profile of its own in a new folder and its DevTools port open on this PC only: BrimDeck reads the cookies through
// that port (Storage.getCookies), then ends that browser and deletes the folder. The browser the user normally runs, and its
// profile, are never touched. Only Chromium browsers open that port; another default browser is replaced by Edge, then Chrome.
// The session (MiMoUsage.MiMoSession) is kept in mimo-session.dat, encrypted with DPAPI for the current user.
public sealed class MiMoAccount(string folder)
{
    private readonly object _gate = new();
    private string FilePath => Path.Combine(folder, "mimo-session.dat");
    public event Action? Changed;

    public bool SignedIn => File.Exists(FilePath);

    public string? Read()
    {
        lock (_gate)
        {
            byte[]? plain = null;
            try
            {
                if (!File.Exists(FilePath)) return null;
                plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { return null; }
            finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
        }
    }

    public void Save(string session)
    {
        lock (_gate)
        {
            var plain = Encoding.UTF8.GetBytes(session);
            try
            {
                Directory.CreateDirectory(folder);
                var temp = FilePath + ".tmp";
                File.WriteAllBytes(temp, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
                File.Move(temp, FilePath, true);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        Changed?.Invoke();
    }

    public void SignOut()
    {
        lock (_gate) { try { File.Delete(FilePath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        Changed?.Invoke();
    }

    // A failure the settings page shows as it is.
    public sealed class SignInException(string message) : Exception(message);

    private static readonly string[] Chromium = ["chrome.exe", "msedge.exe", "brave.exe", "vivaldi.exe", "chromium.exe"];

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "AssocQueryStringW")]
    private static extern int AssocQueryString(int flags, int kind, string association, string? extra, StringBuilder? output, ref uint length);

    // The default browser for https when it is a Chromium browser; otherwise Edge, then Chrome, wherever installed.
    public static string? FindBrowser()
    {
        const int IsProtocol = 0x1000, Executable = 2;
        uint length = 1024; var output = new StringBuilder((int)length);
        if (AssocQueryString(IsProtocol, Executable, "https", null, output, ref length) == 0 && output.ToString() is { Length: > 0 } path
            && File.Exists(path) && Chromium.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)) return path;
        return new[] { "ProgramFiles(x86)", "ProgramFiles", "LOCALAPPDATA" }.Select(Environment.GetEnvironmentVariable).OfType<string>()
            .SelectMany(root => new[] { Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"), Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe") })
            .FirstOrDefault(File.Exists);
    }

    // Opens the sign-in page and waits up to ten minutes for a sign-in the MiMo server accepts; the session is saved and returned.
    // Closing the browser window ends the wait.
    public async Task SignInAsync(CancellationToken cancellation)
    {
        var browser = FindBrowser() ?? throw new SignInException(Loc.T("未找到 Chrome 或 Edge，无法打开登录页", "Chrome or Edge was not found, so the sign-in page cannot be opened."));
        var profile = Path.Combine(folder, "mimo-sign-in", Convert.ToHexString(RandomNumberGenerator.GetBytes(8)));
        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(browser) { UseShellExecute = false };
        foreach (var argument in new[] { "--user-data-dir=" + profile, "--remote-debugging-port=0", "--no-first-run", "--no-default-browser-check",
            "--disable-background-mode", "--new-window", MiMoUsage.SignInAddress }) start.ArgumentList.Add(argument);
        Process? process = null;
        try
        {
            process = Process.Start(start) ?? throw new SignInException(Loc.T("无法打开登录页", "The sign-in page cannot be opened."));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromMinutes(10));
            var session = await WaitAsync(process, profile, deadline.Token, cancellation);
            Save(session.Serialize());
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new SignInException(Loc.T("登录等待超时，请重新登录", "Signing in took too long. Sign in again.")); }
        finally
        {
            try { if (process is { HasExited: false }) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process?.Dispose();
            _ = Task.Run(() => Delete(profile));
        }
    }

    // The browser leaves its folder locked for a moment after it ends.
    private static async Task Delete(string profile)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try { if (Directory.Exists(profile)) Directory.Delete(profile, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(500); }
        }
    }

    private static async Task<MiMoUsage.MiMoSession> WaitAsync(Process process, string profile, CancellationToken deadline, CancellationToken cancellation)
    {
        // The browser writes the port it chose, and the path of its DevTools socket, to DevToolsActivePort in the profile folder.
        Uri? socket = null;
        var waitPort = Stopwatch.StartNew();
        while (socket is null)
        {
            if (process.HasExited) throw new SignInException(Loc.T("登录窗口已关闭", "The sign-in window was closed."));
            if (waitPort.Elapsed > TimeSpan.FromSeconds(30)) throw new SignInException(Loc.T("无法连接登录窗口，请关闭浏览器窗口后重试", "The sign-in window cannot be reached. Close the browser window and try again."));
            try
            {
                var lines = File.ReadAllLines(Path.Combine(profile, "DevToolsActivePort"));
                if (lines.Length >= 2 && int.TryParse(lines[0], out var port) && port > 0 && lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal))
                    socket = new Uri($"ws://127.0.0.1:{port}{lines[1]}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (socket is null) await Task.Delay(300, deadline);
        }
        using var client = new ClientWebSocket();
        await client.ConnectAsync(socket, deadline);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        string tried = "";
        for (int id = 1; ; id++)
        {
            await Task.Delay(1000, deadline);
            if (process.HasExited) throw new SignInException(Loc.T("登录窗口已关闭，未保存登录", "The sign-in window was closed before signing in."));
            var cookies = (await CallAsync(client, id, "Storage.getCookies", deadline)).Get("cookies").Items()
                .Select(c => (c.Get("name").Text(), c.Get("value").Text(), c.Get("domain").Text())).ToList();
            var session = MiMoUsage.Session(cookies);
            // The MiMo server's cookies come in several answers; each new set is tried once against the API.
            if (session.Cookie.Length == 0 || session.Cookie == tried) continue;
            tried = session.Cookie;
            if (await AcceptedAsync(http, session.Cookie, deadline) is not { } accepted) continue;
            // Renewing sends the user agent of the browser the account signed in with (MiMoUsage.RenewAsync).
            var agent = (await CallAsync(client, ++id, "Browser.getVersion", deadline)).Get("userAgent").Text();
            return session with { Cookie = accepted, Agent = agent };
        }
    }

    // The cookies as the MiMo server left them once it accepted them (it may replace some in its answer); null when it refused them.
    private static async Task<string?> AcceptedAsync(HttpClient http, string cookie, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, MiMoUsage.SubscriptionAddress);
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            using var response = await http.SendAsync(request, cancellation);
            return response.IsSuccessStatusCode ? MiMoUsage.Merge(cookie, MiMoUsage.SetCookies(response)) : null;
        }
        catch (HttpRequestException) { return null; }
    }

    // Sends one DevTools command and returns its result.
    private static async Task<JsonElement> CallAsync(ClientWebSocket client, int id, string method, CancellationToken cancellation)
    {
        var command = JsonSerializer.SerializeToUtf8Bytes(new { id, method });
        await client.SendAsync(command, WebSocketMessageType.Text, true, cancellation);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await client.ReceiveAsync(buffer, cancellation);
                if (result.MessageType == WebSocketMessageType.Close) throw new SignInException(Loc.T("登录窗口已关闭", "The sign-in window was closed."));
                message.Write(buffer, 0, result.Count);
                if (message.Length > 8 * 1024 * 1024) throw new SignInException(Loc.T("无法读取登录结果", "The sign-in cannot be read."));
            } while (!result.EndOfMessage);
            using var doc = JsonDocument.Parse(message.ToArray());
            if (doc.RootElement.Get("id").Number() != id) continue;
            return doc.RootElement.Get("result").Clone();
        }
    }
}
