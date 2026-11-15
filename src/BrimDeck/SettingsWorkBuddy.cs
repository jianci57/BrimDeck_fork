using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    // The sign-in under way for each edition, kept across rebuilt rows so the row keeps showing it.
    private static readonly Dictionary<ProviderId, CancellationTokenSource> _workBuddyLogins = [];

    // The WorkBuddy row signs in on the WorkBuddy website in the default browser (UsageService.ConnectWorkBuddyAsync); the sign-in
    // is saved encrypted on this PC. The line shows whether it is signed in and offers to sign in or out.
    private FrameworkElement WorkBuddyDetails(AppEntry entry)
    {
        var grid = AppColumns(); grid.Margin = new Thickness(0, 0, 0, 12);
        var id = entry.QuotaSource;
        string name = ProviderCatalog.Name(id), host = new Uri(WorkBuddyUsage.For(id).Origin).Host;
        string title = Loc.T(name + " 账号", name + " account");
        string help = Loc.T($"在默认浏览器中打开 {name} 官网，登录并授权后，BrimDeck 获得自己的登录凭据，用它读取积分余额。凭据加密保存在本机，只发往 {host}。不读取 {name} 桌面版的登录。",
            $"Opens the {name} website in the default browser. After you sign in and allow access, BrimDeck gets a sign-in of its own and reads the credits with it. The sign-in is stored encrypted on this PC and sent only to {host}. The {name} desktop app's sign-in is not read.");
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "SourceLabel" });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        var caption = TextLine(title, 12, TextSecondary);
        var heading = new HelpHeading(caption, SmallHelp(title, () => help)) { Margin = new Thickness(16, 0, 16, 0), MinWidth = 52, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(heading);
        var state = TextLine("", 12.5, TextPrimary); state.VerticalAlignment = VerticalAlignment.Center; state.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(state, 1); line.Children.Add(state);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(buttons, 2); line.Children.Add(buttons);
        var signIn = ActionButton("", () => { }); signIn.Style = (Style)FindResource("SettingsOutlineButton"); signIn.Height = 34;
        AutomationProperties.SetAutomationId(signIn, "workbuddy-sign-in-" + entry.InstanceId.ToString("N"));
        var signOut = ActionButton(Loc.T("退出登录", "Sign out"), () => { }); signOut.Style = (Style)FindResource("SettingsOutlineButton"); signOut.Height = 34; signOut.Margin = new Thickness(8, 0, 0, 0);
        AutomationProperties.SetAutomationId(signOut, "workbuddy-sign-out-" + entry.InstanceId.ToString("N"));
        buttons.Children.Add(signIn); buttons.Children.Add(signOut);
        var note = TextLine("", 11.5, TextSecondary); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 4, 0, 0);

        void Show()
        {
            bool waiting = _workBuddyLogins.ContainsKey(id), connected = _app.Usage.WorkBuddyConnected(id);
            state.Text = waiting ? Loc.T("等待在浏览器中登录…", "Waiting for the sign-in in the browser…") : connected ? Loc.T("已登录", "Signed in") : Loc.T("未登录", "Not signed in");
            signIn.Content = waiting ? Loc.T("取消", "Cancel") : connected ? Loc.T("重新登录", "Sign in again") : Loc.T("在浏览器中登录", "Sign in in the browser");
            signOut.Visibility = connected && !waiting ? Visibility.Visible : Visibility.Collapsed;
            note.Visibility = note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        static bool Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return true; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        }
        signIn.Click += async (_, _) =>
        {
            if (_workBuddyLogins.TryGetValue(id, out var running)) { running.Cancel(); return; }
            var login = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
            _workBuddyLogins[id] = login; note.Text = ""; Show();
            try
            {
                bool done = await _app.Usage.ConnectWorkBuddyAsync(id, Open, login.Token);
                note.Text = done ? "" : Loc.T("10 分钟内没有完成登录，请重试。", "The sign-in was not finished within 10 minutes. Try again.");
                if (done) _ = _app.Deck.RefreshAsync();
            }
            catch (OperationCanceledException) { note.Text = ""; }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or System.Text.Json.JsonException or WorkBuddyUsage.RejectedException)
            { note.Text = ex is InvalidDataException ? ex.Message : Loc.T($"暂时无法连接 {name} 官网，请稍后重试。", $"The {name} website cannot be reached right now. Try again later."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            { note.Text = Loc.T("登录凭据保存失败，请检查本机设置目录。", "The sign-in could not be saved. Check the local settings folder."); }
            finally { if (_workBuddyLogins.TryGetValue(id, out var current) && current == login) _workBuddyLogins.Remove(id); login.Dispose(); Show(); }
        };
        signOut.Click += (_, _) =>
        {
            try { _app.Usage.DisconnectWorkBuddy(id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or System.Text.Json.JsonException)
            { note.Text = Loc.T("登录凭据删除失败，请检查本机设置目录。", "The sign-in could not be removed. Check the local settings folder."); }
            Show(); _ = _app.Deck.RefreshAsync();
        };
        Show();
        line.Margin = new Thickness(0, 5, 0, 5);
        var lines = new StackPanel(); lines.Children.Add(line);
        var noteLine = new Grid();
        noteLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "SourceLabel" });
        noteLine.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(note, 1); noteLine.Children.Add(note); lines.Children.Add(noteLine);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(lines, 1); Grid.SetColumnSpan(lines, 6); grid.Children.Add(lines);
        return grid;
    }
}
