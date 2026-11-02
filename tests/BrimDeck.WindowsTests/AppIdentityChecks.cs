using System.Diagnostics;
using System.Runtime.InteropServices;
using BrimDeck.Core;
using BrimDeck.Native;

// The id Windows resolves for a process that set its own, the way QueMusic does ("BroNekoX.QueMusic" for QueMusic.exe):
// the shell's resolver is not a documented interface, so this is where a Windows update that changes it would show.
internal static class AppIdentityChecks
{
    public static void Run(Action<string, bool> check)
    {
        const string id = "BrimDeck.Tests.IdentityProbe";
        check("app identity: this process sets its own application id", SetCurrentProcessExplicitAppUserModelID(id) == 0);
        var self = AppIdentity.Describe(Environment.ProcessId);
        using var process = Process.GetCurrentProcess();
        check("app identity: the process is described by its executable and the id it set",
            self.AppId == id && string.Equals(self.ProcessName, process.ProcessName, StringComparison.OrdinalIgnoreCase) && self.PackageFamily is null);
        check("app identity: the process plays for a media session of that id, and not for one of another",
            MediaAppTarget.Plays(new(null, MediaAppTarget.ProcessName(id), id), self)
            && !MediaAppTarget.Plays(new(null, MediaAppTarget.ProcessName("BrimDeck.Tests.Other"), "BrimDeck.Tests.Other"), self));
        check("app identity: an application Windows does not list has no registered name", AppIdentity.RegisteredName(id) is null);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
}
