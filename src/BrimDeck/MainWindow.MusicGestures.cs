using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Native;
using Microsoft.Win32;
using MediaState = BrimDeck.Core.MediaState;

namespace BrimDeck;

// Gestures over the expanded music page (MusicGestures, set on the settings' quick actions page). Dragging with the left button
// draws the pointer's trail on GestureLayer, with the action the drag would run beneath it, and runs that action on release. A
// press on a control (the transport, the seek bar, the source button) is the control's, not a drag; a press on the cover starts
// one, and the cover then does not bring the player forward. A two-finger touchpad swipe to either side, or a tilt of the mouse
// wheel, arrives as the horizontal wheel (WM_MOUSEHWHEEL) and runs its action once per swipe (HorizontalSwipe).
public partial class MainWindow
{
    private readonly HorizontalSwipe _swipe = new();
    private Point? _dragPress;
    private bool _dragging;
    private Polyline? _dragTrail;
    private Border? _dragLabel;
    // A drag recognised as a volume drag adjusts the volume as it goes on, by how far the pointer moves along its axis from where it
    // was recognised; it then keeps to the volume until released, whatever its trail does.
    private (bool Horizontal, int Sign, double From, float Start, SystemVolume Device)? _volumeDrag;
    // Lets go of a press the cover is showing, when a drag that began on it takes over.
    private Action? _releaseCoverPress;

    private void AttachMusicGestures()
    {
        // Without a background the page's empty parts would not take the press.
        MusicContent.Background = Brushes.Transparent;
        MusicContent.PreviewMouseLeftButtonDown += (_, e) => BeginMusicDrag(e);
        MusicContent.PreviewMouseMove += (_, e) => MoveMusicDrag(e);
        MusicContent.PreviewMouseLeftButtonUp += (_, e) => EndMusicDrag(e);
        MusicContent.LostMouseCapture += (_, _) => { if (_dragging) EndTrail(); };
    }

    private bool GesturesAllowed => _expanded && !_preview && IsMusicPage && _detailPopup is null && MediaSourceButton.ContextMenu?.IsOpen != true;

    private void BeginMusicDrag(MouseButtonEventArgs e)
    {
        _dragPress = null;
        if (!GesturesAllowed || !MusicGestures.AnyDrag(Settings) || OnControl(e.OriginalSource as DependencyObject)) return;
        _dragPress = e.GetPosition(GestureLayer);
    }
    private bool OnControl(DependencyObject? node)
    {
        for (; node is not null && !ReferenceEquals(node, MusicContent); node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is ButtonBase or RangeBase or Thumb or TextBoxBase or Hyperlink) return true;
        return false;
    }

    private void MoveMusicDrag(MouseEventArgs e)
    {
        if (_dragPress is not { } start) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _dragPress = null; if (_dragging) { MusicContent.ReleaseMouseCapture(); EndTrail(); } return; }
        var point = e.GetPosition(GestureLayer);
        if (!_dragging)
        {
            if ((point - start).Length < MusicGestures.DragStart) return;
            _dragging = true;
            _releaseCoverPress?.Invoke();
            MusicContent.CaptureMouse();
            StartTrail(start);
        }
        if (_dragTrail is { } trail && (trail.Points.Count == 0 || (point - trail.Points[^1]).Length >= 2)) trail.Points.Add(point);
        if (_volumeDrag is null)
        {
            var direction = DragDirection(point);
            int sign = MusicGestures.VolumeSign(direction, MusicGestures.Drag(Settings, direction));
            if (sign != 0 && SystemVolume.Open() is { } device)
            {
                try
                {
                    bool horizontal = direction is GestureDirection.Left or GestureDirection.Right;
                    _volumeDrag = (horizontal, sign, horizontal ? point.X : point.Y, device.Level, device);
                }
                catch { device.Dispose(); }
            }
        }
        if (_volumeDrag is { } volume)
        {
            double level = MusicGestures.VolumeLevel(volume.Start, volume.Sign, (volume.Horizontal ? point.X : point.Y) - volume.From);
            volume.Device.Level = (float)level;
            ShowDragLabel(Loc.T($"音量 {Math.Round(level * 100)}%", $"Volume {Math.Round(level * 100)}%"), true);
        }
        else ShowDragAction(point);
        e.Handled = true;
    }

    private void EndMusicDrag(MouseButtonEventArgs e)
    {
        if (_dragPress is not { } start) return;
        _dragPress = null;
        if (!_dragging) return;
        var direction = DragDirection(e.GetPosition(GestureLayer));
        bool adjusted = _volumeDrag is not null;
        // Released after the trail is put away, so LostMouseCapture finds nothing left to do.
        EndTrail();
        MusicContent.ReleaseMouseCapture();
        e.Handled = true;
        // A volume drag has done its work while it moved.
        var action = MusicGestures.Drag(Settings, direction);
        if (!adjusted && GesturesAllowed && action is not (MusicGestureAction.VolumeUp or MusicGestureAction.VolumeDown)) RunGesture(action);
    }

    // The horizontal wheel: answers whether the panel took it.
    private bool MusicWheelSwipe(int delta)
    {
        if (!GesturesAllowed || Settings.MusicSwipeLeft == MusicGestureAction.None && Settings.MusicSwipeRight == MusicGestureAction.None
            || !ScreenBounds(Island).Contains(WindowsHost.Cursor())) return false;
        int step = _swipe.Feed(delta, DateTimeOffset.UtcNow);
        if (step != 0) RunGesture(MusicGestures.Swipe(Settings, MusicGestures.Fingers(step, TouchpadReversed())));
        return true;
    }
    // The touchpad's "scrolling direction" (Settings › Bluetooth & devices › Touchpad), read when a swipe completes.
    private static bool TouchpadReversed()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad");
            return key?.GetValue("ScrollDirection") is int value ? value != 0 : true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { return true; }
    }

    private bool CanRunGesture(MusicGestureAction action) => action is MusicGestureAction.VolumeUp or MusicGestureAction.VolumeDown || _mediaTrack is { } track && action switch
    {
        MusicGestureAction.NextTrack => track.CanNext,
        MusicGestureAction.PreviousTrack => track.CanPrevious,
        MusicGestureAction.PlayPause => track.CanToggle,
        MusicGestureAction.NextPlayer => NeighbourSource(1) is not null,
        MusicGestureAction.PreviousPlayer => NeighbourSource(-1) is not null,
        _ => false
    };
    // The source after or before the current one in the source menu's order, going round from the last to the first; none while
    // only one plays.
    private string? NeighbourSource(int step)
    {
        var tracks = _mediaTracks.ToList();
        int at = _mediaTrack is { } track ? tracks.FindIndex(t => t.Id == track.Id) : -1;
        return at >= 0 && tracks.Count > 1 ? tracks[((at + step) % tracks.Count + tracks.Count) % tracks.Count].Id : null;
    }
    private void RunGesture(MusicGestureAction action)
    {
        if (!CanRunGesture(action)) return;
        if (action is MusicGestureAction.VolumeUp or MusicGestureAction.VolumeDown) { VolumeKeys.Press(action == MusicGestureAction.VolumeUp, MusicGestures.VolumeSteps); return; }
        if (_mediaTrack is not { } track) return;
        switch (action)
        {
            case MusicGestureAction.NextTrack: _ = RunMediaCommand(track, MediaCommand.Next); break;
            case MusicGestureAction.PreviousTrack: _ = RunMediaCommand(track, MediaCommand.Previous); break;
            case MusicGestureAction.PlayPause: _ = RunMediaCommand(track, MediaCommand.PlayPause); break;
            default: _media?.Select(NeighbourSource(action == MusicGestureAction.NextPlayer ? 1 : -1)); break;
        }
    }
    private string GestureLabel(MusicGestureAction action) => !CanRunGesture(action) ? SettingsWindow.GestureName(MusicGestureAction.None)
        : action == MusicGestureAction.PlayPause ? _mediaTrack?.State == MediaState.Playing ? Loc.T("暂停", "Pause") : Loc.T("播放", "Play")
        : SettingsWindow.GestureName(action);

    private void StartTrail(Point start)
    {
        GestureLayer.BeginAnimation(OpacityProperty, null);
        GestureLayer.Opacity = 1;
        GestureLayer.Children.Clear();
        _dragTrail = new Polyline { Stroke = UI.Brush(UI.Primary), Opacity = .45, StrokeThickness = 4, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        _dragTrail.Points.Add(start);
        var text = new TextBlock { FontFamily = UI.PanelFont, FontSize = 12, TextWrapping = TextWrapping.NoWrap };
        _dragLabel = new Border { Child = text, Background = UI.Brush(PanelTheme.Hex("popup")), BorderBrush = UI.Brush(PanelTheme.Hex("popupBorder")),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3, 8, 4) };
        GestureLayer.Children.Add(_dragTrail); GestureLayer.Children.Add(_dragLabel);
    }
    // The label sits centred under the trail, or above it when there is no room below, and inside the panel.
    // The trail as drawn, with the pointer's latest point, which the trail skips while it is under 2 DIPs from the last one.
    private GestureDirection DragDirection(Point pointer) => _dragTrail is { } trail
        ? MusicGestures.Direction([.. trail.Points.Select(p => (p.X, p.Y)), (pointer.X, pointer.Y)]) : GestureDirection.None;
    private void ShowDragAction(Point pointer)
    {
        var action = MusicGestures.Drag(Settings, DragDirection(pointer));
        ShowDragLabel(GestureLabel(action), CanRunGesture(action));
    }
    private void ShowDragLabel(string words, bool runs)
    {
        if (_dragTrail is not { } trail || _dragLabel is not { Child: TextBlock text } label) return;
        text.Text = words;
        text.Foreground = UI.Brush(runs ? UI.Primary : UI.Tertiary);
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = label.DesiredSize;
        double left = trail.Points.Min(p => p.X), right = trail.Points.Max(p => p.X), top = trail.Points.Min(p => p.Y), bottom = trail.Points.Max(p => p.Y);
        double width = GestureLayer.ActualWidth, height = GestureLayer.ActualHeight, gap = 10;
        double x = Math.Clamp((left + right - size.Width) / 2, 4, Math.Max(4, width - size.Width - 4));
        double y = bottom + gap + size.Height <= height - 4 ? bottom + gap : Math.Max(4, top - gap - size.Height);
        Canvas.SetLeft(label, x); Canvas.SetTop(label, y);
    }
    private void EndTrail()
    {
        _dragging = false; _dragTrail = null; _dragLabel = null;
        _volumeDrag?.Device.Dispose(); _volumeDrag = null;
        if (GestureLayer.Children.Count == 0) return;
        if (!Soft.Enabled) { GestureLayer.Children.Clear(); return; }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)) { EasingFunction = Soft.Out };
        fade.Completed += (_, _) => { if (!_dragging) { GestureLayer.Children.Clear(); GestureLayer.BeginAnimation(OpacityProperty, null); GestureLayer.Opacity = 1; } };
        GestureLayer.BeginAnimation(OpacityProperty, fade);
    }
}
