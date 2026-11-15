using BrimDeck.Core;

// FrameSample.Low: the worst share of the recent frame gaps converts back to an average frame rate; gaps that are not real
// frames go, and without usable gaps there is no number.
internal static class FrameSampleTests
{
    internal static void Run(Action<string, bool> check)
    {
        check("1% low of a few gaps is the worst single frame",
            FrameSample.Low([10.0, 20.0, 30.0, 40.0], 0.01) == 25.0);
        check("0.1% low is the worst single frame too",
            FrameSample.Low([10.0, 20.0, 30.0, 40.0], 0.001) == 25.0);
        check("1% low of even gaps sits at the even rate",
            FrameSample.Low([.. Enumerable.Range(0, 1000).Select(_ => 20.0)], 0.01) == 50.0);
        check("Non-finite and non-positive gaps are dropped",
            FrameSample.Low([double.NaN, 0, -5, 10.0, 20.0], 0.01) == 50.0);
        check("No usable gaps yields nothing",
            FrameSample.Low([], 0.01) is null && FrameSample.Low([double.NaN], 0.01) is null);
        check("A single gap defines both percentiles",
            FrameSample.Low([16.6], 0.01) == 60.2);
    }
}
