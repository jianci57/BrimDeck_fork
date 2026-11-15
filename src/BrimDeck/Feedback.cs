using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BrimDeck.Core;

namespace BrimDeck;

// Hover and press feedback that fades in and out as the system monitor's does (Soft.Hover on Soft.Follow) instead of
// switching in one frame. A button style sets
// Feedback.Fade; its template has a "HoverLayer" and may have a "PressLayer", both transparent at rest, beneath the content,
// or, for a bare icon (PanelGlyphButton), a "Glyph" whose opacity carries the feedback instead.
// The press layer lies over the hover layer, which stays lit while pressed, so the two never dip in between. The state is
// read after the button's own handlers have run, so IsPressed is already current. A press with the keyboard (Space held on a
// focused button) shows the same press layer: the button sets IsPressed on the key going down and clears it on the key coming
// up or focus leaving, and the state is read again then. Whatever pressed it, one press layer shows.
// A press shows at once (Soft.PressFill) and lets go as hover does (motion-review F12); touch and a pen stand in for the
// pointer only while they press, so they leave no hover behind (F4).
internal static class Feedback
{
    public static readonly DependencyProperty FadeProperty = DependencyProperty.RegisterAttached(
        "Fade", typeof(bool), typeof(Feedback), new PropertyMetadata(false, FadeChanged));
    public static bool GetFade(DependencyObject element) => (bool)element.GetValue(FadeProperty);
    public static void SetFade(DependencyObject element, bool value) => element.SetValue(FadeProperty, value);

    private static readonly MouseButtonEventHandler ButtonHandler = (sender, e) => { Source(sender, e); Update(sender); };
    // Whether the pointer came onto the button by touch or a pen (motion-review F4).
    private static readonly DependencyProperty TouchedProperty = DependencyProperty.RegisterAttached("Touched", typeof(bool), typeof(Feedback));
    private static readonly MouseEventHandler PointerHandler = (sender, e) =>
    {
        if (e.RoutedEvent == UIElement.MouseEnterEvent && sender is DependencyObject button) button.SetValue(TouchedProperty, e.StylusDevice is not null);
        Update(sender);
    };
    // The mouse moving over a button that touch or a pen came onto means the pointer has taken over: it hovers again.
    private static readonly MouseEventHandler MoveHandler = (sender, e) =>
    {
        bool changed = Source(sender, e);
        if (changed || sender is UIElement { IsMouseCaptured: true }) Update(sender);
    };
    // Touch or a pen taking over (or giving back to) the mouse on a button the mouse is already over sends no MouseEnter, so every
    // press and move says which pointer it is. True when that changed what the button believes.
    private static bool Source(object sender, MouseEventArgs e)
    {
        if (sender is not DependencyObject button) return false;
        bool touch = e.StylusDevice is not null;
        if ((bool)button.GetValue(TouchedProperty) == touch) return false;
        button.SetValue(TouchedProperty, touch);
        return true;
    }
    private static readonly DependencyPropertyChangedEventHandler EnabledHandler = (sender, _) => Update(sender);
    private static readonly KeyEventHandler KeyHandler = (sender, _) => Update(sender);
    private static readonly KeyboardFocusChangedEventHandler FocusHandler = (sender, _) => Update(sender);

    private static void FadeChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ButtonBase button) return;
        button.MouseEnter -= PointerHandler; button.MouseLeave -= PointerHandler; button.MouseMove -= MoveHandler;
        button.LostMouseCapture -= PointerHandler; button.IsEnabledChanged -= EnabledHandler;
        button.RemoveHandler(UIElement.MouseLeftButtonDownEvent, ButtonHandler); button.RemoveHandler(UIElement.MouseLeftButtonUpEvent, ButtonHandler);
        button.RemoveHandler(UIElement.KeyDownEvent, KeyHandler); button.RemoveHandler(UIElement.KeyUpEvent, KeyHandler); button.LostKeyboardFocus -= FocusHandler;
        if (!(bool)e.NewValue) return;
        button.MouseEnter += PointerHandler; button.MouseLeave += PointerHandler; button.MouseMove += MoveHandler;
        button.LostMouseCapture += PointerHandler; button.IsEnabledChanged += EnabledHandler;
        // The button handles its own presses; handledEventsToo lets the feedback see them after it has.
        button.AddHandler(UIElement.MouseLeftButtonDownEvent, ButtonHandler, true); button.AddHandler(UIElement.MouseLeftButtonUpEvent, ButtonHandler, true);
        button.AddHandler(UIElement.KeyDownEvent, KeyHandler, true); button.AddHandler(UIElement.KeyUpEvent, KeyHandler, true); button.LostKeyboardFocus += FocusHandler;
    }

    private static void Update(object sender)
    {
        if (sender is not ButtonBase { Template: { } template } button) return;
        bool enabled = button.IsEnabled, animate = Soft.At(button) && button.IsVisible;
        bool over = enabled && PressFeedback.Hovers(button.IsMouseOver, (bool)button.GetValue(TouchedProperty)), pressed = enabled && button.IsPressed;
        Show(template.FindName("HoverLayer", button) as UIElement, over ? 1 : 0, animate, over ? Soft.Hover : Soft.HoverOut);
        Show(template.FindName("PressLayer", button) as UIElement, pressed ? 1 : 0, animate, pressed ? Soft.PressFill : Soft.HoverOut);
        Show(template.FindName("Glyph", button) as UIElement, pressed ? GlyphPressed : over ? 1 : GlyphRest, animate, pressed ? Soft.PressFill : over ? Soft.Hover : Soft.HoverOut);
    }
    private const double GlyphRest = .78, GlyphPressed = .6;

    // The feedback comes in Soft.Hover (a press in Soft.PressFill) and leaves a little quicker (Soft.HoverOut).
    private static void Show(UIElement? layer, double target, bool animate, int milliseconds)
    {
        if (layer is null || Math.Abs(layer.Opacity - target) < .01 && !layer.HasAnimatedProperties) return;
        Soft.Glide(layer, UIElement.OpacityProperty, target, animate, milliseconds);
    }
}
