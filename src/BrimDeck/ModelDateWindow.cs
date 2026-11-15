using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// An owned window lets the calendar extend past a short island without clipping,
// and accepts keyboard input while the overlay itself stays non-activating.
// It holds the picker (ModelDatePicker), which moves as the panel does, and opens and closes as the panel's other pop-ups
// (ModelDetails.ShowModelPopup).
internal sealed class ModelDateWindow : Window
{
    internal ModelDatePicker Picker { get; }
    internal event Action<UsageDateRange>? Applied;
    internal event Action? Cancelled;
    internal UsageDateRange? SelectedRange => Picker.SelectedRange;

    // look: the look of the panel it opens from; a look of its own also gives the window that panel's brushes.
    internal ModelDateWindow(UsageDateRange range, PanelLook? look = null)
    {
        look ??= PanelTheme.Main;
        PanelLook.Set(this, look); look.PublishTo(Resources, onlyOwn: true);
        Title = Loc.T("按时间", "By date"); Width = 246; WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true;
        FontFamily = UI.PanelFont;
        Picker = new ModelDatePicker(range, look.Current) { VerticalAlignment = VerticalAlignment.Top };
        Picker.Applied += selected => { Applied?.Invoke(selected); Close(); };
        Picker.Cancelled += () => { Cancelled?.Invoke(); Close(); };
        Content = Picker;
        // The window keeps the validation message's room below the picker, transparent while it is closed, so the message opens
        // and closes on the shared curve without the window resizing every frame.
        Height = Picker.FullHeight(Width);
    }

    internal void ChooseDate(DateTime date) => Picker.ChooseDate(date);
    internal void ApplySelection() => Picker.ApplySelection();
}
