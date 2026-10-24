using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DcxManager.Views.Editors;

/// <summary>Find and replace for the text editor (Ctrl+F / Ctrl+H, F3 / Shift+F3): plain text or a regular expression
/// (with a time limit, so a pathological pattern can't hang the editor), match case, replace one or all — each replace
/// is an undo step.</summary>
public sealed class FindBar : DockPanel
{
    private readonly TextBox _box;
    private readonly Action<string> _say;
    private readonly TextBox _find = new() { Width = 220, Padding = new Thickness(4, 2, 4, 2) };
    private readonly TextBox _replace = new() { Width = 220, Padding = new Thickness(4, 2, 4, 2) };
    private readonly CheckBox _case = new() { Content = "Aa", ToolTip = "Match case", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private readonly CheckBox _regex = new() { Content = ".*", ToolTip = "Regular expression", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private readonly StackPanel _replaceRow;

    public FindBar(TextBox box, Action<string> say)
    {
        _box = box; _say = say;
        Margin = new Thickness(10, 0, 10, 6);
        Visibility = Visibility.Collapsed;
        Button B(string t, Action a) { var b = new Button { Content = t, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) }; b.Click += (_, _) => a(); return b; }
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(new TextBlock { Text = "Find", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        top.Children.Add(_find); top.Children.Add(_case); top.Children.Add(_regex);
        top.Children.Add(B("↓ Next", () => Next(false))); top.Children.Add(B("↑ Previous", () => Next(true))); top.Children.Add(B("✕", Hide));
        _replaceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        _replaceRow.Children.Add(new TextBlock { Text = "Replace", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _replaceRow.Children.Add(_replace);
        _replaceRow.Children.Add(B("Replace", ReplaceOne)); _replaceRow.Children.Add(B("Replace all", ReplaceAll));
        var col = new StackPanel(); col.Children.Add(top); col.Children.Add(_replaceRow);
        Children.Add(col);
        _find.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Next(Keyboard.Modifiers == ModifierKeys.Shift); e.Handled = true; } else if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
        _replace.KeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
    }

    public void Show(bool replace)
    {
        Visibility = Visibility.Visible;
        _replaceRow.Visibility = replace ? Visibility.Visible : Visibility.Collapsed;
        if (_box.SelectionLength is > 0 and < 200 && !_box.SelectedText.Contains('\n')) _find.Text = _box.SelectedText;
        _find.Focus(); _find.SelectAll();
    }

    private void Hide() { Visibility = Visibility.Collapsed; _box.Focus(); }

    private System.Text.RegularExpressions.Regex? Pattern()
    {
        if (_find.Text.Length == 0) return null;
        try
        {
            var opts = _case.IsChecked == true ? System.Text.RegularExpressions.RegexOptions.None : System.Text.RegularExpressions.RegexOptions.IgnoreCase;
            return new System.Text.RegularExpressions.Regex(_regex.IsChecked == true ? _find.Text : System.Text.RegularExpressions.Regex.Escape(_find.Text), opts | System.Text.RegularExpressions.RegexOptions.Multiline, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex) { _say("pattern: " + ex.Message); return null; }
    }

    /// <summary>Selects the next (or previous) match, wrapping around.</summary>
    public void Next(bool back)
    {
        if (Pattern() is not { } rx) return;
        try
        {
            var all = rx.Matches(_box.Text).Where(m => m.Length > 0).ToList();
            if (all.Count == 0) { _say("not found"); return; }
            var from = back ? _box.SelectionStart : _box.SelectionStart + Math.Max(1, _box.SelectionLength);
            var m = back ? all.LastOrDefault(x => x.Index < from) ?? all[^1] : all.FirstOrDefault(x => x.Index >= from) ?? all[0];
            _box.Select(m.Index, m.Length);
            _box.ScrollToLine(_box.GetLineIndexFromCharacterIndex(m.Index));
            _say($"match {all.IndexOf(m) + 1} of {all.Count}");
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { _say("the pattern took too long"); }
    }

    private void ReplaceOne()
    {
        if (Pattern() is not { } rx) return;
        if (_box.SelectionLength > 0 && rx.Match(_box.SelectedText) is { Success: true } m && m.Length == _box.SelectionLength)
            _box.SelectedText = _regex.IsChecked == true ? m.Result(_replace.Text) : _replace.Text;
        Next(false);
    }

    private void ReplaceAll()
    {
        if (Pattern() is not { } rx) return;
        try
        {
            int n = rx.Matches(_box.Text).Count;
            if (n == 0) { _say("not found"); return; }
            var text = _regex.IsChecked == true ? rx.Replace(_box.Text, _replace.Text) : rx.Replace(_box.Text, _ => _replace.Text);
            _box.SelectAll(); _box.SelectedText = text;   // one undo step
            _box.Select(0, 0);
            _say($"{n} replaced");
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { _say("the pattern took too long"); }
    }

    /// <summary>Self-test: find and replace without the UI.</summary>
    internal void SetForTest(string find, string replace, bool regex) { _find.Text = find; _replace.Text = replace; _regex.IsChecked = regex; }
    internal void ReplaceAllForTest() => ReplaceAll();
}
