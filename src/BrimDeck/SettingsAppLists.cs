using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;
using Microsoft.Win32;

namespace BrimDeck;

// Rows that list applications: the music's ignored apps and the clipboard's apps not recorded. The row shows the count and the
// "Add app" button at the right; a click on the row or its arrow shows the apps under the title, as the app rules row does.
public partial class SettingsWindow
{
    private FrameworkElement AppListRow(string title, Func<string> help, string showName, string addName, IReadOnlyList<FrameworkElement> lines,
        Action<Button> menu, bool open, Action<bool> opened, bool enabled = true)
    {
        if (lines.Count == 0) { open = false; opened(false); }
        var list = new StackPanel { Margin = new Thickness(20, 0, 0, 6) };
        foreach (var line in lines)
        {
            list.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider) });
            list.Children.Add(line);
        }
        var label = TextLine(title, 13, TextPrimary, FontWeights.SemiBold); label.VerticalAlignment = VerticalAlignment.Center;
        var info = HelpIcon(title, help, 14);
        info.MouseLeftButtonDown += (_, e) => e.Handled = true;
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new HelpHeading(label, info));
        Button? arrow = null;
        void Show(bool shown)
        {
            list.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            if (arrow is not null) PointArrow(arrow, shown);
        }
        void Flip() { open = !open; opened(open); Show(open); }
        if (lines.Count > 0)
        {
            arrow = ChevronButton(showName, Flip);
            arrow.Margin = new Thickness(2, 0, 0, 0);
            heading.Children.Add(arrow);
        }
        int n = lines.Count;
        var count = TextLine(n == 0 ? Loc.T("未添加", "None") : Loc.T($"{n} 个应用", n == 1 ? "1 app" : $"{n} apps"), 12, TextTertiary);
        count.VerticalAlignment = VerticalAlignment.Center; count.Margin = new Thickness(0, 0, 12, 0);
        var add = AddAppButton(addName, menu);
        add.IsEnabled = enabled;
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(count); controls.Children.Add(add);
        var header = new Grid { MinHeight = 44, Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(heading); Grid.SetColumn(controls, 1); header.Children.Add(controls);
        header.MouseLeftButtonDown += (_, e) => { if (arrow is not null) Flip(); e.Handled = true; };
        Show(open);
        var row = new StackPanel(); row.Children.Add(header); row.Children.Add(list); return row;
    }

    // One listed app: its icon, its name, lighter its file or id, and the cross that takes it off the list.
    private FrameworkElement AppLine(string name, string detail, string path, string tip, Action remove)
    {
        var row = new Grid { MinHeight = 40, Background = Brushes.Transparent, ToolTip = tip.Length > 0 ? tip : null };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(AppIcon(path));
        var labels = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        labels.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); labels.ColumnDefinitions.Add(new ColumnDefinition());
        var title = TextLine(name, 13, TextPrimary); title.VerticalAlignment = VerticalAlignment.Center; title.TextTrimming = TextTrimming.CharacterEllipsis;
        labels.Children.Add(title);
        if (detail.Length > 0 && !string.Equals(detail, name, StringComparison.OrdinalIgnoreCase))
        {
            var file = TextLine(detail, 11.5, TextTertiary);
            file.VerticalAlignment = VerticalAlignment.Center; file.Margin = new Thickness(8, 1, 0, 0); file.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(file, 1); labels.Children.Add(file);
        }
        Grid.SetColumn(labels, 1); row.Children.Add(labels);
        var cross = RemoveButton(name, remove);
        Grid.SetColumn(cross, 2); row.Children.Add(cross);
        return row;
    }

    // The menu's last entry, after a separator: choose an executable that is not running.
    private void AddBrowseItem(ContextMenu menu, string title, Action<string> chosen)
    {
        menu.Items.Add(new Separator());
        var browse = new MenuItem { Header = Loc.T("浏览可执行文件…", "Browse for an executable…") };
        browse.Click += (_, _) => { if (BrowseExecutable(title) is { } path) chosen(path); };
        menu.Items.Add(browse);
    }

    private string? BrowseExecutable(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = Loc.T("应用程序 (*.exe)|*.exe", "Applications (*.exe)|*.exe"),
            CheckFileExists = true
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }
}
