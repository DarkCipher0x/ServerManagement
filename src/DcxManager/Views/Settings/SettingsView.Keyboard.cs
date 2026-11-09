using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Services;
using DcxManager.Services.Keys;

namespace DcxManager.Views;

// SettingsView: Settings → Keyboard (0.6.36, ADR-115). Every command with its keys, grouped and searchable; change a key by
// pressing it, add a second one, remove them or put the defaults back. Clashes are shown before anything is saved, and a
// key that a terminal would keep says so. Group Policy can set the list or keep the defaults (then this page only shows).
public sealed partial class SettingsView
{
    /// <summary>The Settings tab of the keyboard shortcuts (Ctrl+Shift+/ opens it).</summary>
    internal const string KeyboardCategory = "Keyboard";
    internal const string KeyboardCardTitle = "Keyboard shortcuts";

    /// <summary>The page's list, for the self-test: drives the same rows, buttons and capture box you use.</summary>
    internal KeyboardPage? Keys { get; private set; }

    private Border KeyboardCard()
    {
        var page = Keys = new KeyboardPage(this);
        return Card(KeyboardCardTitle,
            "Everything the app does from the keyboard, with its keys. Change… records the next keys you press (Esc cancels); a key another command has is offered to move here. " +
            "Keys work in the main window and every pop-out window. In a terminal, VNC or Remote Desktop session only Ctrl+Shift, Ctrl+Alt, Ctrl+Tab and Ctrl+F-key shortcuts are taken: plain Ctrl+letter, Alt+letter and the F-keys go to the remote program there. " +
            "Every command is also in the command palette (Ctrl+Shift+P).",
            page.Root);
    }

    /// <summary>The list on the Keyboard page.</summary>
    internal sealed class KeyboardPage
    {
        private readonly SettingsView _view;
        private readonly TextBox _filter;
        private readonly CheckBox _changedOnly;
        private readonly StackPanel _list = new();
        private readonly TextBlock _lock = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
        private readonly TextBlock _problems = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
        private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        private readonly Dictionary<string, Row> _rows = new(StringComparer.Ordinal);
        private Row? _editing;

        public StackPanel Root { get; } = new();

        public KeyboardPage(SettingsView view)
        {
            _view = view;
            _filter = Hint.Set(new TextBox { Width = FieldWidth, Padding = new Thickness(6, 4, 6, 4), ToolTip = "Search by what a command does or by its keys (\"ctrl+shift\", \"F6\")" }, "Search commands or keys…");
            AutomationProperties.SetName(_filter, "Search keyboard shortcuts");
            _filter.TextChanged += (_, _) => Render();
            _changedOnly = new CheckBox { Content = "Only the ones I changed", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            _changedOnly.Click += (_, _) => Render();
            _lock.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
            _problems.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
            _result.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            bar.Children.Add(_filter); bar.Children.Add(_changedOnly);
            var resetAll = view.Btn("Reset all to defaults…", ResetAllAsked, "SmallButton");
            var copy = view.Btn("Copy the list", CopyList, "SmallButton");
            copy.ToolTip = "Copies every command and its keys as a table (paste it into a note or a document)";
            var footer = view.Row(resetAll, copy);
            var fixedKeys = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Style = (Style)view.FindResource("Hint"),
                Text = "Keys that keep their usual meaning: Tab and Shift+Tab move between controls, the arrow keys move in lists and the sidebar, Enter or Space presses a button, Shift+F10 or the menu key opens a right-click menu, Esc closes a menu or dialog (and leaves the sidebar), Alt+F4 closes a window, and Ctrl+C, Ctrl+X, Ctrl+V, Ctrl+Z, Ctrl+Y and Ctrl+A edit text. " +
                       "In a terminal Ctrl+Shift+C and Ctrl+Shift+V copy and paste. The file commander follows Midnight Commander's keys and the text editor's Vim mode follows Vim (see Help); while the commander's file list or a Docs page's text has the keyboard, their own keys (F6, Ctrl+B, Ctrl+K and others) win over these.",
            };
            Root.Children.Add(bar);
            Root.Children.Add(_lock);
            Root.Children.Add(_problems);
            Root.Children.Add(Bound(_list, nameof(AppSettings.KeyBindings)));
            Root.Children.Add(footer);
            Root.Children.Add(_result);
            Root.Children.Add(fixedKeys);
            // Changes made elsewhere (an import, another window) redraw the list while it is on screen.
            Root.Loaded += (_, _) => { KeyCommands.Changed -= OnChanged; KeyCommands.Changed += OnChanged; Render(); };
            Root.Unloaded += (_, _) => KeyCommands.Changed -= OnChanged;
            Render();
        }

        private void OnChanged() { if (_editing == null) Render(); }

        /// <summary>Rows shown now, by command id (self-test).</summary>
        internal IReadOnlyDictionary<string, Row> Rows => _rows;
        internal string ResultText => _result.Text;
        internal string Filter { get => _filter.Text; set => _filter.Text = value; }

        // ---- the list -----------------------------------------------------------------------------------------------

        internal void Render(string? focusId = null)
        {
            _editing = null;
            _list.Children.Clear();
            _rows.Clear();
            _lock.Text = KeyCommands.WhyLocked is { } why ? "🔒 " + why + " The keys below are in force; they can't be changed here." : "";
            _lock.Visibility = _lock.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            var problems = KeyCommands.Problems;
            _problems.Text = problems.Count == 0 ? "" : "⚠ " + string.Join("\n⚠ ", problems.Take(8)) + (problems.Count > 8 ? $"\n… and {problems.Count - 8} more" : "");
            _problems.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var q = _filter.Text.Trim();
            bool changedOnly = _changedOnly.IsChecked == true;
            int shown = 0;
            foreach (var group in KeyCommands.All.GroupBy(c => c.Category))
            {
                var cmds = group.Where(c => (!changedOnly || KeyCommands.IsChanged(c.Id)) && Matches(c, q)).ToList();
                if (cmds.Count == 0) continue;
                _list.Children.Add(new TextBlock { Text = group.Key.ToUpperInvariant(), Style = (Style)_view.FindResource("Label"), Margin = new Thickness(0, 12, 0, 4) });
                foreach (var c in cmds)
                {
                    var row = new Row(this, c);
                    _rows[c.Id] = row;
                    _list.Children.Add(row.Root);
                    shown++;
                }
            }
            if (shown == 0) _list.Children.Add(new TextBlock { Text = q.Length > 0 ? $"No command or key matches “{q}”." : "Nothing changed yet.", Style = (Style)_view.FindResource("Hint"), Margin = new Thickness(0, 8, 0, 0) });
            if (focusId != null && _rows.TryGetValue(focusId, out var r)) r.FocusChange();
        }

        private static bool Matches(KeyCommand c, string q)
        {
            if (q.Length == 0) return true;
            bool Has(string? s) => s != null && s.Contains(q, StringComparison.OrdinalIgnoreCase);
            if (Has(c.Title) || Has(c.Id) || Has(c.Category) || Has(c.Detail)) return true;
            // "ctrl+shift+p" or "Ctrl+Shift+," matches the keys themselves.
            return KeyCommands.Effective(c.Id).Any(k => Has(k.Shown) || Has(k.Name) || k.Shown.StartsWith(q, StringComparison.OrdinalIgnoreCase));
        }

        // ---- changes ------------------------------------------------------------------------------------------------

        /// <summary>Saves a change made through <see cref="KeyCommands"/>; the audit log records it.</summary>
        internal bool Apply(string id, Func<string?> change, string what)
        {
            var before = KeyCommands.TextAll(id);
            if (change() is { } refused) { _result.Text = "⚠ " + refused; return false; }
            _view.Save();
            AuditLog.Write("you", "keyboard", "shortcut changed", $"{id}: {before} → {KeyCommands.TextAll(id)}");
            _result.Text = $"✓ {what}: {KeyCommands.Find(id)?.Title} is now {KeyCommands.TextAll(id)}.";
            Render(focusId: id);
            return true;
        }

        private void ResetAllAsked()
        {
            if (!KeyCommands.CanEdit) { _result.Text = "⚠ " + KeyCommands.WhyLocked; return; }
            if (!Dialogs.Confirm("Keyboard shortcuts", "Put every command back on its default keys? Your changes are removed.", "Reset all")) return;
            ResetAll();
        }

        internal void ResetAll()
        {
            if (KeyCommands.ResetAll() is { } refused) { _result.Text = "⚠ " + refused; return; }
            _view.Save();
            AuditLog.Write("you", "keyboard", "shortcuts reset", "every command back on its default keys");
            _result.Text = "✓ Every command is back on its default keys.";
            Render();
        }

        /// <summary>The list as a Markdown table (the clipboard copy).</summary>
        internal static string ListText()
        {
            var sb = new System.Text.StringBuilder("| Command | Keys | Where | Id (Group Policy) |\n|---|---|---|---|\n");
            foreach (var c in KeyCommands.All)
                sb.Append("| ").Append(c.Title.Replace("|", "/")).Append(" | ").Append(KeyCommands.TextAll(c.Id)).Append(" | ").Append(KeyScopes.Title(c.Scope)).Append(" | ").Append(c.Id).Append(" |\n");
            return sb.ToString();
        }

        private void CopyList()
        {
            Menus.Clip(ListText());
            _result.Text = "✓ Copied the list of shortcuts.";
        }

        // ---- one command ----------------------------------------------------------------------------------------------

        internal sealed class Row
        {
            private readonly KeyboardPage _page;
            private readonly KeyCommand _cmd;
            private readonly Button _change, _remove, _reset;
            private readonly StackPanel _editor = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 8) };
            private readonly KeyCaptureBox _capture = new();
            private readonly TextBlock _say = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            private readonly Button _use, _add;

            public Border Root { get; }
            internal KeyCaptureBox Capture => _capture;
            internal string Saying => _say.Text;
            internal bool CanUse => _use.IsEnabled;
            internal bool CanAdd => _add.IsEnabled;
            internal bool Editing => _editor.Visibility == Visibility.Visible;

            public Row(KeyboardPage page, KeyCommand cmd)
            {
                _page = page; _cmd = cmd;
                var v = page._view;
                bool canEdit = KeyCommands.CanEdit;
                var keys = KeyCommands.Effective(cmd.Id);

                var title = new TextBlock { Text = cmd.Title, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, ToolTip = $"Command id: {cmd.Id} (for Group Policy: KeyBindings)" + (cmd.Detail is { Length: > 0 } d ? "\n" + d : "") };
                var sub = new TextBlock { Style = (Style)v.FindResource("Caption"), TextWrapping = TextWrapping.Wrap };
                var notes = new List<string>();
                if (cmd.Scope != KeyScopes.App) notes.Add(KeyScopes.Title(cmd.Scope));
                if (cmd.Scope == KeyScopes.App && keys.Count > 0 && keys.All(k => !k.WorksInRemoteSessions)) notes.Add("not in terminals");
                if (KeyCommands.IsChanged(cmd.Id)) notes.Add("changed");
                sub.Text = string.Join(" · ", notes);
                sub.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                left.Children.Add(title); left.Children.Add(sub);

                var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
                if (keys.Count == 0) chips.Children.Add(new TextBlock { Text = "no key", Style = (Style)v.FindResource("Caption"), VerticalAlignment = VerticalAlignment.Center });
                foreach (var k in keys) chips.Children.Add(Chip(k));

                _change = v.Btn("Change…", Open, "SmallButton");
                _change.ToolTip = "Record new keys for this command";
                AutomationProperties.SetName(_change, "Change the keys of " + cmd.Title);
                _remove = v.Btn("Remove", () => page.Apply(cmd.Id, () => KeyCommands.Set(cmd.Id, Array.Empty<KeyChord>()), "Removed the keys"), "SmallButton");
                _remove.ToolTip = "This command gets no key (it stays in the command palette and its menus)";
                AutomationProperties.SetName(_remove, "Remove the keys of " + cmd.Title);
                _reset = v.Btn("Default", () => page.Apply(cmd.Id, () => KeyCommands.Reset(cmd.Id), "Back to the default"), "SmallButton");
                _reset.ToolTip = "Default: " + (cmd.Defaults.Count == 0 ? "no key" : string.Join(" or ", cmd.Defaults.Select(d => d.Shown)));
                AutomationProperties.SetName(_reset, "Put the default keys back for " + cmd.Title);
                _change.IsEnabled = canEdit;
                _remove.IsEnabled = canEdit && keys.Count > 0;
                _reset.IsEnabled = canEdit && KeyCommands.IsChanged(cmd.Id);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                foreach (var b in new[] { _change, _remove, _reset }) { b.Margin = new Thickness(4, 0, 0, 0); buttons.Children.Add(b); }

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(chips, 1); Grid.SetColumn(buttons, 2);
                grid.Children.Add(left); grid.Children.Add(chips); grid.Children.Add(buttons);

                // The editor under the row: the capture box, what the keys would mean, and the choices.
                _use = v.Btn("Use these keys", () => Commit(add: false), "PrimaryButton");
                _add = v.Btn("Add as another key", () => Commit(add: true), "SmallButton");
                var cancel = v.Btn("Cancel", Close, "SmallButton");
                _use.IsEnabled = _add.IsEnabled = false;
                _capture.Captured += Recorded;
                _capture.Cancelled += Close;
                _say.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                var line = new WrapPanel();
                line.Children.Add(_capture);
                foreach (var b in new[] { _use, _add, cancel }) { b.Margin = new Thickness(8, 0, 0, 0); b.VerticalAlignment = VerticalAlignment.Center; line.Children.Add(b); }
                _editor.Children.Add(line);
                _editor.Children.Add(_say);

                var body = new StackPanel();
                body.Children.Add(grid);
                body.Children.Add(_editor);
                Root = new Border { Child = body, Padding = new Thickness(0, 5, 0, 5), BorderThickness = new Thickness(0, 0, 0, 1) };
                Root.SetResourceReference(Border.BorderBrushProperty, "Line");
                AutomationProperties.SetName(Root, $"{cmd.Title}: {KeyCommands.TextAll(cmd.Id)}");
            }

            private Border Chip(KeyChord k)
            {
                var t = new TextBlock { Text = k.Shown };
                t.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
                var b = new Border { Child = t, Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(0, 2, 6, 2), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
                b.SetResourceReference(Border.BorderBrushProperty, "Line");
                b.SetResourceReference(Border.BackgroundProperty, "Surface2");
                if (_cmd.Scope == KeyScopes.App && !k.WorksInRemoteSessions) b.ToolTip = "Not in a terminal or remote desktop: there this key goes to the remote program.";
                return b;
            }

            internal void FocusChange() { if (_change.IsEnabled) _change.Focus(); }

            internal void Open()
            {
                if (_page._editing is { } other && other != this) other.Close();
                _page._editing = this;
                _capture.Clear();
                _say.Text = "Press the keys you want (Esc cancels).";
                _use.IsEnabled = _add.IsEnabled = false;
                _editor.Visibility = Visibility.Visible;
                _capture.Focus();
            }

            internal void Close()
            {
                _editor.Visibility = Visibility.Collapsed;
                if (_page._editing == this) _page._editing = null;
                FocusChange();
            }

            /// <summary>What the recorded keys would do here: refused, taken from another command, or fine (with a note
            /// when a terminal would keep them).</summary>
            private void Recorded(KeyChord k)
            {
                _use.IsEnabled = _add.IsEnabled = false;
                if (k.Problem() is { } why) { _say.Text = $"⚠ {k.Shown}: {why}."; return; }
                var current = KeyCommands.Effective(_cmd.Id);
                var notes = new List<string>();
                if (current.Contains(k)) notes.Add($"{k.Shown} is already this command's key.");
                if (KeyCommands.Conflicts(_cmd.Id, k) is { Count: > 0 } others)
                    notes.Add($"{k.Shown} is now “{others[0].Title}”{(others.Count > 1 ? $" and {others.Count - 1} more" : "")}: using it here takes it from there.");
                if (_cmd.Scope == KeyScopes.App && !k.WorksInRemoteSessions)
                    notes.Add("In a terminal, VNC or Remote Desktop session this key goes to the remote program (Ctrl+Shift or Ctrl+Alt keys work there too).");
                _say.Text = notes.Count == 0 ? $"{k.Shown} is free." : string.Join(" ", notes);
                _use.IsEnabled = true;
                _add.IsEnabled = !current.Contains(k) && current.Count < KeyBindingList.MaxChordsPerCommand;
            }

            internal bool Commit(bool add)
            {
                if (_capture.Chord is not { } k || k.Problem() != null) return false;
                var keys = add ? KeyCommands.Effective(_cmd.Id).Append(k).ToList() : new List<KeyChord> { k };
                return _page.Apply(_cmd.Id, () => KeyCommands.Set(_cmd.Id, keys, takeFromOthers: true), add ? "Added a key" : "Changed");
            }

            internal void Remove() => _remove.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            internal void Reset() => _reset.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            internal bool ChangeEnabled => _change.IsEnabled;
            internal bool ResetEnabled => _reset.IsEnabled;
        }
    }
}
