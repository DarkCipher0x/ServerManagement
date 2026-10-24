using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using DcxManager.Files;
using DcxManager.Services;
using DcxManager.Views.Editors;

namespace DcxManager.Views;

/// <summary>
/// The text editor for local and remote files (File commander F4, Files → Edit). Save writes straight back (over SFTP for
/// remote files); unsaved text is journaled for crash recovery (ADR-022) when the file's place is known. 0.3.0 §13:
/// an optional Vim mode (remembered; Ctrl+Alt+V), find / replace (Ctrl+F, Ctrl+H) and go to line (Ctrl+G), the line and
/// column, the file's own line endings kept (a Linux file stays LF), and before saving a remote or local file the editor
/// checks that it didn't change since it was opened (someone else's edit is never overwritten silently). Nothing is
/// written to local disk for a remote file.
/// </summary>
public static class TextEditorWindow
{
    /// <summary>The editors open now (the self-test drives them).</summary>
    internal static readonly List<(Window Window, TextBox Box, TextBlock Status, Func<Task> Save)> Open_ = new();
    /// <summary>The Vim engine of each open editor (self-test).</summary>
    internal static readonly Dictionary<Window, VimEngine> Vim_ = new();

    public static Window Open(string title, string text, Func<string, Task> save, FsRef? source = null, string? path = null, Recovery.Draft? recovered = null)
    {
        var w = Dialogs.MakeWindow(recovered != null ? title + "  (recovered)" : title, 1000, 720);
        w.ShowInTaskbar = true; w.Owner = null; w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (SelfTest.Running) { w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -10000; w.Top = -10000; w.ShowInTaskbar = false; w.ShowActivated = false; }
        var draft = recovered ?? new Recovery.Draft { Title = title, Source = source?.ToString() ?? "local", Path = path ?? "", OriginalHash = Recovery.Hash(text) };
        if (recovered != null) text = recovered.Text;
        // The file's own line ending: a file with no CRLF is saved with LF only (the TextBox types CRLF on Enter).
        bool crlf = text.Contains("\r\n");
        var box = new TextBox
        {
            Text = text, AcceptsReturn = true, AcceptsTab = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8, 6, 8, 6),
        }.Fs(FontStep.Control).Editor();
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center }.Fs(FontStep.Small);
        status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var where = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) }.Fs(FontStep.Small);
        where.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var mode = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), FontWeight = FontWeights.Bold }.Fs(FontStep.Small).Mono();
        mode.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var saveBtn = new Button { Content = "Save  (Ctrl+S)", Style = (Style)Application.Current.Resources["PrimaryButton"] };
        var wrap = new CheckBox { Content = "wrap", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        // 0.3.0 §13 (review gap): line numbers and syntax colours, both switchable; colours only for a format the file name names.
        var lang = SyntaxHighlighter.LanguageFor(path ?? title);
        var gutter = new EditorGutter(box);
        var numbers = new CheckBox { Content = "line numbers", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), IsChecked = AppSettings.Current.EditorLineNumbers };
        var colours = new CheckBox { Content = "colours" + (lang != null ? " (" + lang + ")" : ""), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), IsChecked = lang != null && AppSettings.Current.EditorSyntaxColours, IsEnabled = lang != null, ToolTip = lang != null ? "Syntax colours drawn over the text; switch off if they look misaligned" : "No colours: the file's name doesn't say what format it is" };
        SyntaxHighlighter.Overlay? overlay = null;
        void Colours(bool on)
        {
            if (on && lang != null) { overlay ??= new SyntaxHighlighter.Overlay(box, lang); overlay.Attach(); }
            else overlay?.Detach();
        }
        numbers.Checked += (_, _) => { gutter.Visibility = Visibility.Visible; AppSettings.Current.EditorLineNumbers = true; try { AppSettings.Save(); } catch { } };
        numbers.Unchecked += (_, _) => { gutter.Visibility = Visibility.Collapsed; AppSettings.Current.EditorLineNumbers = false; try { AppSettings.Save(); } catch { } };
        colours.Checked += (_, _) => { Colours(true); AppSettings.Current.EditorSyntaxColours = true; try { AppSettings.Save(); } catch { } };
        colours.Unchecked += (_, _) => { Colours(false); AppSettings.Current.EditorSyntaxColours = false; try { AppSettings.Save(); } catch { } };
        gutter.Visibility = numbers.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        var vimBox = new CheckBox { Content = "Vim", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), IsChecked = AppSettings.Current.EditorVimMode, ToolTip = "Vim keys (normal / insert / visual, :w :q, / search) — Ctrl+Alt+V" };
        wrap.Checked += (_, _) => box.TextWrapping = TextWrapping.Wrap; wrap.Unchecked += (_, _) => box.TextWrapping = TextWrapping.NoWrap;
        bool dirty = false, loading = true;
        string current = text;   // a copy the journal can read from any thread
        string openedHash = Recovery.Hash(text);
        var track = source != null && path != null
            ? Recovery.TrackDraft(() => dirty ? new Recovery.Draft { Id = draft.Id, Title = draft.Title, Source = draft.Source, Path = draft.Path, OriginalHash = draft.OriginalHash, Text = current, Updated = DateTime.Now } : null)
            : null;

        // Vim (0.3.0 §13): an engine over the TextBox, keys routed only while the box is switched on.
        var buffer = new TextBoxVimBuffer(box);
        var vim = new VimEngine(buffer);
        // The Vim profile (options + mappings): picked here, edited in Settings → Text editor.
        var profiles = AppSettings.Current.EditorVimProfiles.Count > 0 ? AppSettings.Current.EditorVimProfiles : VimProfile.Defaults();
        var profileBox = new ComboBox { ItemsSource = profiles, DisplayMemberPath = "Name", Width = 220, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), ToolTip = "Vim profile: options and key mappings (Settings → Text editor)" };
        profileBox.SelectedItem = profiles.FirstOrDefault(p => p.Name == AppSettings.Current.EditorVimProfile) ?? profiles[0];
        void ApplyProfile()
        {
            vim.Config = VimConfig.Parse((profileBox.SelectedItem as VimProfile)?.Rc ?? "");
            if (vim.Config.Wrap is { } wr) wrap.IsChecked = wr;
        }
        ApplyProfile();
        profileBox.SelectionChanged += (_, _) =>
        {
            ApplyProfile();
            if (profileBox.SelectedItem is VimProfile vp) { AppSettings.Current.EditorVimProfile = vp.Name; try { AppSettings.Save(); } catch { } }
        };
        void ShowMode()
        {
            mode.Text = vimBox.IsChecked == true ? (vim.ModeText.Length > 0 ? vim.ModeText : "-- NORMAL --") + (vim.Message.Length > 0 ? "   " + vim.Message : "") : "";
            box.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, vimBox.IsChecked == true && vim.Current != VimEngine.Mode.Insert ? "Accent" : "Fg");
        }
        void ShowWhere()
        {
            var i = Math.Min(box.CaretIndex, box.Text.Length);
            var line = box.Text.AsSpan(0, i).Count('\n') + 1;
            var col = i - (box.Text.LastIndexOf('\n', Math.Max(0, i - 1)) + 1) + 1;
            where.Text = $"Ln {line}, Col {col}  ·  {(crlf ? "CRLF" : "LF")}  ·  UTF-8";
        }
        box.SelectionChanged += (_, _) => ShowWhere();
        box.TextChanged += (_, _) =>
        {
            ShowWhere();
            if (loading) return;
            dirty = true; current = box.Text; status.Text = recovered != null ? "recovered draft, modified — not saved yet" : "modified";
            if (track != null) Recovery.Changed();
        };

        string ForSave() => crlf ? box.Text.Replace("\r\n", "\n").Replace("\n", "\r\n") : box.Text.Replace("\r\n", "\n");

        async Task DoSave()
        {
            try
            {
                saveBtn.IsEnabled = false;
                // Someone else changed the file since it was opened (or, for a recovered draft, before the crash): ask.
                if (source != null && path != null && await CurrentHashAsync(source, path) is { } now && now != (recovered != null ? draft.OriginalHash : openedHash)
                    && !Dialogs.Confirm("The file changed", $"{path} has changed on {(source.Kind == "local" ? "disk" : "the server")} since you opened it. Overwrite those changes with your text?", "Overwrite", danger: true))
                    return;
                var outText = ForSave();
                await save(outText);
                dirty = false; draft.OriginalHash = openedHash = Recovery.Hash(outText); recovered = null;
                status.Text = $"saved {DateTime.Now:HH:mm:ss}";
                if (track != null) Recovery.Changed();
            }
            catch (Exception ex) { Dialogs.Error("Save", ex.Message); }
            finally { saveBtn.IsEnabled = true; }
        }

        vim.Ex += async cmd =>
        {
            switch (cmd)
            {
                case ":w": await DoSave(); break;
                case ":wq" or ":x" or ":wq!": await DoSave(); if (!dirty) w.Close(); break;
                case ":q": if (!dirty) w.Close(); else status.Text = "E37: No write since last change (add ! to override)"; break;
                case ":q!": dirty = false; w.Close(); break;
                case ":e!": box.Text = text; dirty = false; status.Text = "reverted"; break;
            }
        };
        vimBox.Checked += (_, _) => { AppSettings.Current.EditorVimMode = true; try { AppSettings.Save(); } catch { } ShowMode(); };
        vimBox.Unchecked += (_, _) => { AppSettings.Current.EditorVimMode = false; try { AppSettings.Save(); } catch { } ShowMode(); };
        box.PreviewTextInput += (_, e) =>
        {
            if (vimBox.IsChecked != true) return;
            if (vim.Current == VimEngine.Mode.Insert)
            {
                // Insert-mode mappings ("inoremap jk <Esc>").
                if (e.Text.Length == 1 && vim.InsertChar(e.Text[0])) { e.Handled = true; ShowMode(); }
                return;
            }
            foreach (var ch in e.Text) vim.Key(ch.ToString());
            e.Handled = true;
            ShowMode();
        };

        // Find / replace and go to line.
        var find = new FindBar(box, s => status.Text = s);
        saveBtn.Click += async (_, _) => await DoSave();
        w.PreviewKeyDown += async (_, e) =>
        {
            var mods = Keyboard.Modifiers;
            if (e.Key == Key.S && mods == ModifierKeys.Control) { e.Handled = true; await DoSave(); return; }
            if (e.Key == Key.F && mods == ModifierKeys.Control) { e.Handled = true; find.Show(replace: false); return; }
            if (e.Key == Key.H && mods == ModifierKeys.Control) { e.Handled = true; find.Show(replace: true); return; }
            if (e.Key == Key.G && mods == ModifierKeys.Control) { e.Handled = true; GoToLine(box); return; }
            if (e.Key == Key.V && mods == (ModifierKeys.Control | ModifierKeys.Alt)) { e.Handled = true; vimBox.IsChecked = vimBox.IsChecked != true; return; }
            if (e.Key == Key.F3) { e.Handled = true; find.Next(mods == ModifierKeys.Shift); return; }
            // Tab follows the profile (spaces or a tab), in insert mode or with Vim off.
            if (e.Key == Key.Tab && mods == ModifierKeys.None && box.IsKeyboardFocusWithin && (vimBox.IsChecked != true || vim.Current == VimEngine.Mode.Insert))
            { e.Handled = true; box.SelectedText = vim.Config.TabText; box.CaretIndex = box.SelectionStart + box.SelectionLength; box.SelectionLength = 0; return; }
            if (vimBox.IsChecked != true || !box.IsKeyboardFocusWithin) return;
            var key = (e.Key == Key.System ? e.SystemKey : e.Key) switch
            {
                Key.Escape => "Esc",
                Key.Enter when vim.Current != VimEngine.Mode.Insert => "Enter",
                Key.Back when vim.Current is VimEngine.Mode.Command => "Backspace",
                Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End when vim.Current is VimEngine.Mode.Normal or VimEngine.Mode.Visual or VimEngine.Mode.VisualLine => e.Key.ToString(),
                Key.R when mods == ModifierKeys.Control => "C-r",
                Key.D when mods == ModifierKeys.Control => "C-d",
                Key.U when mods == ModifierKeys.Control => "C-u",
                Key.Back or Key.Delete when vim.Current != VimEngine.Mode.Insert => "Nop",
                _ => null,
            };
            if (key == null) return;
            if (key != "Nop") vim.Key(key);
            e.Handled = true;
            ShowMode();
        };
        w.Closing += (_, e) => { if (dirty && !Dialogs.Confirm("Unsaved changes", "Close without saving?", "Discard", danger: true)) e.Cancel = true; };
        w.Closed += (_, _) => { dirty = false; track?.Dispose(); lock (Open_) { Open_.RemoveAll(o => o.Window == w); Vim_.Remove(w); } };

        var bar = new DockPanel { Margin = new Thickness(10, 8, 10, 8) };
        DockPanel.SetDock(saveBtn, Dock.Right); DockPanel.SetDock(wrap, Dock.Right); DockPanel.SetDock(numbers, Dock.Right); DockPanel.SetDock(colours, Dock.Right); DockPanel.SetDock(vimBox, Dock.Right); DockPanel.SetDock(profileBox, Dock.Right);
        bar.Children.Add(saveBtn); bar.Children.Add(wrap); bar.Children.Add(numbers); bar.Children.Add(colours); bar.Children.Add(vimBox); bar.Children.Add(profileBox); bar.Children.Add(status);
        var foot = new DockPanel { Margin = new Thickness(10, 4, 10, 6) };
        DockPanel.SetDock(where, Dock.Right);
        foot.Children.Add(where); foot.Children.Add(mode);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); DockPanel.SetDock(find, Dock.Top); DockPanel.SetDock(foot, Dock.Bottom);
        var editorRow = new DockPanel();
        DockPanel.SetDock(gutter, Dock.Left);
        editorRow.Children.Add(gutter);
        editorRow.Children.Add(new AdornerDecorator { Child = box });   // the colour overlay lives in this adorner layer
        root.Children.Add(bar); root.Children.Add(find); root.Children.Add(foot); root.Children.Add(editorRow);
        w.Content = root;
        w.Loaded += (_, _) => Colours(colours.IsChecked == true);
        loading = false;
        ShowWhere(); ShowMode();
        if (recovered != null)
        {
            // Not saved anywhere until Save: the draft stays journaled while it is open.
            dirty = true;
            status.Text = $"recovered draft from {recovered.Updated:yyyy-MM-dd HH:mm:ss} — not saved yet";
            if (track != null) Recovery.Changed();
        }
        lock (Open_) { Open_.Add((w, box, status, DoSave)); Vim_[w] = vim; }
        w.Show();
        if (!SelfTest.Running) box.Focus();
        return w;
    }

    private static void GoToLine(TextBox box)
    {
        if (Dialogs.Prompt("Go to line", "Line number:") is not { } s || !int.TryParse(s.Trim(), out var n) || n < 1) return;
        var idx = 0;
        for (int i = 1; i < n && idx >= 0; i++) { idx = box.Text.IndexOf('\n', idx); if (idx >= 0) idx++; }
        box.CaretIndex = idx < 0 ? box.Text.Length : idx;
        box.Focus();
        box.ScrollToLine(Math.Max(0, box.GetLineIndexFromCharacterIndex(box.CaretIndex)));
    }

    /// <summary>SHA-256 of the file's current text, or null when it can't be read (gone, no connection).</summary>
    private static async Task<string?> CurrentHashAsync(FsRef source, string path)
    {
        try
        {
            using var fs = source.Open();
            await using var s = await fs.OpenReadAsync(path);
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            return Recovery.Hash(Encoding.UTF8.GetString(ms.ToArray()));
        }
        catch { return null; }
    }

    /// <summary>A recovered draft back in the editor; Save writes it to its original place.</summary>
    public static Window OpenRecovered(Recovery.Draft d)
    {
        var src = FsRef.Parse(d.Source);
        return Open($"{src.Title}:{d.Path}", d.Text, async text =>
        {
            using var fs = src.Open();
            await using (var w = await fs.CreateAsync(d.Path))
                await w.WriteAsync(new UTF8Encoding(false).GetBytes(text));
            AuditLog.Write("you", src.Title, "edited " + d.Path + " (recovered draft)");
        }, src, d.Path, d);
    }
}
