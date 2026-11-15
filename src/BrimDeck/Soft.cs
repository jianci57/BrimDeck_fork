using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BrimDeck.Core;

namespace BrimDeck;

// One motion language for the whole app: the same few curves and durations for the panel, its pages, cards, hover and press
// feedback, and dragging. Moves start promptly and land slowly (Out); what changes in place eases both ways (InOut); what
// opens or appears leaves at full speed and lands with a slight give (Bounce), and what closes or goes away leaves at once and
// lands softly without one (Drawer; the power-off still settles from a slow start, Settle); what follows the pointer trails it on a spring (BrimDeck.Core.Spring).
// The curves are frozen and shared.
internal static class Soft
{
    // A soft landing: quick at first, a long gentle tail.
    public static readonly IEasingFunction Out = Frozen(new BezierEase(.22, .8, .28, 1));
    // For what turns or swaps in place.
    public static readonly IEasingFunction InOut = Frozen(new BezierEase(.45, 0, .25, 1));
    // The surface drawing back into the dot as BrimDeck powers off: a slow start, then it settles.
    public static readonly IEasingFunction Settle = Frozen(new BezierEase(.45, 0, .12, 1));
    // The collapsing panel (motion-review C2: chosen over Settle in an A/B comparison): a drawer's curve, which leaves at once
    // and lands softly; the surface still starts a little after the content (PanelMotion.CloseSurfaceStart).
    public static readonly IEasingFunction Drawer = Frozen(new BezierEase(.32, .72, 0, 1));
    // The system monitor's move (Curves.Follow): content that moves or swaps, selections that glide, hover feedback.
    public static readonly IEasingFunction Follow = Frozen(new FollowEase());
    // The opening surface: a fast attack that passes its size by about a third of a percent and settles back (Curves.Bounce).
    public static readonly IEasingFunction Bounce = Frozen(new BounceEase());

    // Hover and press feedback, and what leaves; small things that appear or swap. Values that follow readings take
    // BrimDeck.Core.Eased.Seconds, and the panel itself the duration chosen in the settings. Hover is the monitor's (Eased.Hover, and
    // HoverOut its quicker way out), a glide between choices the monitor's sort pill (Choose), content that moves in or out the
    // monitor's readings (Move).
    public const int Quick = 160, Short = 240, Hover = (int)(Eased.Hover * 1000), HoverOut = (int)(Eased.HoverOut * 1000 + .5), Choose = (int)(Eased.Seconds * 750), Move = (int)(Eased.Seconds * 1000);
    // A press (motion-review F11, F12): what shrinks under the button goes down in Press to PressScale, a pressed fill or glyph
    // in PressFill, so the press is felt at once; both let go in Quick.
    public const int Press = PressFeedback.Down, PressFill = PressFeedback.Fill;
    public const double PressScale = PressFeedback.Scale;
    // How small content is as it steps out of or into its place (the island's compact content, motion-review F15): never so
    // small that it reads as coming from nothing.
    public const double Shrunk = .94;

    // Whether anything moves: the setting in BrimDeck and Windows' own "animation effects", and never in high contrast. The one
    // rule for every animation in the app (motion-review F5, F6): nothing outside it decides for itself.
    // "Enable animations" belongs to a profile, and a panel may show another profile than the one in use (App.SettingsFor): a panel
    // asks with its own settings (For, MainWindow.Animates), what lies in a window asks by that window (At), and Enabled, the
    // profile in use, is for the settings window and what belongs to no panel.
    public static bool Enabled => For((Application.Current as App)?.Settings);
    public static bool For(DeckSettings? settings) =>
        MotionConditions.Allowed(settings?.Animations != false, SystemParameters.ClientAreaAnimation, SystemParameters.HighContrast);
    // For something on screen: its panel's settings when it belongs to a panel, otherwise the profile in use.
    public static bool At(DependencyObject? element) => PanelOf(element) is { } deck ? For(deck.PanelSettings) : Enabled;
    // The panel something belongs to. A menu or pop-up belongs to what it opens from, and a window a panel opened (the model
    // details' date and filter windows) to its owner, so the walk goes through those as well as through parents.
    private static MainWindow? PanelOf(DependencyObject? element)
    {
        for (int step = 0; element is not null && step < 512; step++)
        {
            switch (element)
            {
                case MainWindow deck: return deck;
                case Window window: element = window.Owner; continue;
                case System.Windows.Controls.ContextMenu { PlacementTarget: { } target }: element = target; continue;
                case System.Windows.Controls.ToolTip { PlacementTarget: { } target }: element = target; continue;
                case System.Windows.Controls.Primitives.Popup { PlacementTarget: { } target }: element = target; continue;
            }
            // The logical parent first: a pop-up's content has the pop-up as its parent there, and nothing above it visually.
            element = LogicalTreeHelper.GetParent(element) ?? (element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : null);
        }
        return null;
    }
    // Whether text too long for its place scrolls; high contrast does not stop it (MotionConditions.Scrolls).
    public static bool Scrolls(DeckSettings? settings) => MotionConditions.Scrolls(settings?.Animations != false, SystemParameters.ClientAreaAnimation);

    public static DoubleAnimation Fade(double from, double to, int milliseconds, IEasingFunction? easing = null) =>
        new(from, to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = easing ?? Out };

    // What opens below its anchor (a menu, a drop-down list, a pop-up) fades in while it drops the last few DIPs into place.
    // The element's own transform is replaced only when it has none.
    public static void Enter(UIElement? element, double drop = 6)
    {
        // By the panel it opens from: a panel showing another profile than the one in use follows its own (ModelDetails holds a
        // pop-up's content out of sight on that panel's word and leaves the entrance to this).
        if (element is null || !At(element)) return;
        var start = Fade(0, 1, Short); start.FillBehavior = FillBehavior.Stop;
        element.BeginAnimation(UIElement.OpacityProperty, start);
        if (element.RenderTransform is not (null or MatrixTransform { Matrix.IsIdentity: true } or TranslateTransform { IsFrozen: false })) return;
        if (element.RenderTransform is not TranslateTransform shift) element.RenderTransform = shift = new TranslateTransform();
        var fall = Fade(-drop, 0, Short + 60); fall.FillBehavior = FillBehavior.Stop;
        shift.BeginAnimation(TranslateTransform.YProperty, fall);
    }

    // The way back: it fades as it rises the same few DIPs, a little quicker, as the monitor page's card leaves. done runs once
    // it has gone. It leaves at full speed and slows, as what arrives does (motion-review F13).
    public static void Leave(UIElement element, Action done, double rise = 4)
    {
        var fade = Fade(element.Opacity, 0, Quick);
        fade.Completed += (_, _) => done();
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        if (element.RenderTransform is not (null or MatrixTransform { Matrix.IsIdentity: true } or TranslateTransform { IsFrozen: false })) return;
        if (element.RenderTransform is not TranslateTransform shift) element.RenderTransform = shift = new TranslateTransform();
        shift.BeginAnimation(TranslateTransform.YProperty, Fade(shift.Y, -rise, Quick));
    }

    // A scale that answers a press: down to PressScale in Press, back in Quick.
    public static void Pressed(ScaleTransform scale, bool down, bool animate)
    {
        Glide(scale, ScaleTransform.ScaleXProperty, down ? PressScale : 1, animate, PressFeedback.ScaleLength(down));
        Glide(scale, ScaleTransform.ScaleYProperty, down ? PressScale : 1, animate, PressFeedback.ScaleLength(down));
    }

    // Sets a property to its new value and, when animate, glides there from where it was (Soft.Follow, Hover by default): the
    // animation only covers the way and ends without holding the property, so the value set is what stays. Changes of at most
    // tolerance are set at once.
    public static void Glide<T>(T target, DependencyProperty property, double to, bool animate, int milliseconds = Hover, double tolerance = 0)
        where T : DependencyObject, IAnimatable
    {
        double from = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, to);
        if (!animate || !double.IsFinite(from) || Math.Abs(from - to) <= tolerance) return;
        target.BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = Follow, FillBehavior = FillBehavior.Stop });
    }

    // The same for a brush's color.
    public static void Glide(SolidColorBrush brush, Color to, bool animate, int milliseconds = Hover)
    {
        var from = brush.Color;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        brush.Color = to;
        if (animate && from != to) brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = Follow, FillBehavior = FillBehavior.Stop });
    }

    private static IEasingFunction Frozen(Freezable ease) { ease.Freeze(); return (IEasingFunction)ease; }
}

// Cubic Bézier easing with CSS semantics, so the surface follows the same curve as the approved mock-up. EasingFunctionBase
// mirrors EaseInCore unless the mode is EaseIn (its default is EaseOut), which turned every curve here back to front: the
// soft landing started slowly and stopped hard. The curve is the whole easing, so the mode is EaseIn.
public sealed class BezierEase : EasingFunctionBase
{
    private readonly double _x1, _y1, _x2, _y2;
    public BezierEase(double x1, double y1, double x2, double y2) { _x1 = x1; _y1 = y1; _x2 = x2; _y2 = y2; EasingMode = EasingMode.EaseIn; }
    protected override Freezable CreateInstanceCore() => new BezierEase(_x1, _y1, _x2, _y2);
    protected override double EaseInCore(double normalizedTime) => Curves.Bezier(_x1, _y1, _x2, _y2, normalizedTime);
}

// Curves.Follow as a WPF easing; see BezierEase for the mode.
public sealed class FollowEase : EasingFunctionBase
{
    public FollowEase() { EasingMode = EasingMode.EaseIn; }
    protected override Freezable CreateInstanceCore() => new FollowEase();
    protected override double EaseInCore(double normalizedTime) => Curves.Follow(normalizedTime);
}

// Curves.Bounce as a WPF easing; see BezierEase for the mode.
public sealed class BounceEase : EasingFunctionBase
{
    public BounceEase() { EasingMode = EasingMode.EaseIn; }
    protected override Freezable CreateInstanceCore() => new BounceEase();
    protected override double EaseInCore(double normalizedTime) => Curves.Bounce(normalizedTime);
}
