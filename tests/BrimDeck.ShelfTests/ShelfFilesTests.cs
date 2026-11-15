using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using BrimDeck;

// Standalone fixture-only executable: no application data, shell calls, or original-file deletion.
internal static class ShelfFilesTests
{
    private static int Main()
    {
        int failed = 0;
        void Check(string name, bool ok) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}"); if (!ok) failed++; }
        string root = Path.Combine(Path.GetTempPath(), "BrimDeck-Shelf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string present = Path.Combine(root, "present.txt");
            File.WriteAllText(present, "fixture");
            Check("file is present", ShelfFiles.ProbePath(present) == ShelfFiles.PathState.Present);
            Check("directory is present", ShelfFiles.ProbePath(root) == ShelfFiles.PathState.Present);
            Check("absence with readable parent is confirmed", ShelfFiles.ProbePath(Path.Combine(root, "missing.txt")) == ShelfFiles.PathState.ConfirmedMissing);
            Check("missing parent is unavailable", ShelfFiles.ProbePath(Path.Combine(root, "offline", "missing.txt")) == ShelfFiles.PathState.Unavailable);
            Check("invalid path is unavailable", ShelfFiles.ProbePath("bad\0path") == ShelfFiles.PathState.Unavailable);

            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var busy = typeof(ShelfFiles).GetField("_probingMoved", flags)!;
            busy.SetValue(null, 1);
            try
            {
                Check("busy probe admits no queued task", Enumerable.Range(0, 1000).All(_ => ShelfFiles.TryProbeMoved([present]) is null));
            }
            finally { busy.SetValue(null, 0); }
            var probe = ShelfFiles.TryProbeMoved([present, Path.Combine(root, "missing.txt")]);
            Check("one admitted batch completes", probe is not null && probe.GetAwaiter().GetResult().SequenceEqual([ShelfFiles.PathState.Present, ShelfFiles.PathState.ConfirmedMissing]));
            Check("slot releases on completion", (int)busy.GetValue(null)! == 0);

            var folders = (ConcurrentDictionary<string, ShelfFiles.FolderStats>)typeof(ShelfFiles).GetField("Folders", flags)!.GetValue(null)!;
            var icons = (System.Collections.IDictionary)typeof(ShelfFiles).GetField("Icons", flags)!.GetValue(null)!;
            var types = (ConcurrentDictionary<string, string>)typeof(ShelfFiles).GetField("Types", flags)!.GetValue(null)!;
            string runningPath = Path.Combine(root, "running"), donePath = Path.Combine(root, "done");
            var running = new ShelfFiles.FolderStats { Counting = true };
            var done = new ShelfFiles.FolderStats { Items = 1, Size = 7 };
            folders[runningPath] = running; folders[donePath] = done;
            icons[present] = null; types[present] = "fixture";
            Check("Rest reports released caches", ShelfFiles.Rest());
            Check("Rest preserves running ownership and drops completed cache", folders.Count == 1 && ReferenceEquals(folders[runningPath], running));
            Check("Rest still releases icon and type caches", icons.Count == 0 && types.IsEmpty);
            Parallel.For(0, 100, _ => { ShelfFiles.Rest(); ShelfFiles.Forget(runningPath); if (!ReferenceEquals(ShelfFiles.Folder(runningPath), running)) Interlocked.Increment(ref failed); });
            Check("repeated concurrent rest and folder requests retain running identity", ReferenceEquals(folders[runningPath], running) && folders.Count == 1);
            Check("only retained running work is not released memory", !ShelfFiles.Rest());
            running.Counting = false; ShelfFiles.Rest();
            Check("completed work becomes removable", folders.IsEmpty);
        }
        finally
        {
            // Only the unique temporary fixture tree created above is removed.
            Directory.Delete(root, true);
        }
        return failed == 0 ? 0 : 1;
    }
}
