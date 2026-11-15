namespace BrimDeck.Core;

// Notes of failures that readings swallow (a provider that fails shows as dashes), kept for the log without flooding it: one note for
// each kind of failure of a source, again only after QuietSeconds. The clock and the writer are given, so the rule can be tested.
public sealed class FailureNotes(Func<double> clock, Action<string> write, double quietSeconds = FailureNotes.QuietSeconds)
{
    public const double QuietSeconds = 600;
    private const int Kept = 64;
    private readonly object _gate = new();
    private readonly Dictionary<(string Source, string Kind), double> _last = [];

    // Whether the failure was written (the first of its kind for the source, or the first after the quiet time). The whole exception is
    // written, with its stack: the message alone does not tell where a swallowed failure came from.
    public bool Note(string source, Exception error)
        => Note(source, error.GetType().FullName + ":" + error.HResult, error.ToString());

    // kind tells failures apart (an exception's type, or a short word); text is what is written.
    public bool Note(string source, string kind, string text)
    {
        double now = clock();
        lock (_gate)
        {
            if (_last.TryGetValue((source, kind), out var at) && now - at < quietSeconds) return false;
            if (_last.Count >= Kept)
                foreach (var old in _last.Where(pair => now - pair.Value >= quietSeconds && pair.Key != (source, kind)).Select(pair => pair.Key).ToList()) _last.Remove(old);
            if (_last.Count >= Kept && !_last.ContainsKey((source, kind))) return false;
            _last[(source, kind)] = now;
        }
        write($"{source}: {text}");
        return true;
    }
}
