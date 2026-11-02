using BrimDeck.Core;

// How the dot boots and powers off (MainWindow.Lifecycle.cs).
internal static class DotMotionTests
{
    public static void Run(Action<string, bool> check)
    {
        var boot = Samples(DotMotion.Boot, DotMotion.BootLength);
        var off = Samples(DotMotion.PowerOff, DotMotion.Touchdown);

        check("DotMotion: the boot starts out of sight and ends at rest", boot[0].Opacity == 0 && DotMotion.Boot(DotMotion.BootLength) == DotPose.Rest);
        check("DotMotion: the boot fades in without passing full opacity", boot.Zip(boot.Skip(1)).All(p => p.Second.Opacity >= p.First.Opacity - 1e-9) && boot.All(p => p.Opacity <= 1));
        check("DotMotion: the dot jumps exactly twice when booting and when powering off", Jumps(boot).Count == 2 && Jumps(off).Count == 2);
        check("DotMotion: both jumps have the same height, booting and powering off alike",
            Jumps(boot) is [var first, var second] && Math.Abs(first - second) < .01 && first > 10
            && Jumps(off) is [var offFirst, var offSecond] && Math.Abs(offFirst - first) < .01 && Math.Abs(offSecond - second) < .01);
        check("DotMotion: the jumps go up from the floor", boot.Concat(off).All(p => p.Lift >= -.01) && Jumps(boot).All(h => h > 0));
        check("DotMotion: the pose moves without jumps", Smooth(boot) && Smooth(off));
        check("DotMotion: a squash keeps the dot's volume", boot.Concat(off).Where(p => p.Opacity == 1).All(p => Math.Abs(p.ScaleX * p.ScaleX * p.ScaleY - 1) < 1e-9));

        check("DotMotion: the surface swells from a still circle, with nothing left to snap back",
            DotMotion.SwellStart <= DotMotion.BootLength && DotMotion.Boot(DotMotion.SwellStart) == DotPose.Rest);
        check("DotMotion: the jumps fit in the headroom left above the dot",
            boot.Concat(off).All(p => p.Lift + DotMotion.Size * (p.ScaleY - 1) <= DotMotion.Headroom + 1e-9));

        var merge = Enumerable.Range(0, 201).Select(i => DotMotion.MergeAt(i / 200.0)).ToList();
        const double r = DotMotion.Size / 2;
        var grid = (from x in Enumerable.Range(-40, 81) from y in Enumerable.Range(-40, 45) select (X: x * .5, Y: y * .5)).ToList();
        check("DotMotion: sinking starts as the plain dot resting on the water, where the jumps left it",
            Math.Abs(merge[0].Depth + r) < 1e-9 && merge[0].Radius == r && merge[0].LineThickness == 0 && merge[0].Blend == 0
            && grid.All(p => (DotMotion.MergeDistance(merge[0], p.X, p.Y) < 0) == (p.X * p.X + (p.Y + r) * (p.Y + r) < r * r && p.Y < 0)));
        check("DotMotion: the dot only goes down, and ends far enough under that it raises nothing",
            merge.Zip(merge.Skip(1)).All(p => p.Second.Depth >= p.First.Depth - 1e-9) && merge[^1].Depth - merge[^1].Radius > merge[^1].Blend);
        check("DotMotion: the surface opens wider than the dot, then closes into the middle",
            merge.Max(m => m.LineHalf) > r * 1.5 && merge.Max(m => m.LineHalf) < DotMotion.Size * 1.5 && merge[^1].LineHalf < 1e-9);
        check("DotMotion: the dot melts into the surface: the water rises to meet it before it reaches the middle",
            merge.Any(m => m.Depth < 0 && DotMotion.MergeDistance(m, m.Radius * .9, -m.LineThickness - .4) < 0));
        check("DotMotion: as the dot goes under, the surface swells over it, then sinks back flat and is gone",
            merge.Any(m => m.Depth > m.Radius * .6 && DotMotion.MergeDistance(m, 0, -m.LineThickness - .4) < 0)
            && grid.All(p => DotMotion.MergeDistance(merge[^1], p.X, p.Y) > 0));
        check("DotMotion: sinking never jumps in its parts", merge.Zip(merge.Skip(1)).All(p =>
            Math.Abs(p.Second.Depth - p.First.Depth) < .6 && Math.Abs(p.Second.LineHalf - p.First.LineHalf) < 1
            && Math.Abs(p.Second.LineThickness - p.First.LineThickness) < .05 && Math.Abs(p.Second.Blend - p.First.Blend) < .15));
    }

    private static List<DotPose> Samples(Func<double, DotPose> pose, double length)
        => Enumerable.Range(0, (int)(length * 4) + 1).Select(i => pose(i / 4.0)).ToList();

    private static List<double> Jumps(List<DotPose> poses)
    {
        var heights = new List<double>();
        double peak = 0;
        foreach (var pose in poses)
        {
            if (pose.Lift > 0) peak = Math.Max(peak, pose.Lift);
            else if (peak > 0) { heights.Add(Math.Round(peak, 6)); peak = 0; }
        }
        if (peak > 0) heights.Add(Math.Round(peak, 6));
        return heights;
    }

    private static bool Smooth(List<DotPose> poses) => poses.Zip(poses.Skip(1)).All(p =>
        Math.Abs(p.Second.Lift - p.First.Lift) < .5 && Math.Abs(p.Second.ScaleX - p.First.ScaleX) < .05 && Math.Abs(p.Second.ScaleY - p.First.ScaleY) < .05
        && Math.Abs(p.Second.Opacity - p.First.Opacity) < .05);
}
