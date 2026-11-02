using System.Windows;
using System.Windows.Controls;
using BrimDeck;

// The settings window's keyboard focus rules that need real WPF elements: a radio group's one Tab stop, and the row whose focused
// control folds away. Nothing is shown on the screen (the window is off it, unactivated) and no keyboard input is sent.
internal static class SettingsFocusTests
{
    public static void Run(Action<string, bool> check)
    {
        // A group is one Tab stop: the chosen option, or the first when none is chosen.
        var group = new StackPanel();
        var one = new RadioButton { GroupName = "g" }; var two = new RadioButton { GroupName = "g" }; var three = new RadioButton { GroupName = "g" };
        foreach (var radio in new[] { one, two, three }) group.Children.Add(radio);
        RadioGroups.SetEnabled(group, true);
        bool Stops(params bool[] expected) => new[] { one, two, three }.Select(radio => System.Windows.Input.KeyboardNavigation.GetIsTabStop(radio)).SequenceEqual(expected);
        two.IsChecked = true;
        check("A radio group's Tab stop is its chosen option", Stops(false, true, false));
        two.IsChecked = false;
        check("When the chosen option is cleared (both presets off) the Tab stop goes back to the first option", Stops(true, false, false));
        three.IsChecked = true;
        check("A later choice takes the Tab stop again", Stops(false, false, true));

        var window = new Window { Width = 200, Height = 100, Left = -20000, Top = -20000, ShowActivated = false, WindowStyle = WindowStyle.None };
        var handle = new Border { Focusable = true }; var field = new TextBox(); var details = new StackPanel(); details.Children.Add(field);
        var line = new Grid(); line.Children.Add(handle);
        var inner = new StackPanel(); inner.Children.Add(line); inner.Children.Add(details);
        var row = new Border { Child = inner };
        var outside = new TextBox(); var host = new StackPanel(); host.Children.Add(row); host.Children.Add(outside);
        window.Content = host;
        try
        {
            window.Show();
            check("A focused control that is still shown keeps the focus where it is", !RowFocus.Orphaned(row, field));
            details.Visibility = Visibility.Collapsed;
            check("A focused control inside the row that folds away has lost the focus, and is for the handle to take", RowFocus.Orphaned(row, field));
            check("The handle, which stays shown, is not orphaned", !RowFocus.Orphaned(row, handle));
            outside.Visibility = Visibility.Collapsed;
            check("A control outside the row is not the row's to rescue", !RowFocus.Orphaned(row, outside));
            check("No focus at all is left alone", !RowFocus.Orphaned(row, null));
        }
        finally { window.Close(); }

        NumberBoxClicks(check);
    }

    // A left press on an element, as the two routed events WPF raises for the mouse: MouseDown (which the settings window's "empty
    // click releases the focus" rule listens to) and MouseLeftButtonDown (which the number box's frame listens to). Which of the two
    // reaches the element first is WPF's business and the test cannot press a real button to find out, so both orders are tried.
    private static void Press(UIElement target, bool leftButtonFirst)
    {
        System.Windows.Input.MouseButtonEventArgs Args(RoutedEvent routed) =>
            new(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = routed, Source = target };
        if (leftButtonFirst) target.RaiseEvent(Args(UIElement.MouseLeftButtonDownEvent));
        target.RaiseEvent(Args(UIElement.MouseDownEvent));
        if (!leftButtonFirst) target.RaiseEvent(Args(UIElement.MouseLeftButtonDownEvent));
    }

    // The number box of the settings window as it is built there (NumberBox.Create): a framed field with its unit, a divider and two
    // steppers, in a window that takes the focus off a field when empty space is clicked. Clicking its padding or its unit puts the
    // focus in the field and selects its number; clicking a stepper does not; clicking empty space still releases the focus.
    private static void NumberBoxClicks(Action<string, bool> check)
    {
        var holder = new Border { Focusable = true };
        var field = new TextBox { Text = "260", Width = 40 };
        var unit = new TextBlock { Text = "ms" };
        var plus = new System.Windows.Controls.Primitives.RepeatButton { Width = 21, Height = 26, Content = "+" };
        var minus = new System.Windows.Controls.Primitives.RepeatButton { Width = 21, Height = 26, Content = "-" };
        var brush = System.Windows.Media.Brushes.White;
        // The box through the factory the settings window builds its own with.
        var chip = NumberBox.Create(field, unit, minus, plus, brush, brush, brush);
        var other = new TextBox { Text = "other" };
        var empty = new Border { Height = 20, Background = System.Windows.Media.Brushes.Transparent };
        var host = new StackPanel(); host.Children.Add(holder); host.Children.Add(chip); host.Children.Add(other); host.Children.Add(empty);
        var window = new Window { Width = 300, Height = 200, Left = -20000, Top = -20000, ShowActivated = false, WindowStyle = WindowStyle.None, Content = host };
        EmptyClickFocus.Attach(window, () => holder);
        try
        {
            window.Show();
            foreach (bool leftFirst in new[] { true, false })
                foreach (var (name, target) in new (string, UIElement)[] { ("the box's padding", chip), ("the unit beside the number", unit) })
                {
                    System.Windows.Input.Keyboard.Focus(other);
                    field.Select(0, 0);
                    Press(target, leftFirst);
                    check($"A click on {name} focuses the number in the box and selects it ({(leftFirst ? "MouseLeftButtonDown first" : "MouseDown first")})",
                        System.Windows.Input.Keyboard.FocusedElement == field && field.SelectedText == "260");
                }
            System.Windows.Input.Keyboard.Focus(field);
            Press(empty, leftButtonFirst: false);
            check("A click on empty space still takes the focus off the number", System.Windows.Input.Keyboard.FocusedElement == holder);
        }
        finally { window.Close(); }
    }
}
