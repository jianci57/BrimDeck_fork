using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// The smooth memory mode prepares the code that builds and draws the pages once the app has started and has nothing else to do
// (MemoryPlan.WarmAtStart), as PowerWarm does for the power rows: code compiled when a page is first built otherwise runs on the UI
// thread as the page opens, which shows as a stall the first time each page is opened. A build without precompiled code (a local
// build) compiles everything on first use; a published build has most of it precompiled and this pass finds little to do. One pass
// on a pool thread, once per run; the compiled code stays for the run.
internal static class PageWarm
{
    private static int _started;

    public static void Start(Dispatcher dispatcher)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => ThreadPool.QueueUserWorkItem(_ => Run()));
    }

    // The pages' own types (the usage, music, clipboard and devices pages are built by MainWindow), what they draw with, and the
    // layouts they ask for.
    private static readonly Type[] Types =
    [
        typeof(MainWindow), typeof(PageHandover), typeof(UI),
        typeof(MonitorView), typeof(FigureGauge), typeof(ColumnGraph), typeof(BlockGauge), typeof(MemoryBlock), typeof(NetworkBlock), typeof(StorageBlock),
        typeof(FramesBlock), typeof(ProcessListView), typeof(ProcessCard), typeof(MonitorDraw), typeof(PowerBlock),
        typeof(MusicVisuals), typeof(MusicEqualizer), typeof(MusicMarquee), typeof(MusicControlButton), typeof(MusicSeekSlider),
        typeof(ShelfRowList), typeof(DevicesView), typeof(QuotaBar), typeof(OffPeakBar),
        typeof(MusicLayout), typeof(MonitorLayout), typeof(PanelHeader), typeof(DeckPages),
    ];

    private static void Run()
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var type in Types) Prepare(type);

        // The type and its nested types: lambdas and local functions are compiled into nested types of their own.
        static void Prepare(Type type)
        {
            if (type.ContainsGenericParameters) return;
            foreach (var method in type.GetMethods(All)) PrepareMethod(method);
            foreach (var constructor in type.GetConstructors(All)) PrepareMethod(constructor);
            foreach (var nested in type.GetNestedTypes(All)) Prepare(nested);
        }
        static void PrepareMethod(MethodBase method)
        {
            if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() is null) return;
            // A method that cannot be prepared ahead (one the runtime would only compile in its own context) is compiled on first use
            // as before.
            try { RuntimeHelpers.PrepareMethod(method.MethodHandle); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine(ex); }
        }
    }
}
