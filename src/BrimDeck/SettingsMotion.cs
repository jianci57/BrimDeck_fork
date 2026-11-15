using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The settings window's slider knobs and switches move in code, on the app's one rule (Soft.Enabled): their storyboards in
// SettingsResources.xaml played even with animations off or in high contrast (motion-review F5). A knob grows a little under
// the pointer on the monitor's hover glide; a switch's knob slides across in the short time a press lets go.
internal static class SettingsMotion
{
    private const double KnobHover = 1.08, SwitchTravel = 16;

    // On a slider's thumb whose template has a "KnobScale".
    public static readonly DependencyProperty KnobProperty = DependencyProperty.RegisterAttached(
        "Knob", typeof(bool), typeof(SettingsMotion), new PropertyMetadata(false, KnobChanged));
    public static bool GetKnob(DependencyObject element) => (bool)element.GetValue(KnobProperty);
    public static void SetKnob(DependencyObject element, bool value) => element.SetValue(KnobProperty, value);

    // On a switch (a check box) whose template has a "KnobShift", the knob's TranslateTransform.
    public static readonly DependencyProperty SwitchProperty = DependencyProperty.RegisterAttached(
        "Switch", typeof(bool), typeof(SettingsMotion), new PropertyMetadata(false, SwitchChanged));
    public static bool GetSwitch(DependencyObject element) => (bool)element.GetValue(SwitchProperty);
    public static void SetSwitch(DependencyObject element, bool value) => element.SetValue(SwitchProperty, value);

    private static void KnobChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Thumb thumb) return;
        thumb.MouseEnter -= KnobHovered; thumb.MouseLeave -= KnobHovered;
        if (!(bool)e.NewValue) return;
        thumb.MouseEnter += KnobHovered; thumb.MouseLeave += KnobHovered;
    }

    private static void KnobHovered(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Thumb { Template: { } template } thumb || template.FindName("KnobScale", thumb) is not ScaleTransform scale) return;
        // Touch and a pen do not hover (motion-review F4).
        double to = PressFeedback.Hovers(thumb.IsMouseOver, e.StylusDevice is not null) ? KnobHover : 1;
        Soft.Glide(scale, ScaleTransform.ScaleXProperty, to, Soft.Enabled, Soft.Quick);
        Soft.Glide(scale, ScaleTransform.ScaleYProperty, to, Soft.Enabled, Soft.Quick);
    }

    private static void SwitchChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ToggleButton toggle) return;
        toggle.Checked -= Toggled; toggle.Unchecked -= Toggled; toggle.Indeterminate -= Toggled; toggle.Loaded -= Placed;
        if (!(bool)e.NewValue) return;
        toggle.Checked += Toggled; toggle.Unchecked += Toggled; toggle.Indeterminate += Toggled; toggle.Loaded += Placed;
    }

    // Shown the first time (or again), the knob is where the switch is, without moving; only later changes slide.
    private static void Placed(object sender, RoutedEventArgs e) => Slide((ToggleButton)sender, animate: false);
    private static void Toggled(object sender, RoutedEventArgs e) => Slide((ToggleButton)sender, animate: ((ToggleButton)sender).IsLoaded);

    private static void Slide(ToggleButton toggle, bool animate)
    {
        toggle.ApplyTemplate();
        if (toggle.Template?.FindName("KnobShift", toggle) is not TranslateTransform shift) return;
        Soft.Glide(shift, TranslateTransform.XProperty, toggle.IsChecked == true ? SwitchTravel : 0, animate && Soft.Enabled, Soft.Quick);
    }
}
