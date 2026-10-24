using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;
using DcxManager.Views.Editors;

namespace DcxManager.Views;

// SettingsView: the text editor card (0.3.0 §13): Vim mode and Vim profiles — small vimrc files of options and key mappings.
public sealed partial class SettingsView
{
    /// <summary>tmux (0.6.6, ADR-085): the terminal's ⧉ tmux button — install when missing, the session name, your config.</summary>
    private Border TmuxCard()
    {
        var install = Bound(new CheckBox { Content = "Install tmux when it's missing (asks first; the host's package manager, with sudo)", IsChecked = _s.TmuxInstall }, nameof(AppSettings.TmuxInstall));
        install.Click += (_, _) => { _s.TmuxInstall = install.IsChecked == true; Save(); };
        var session = Bound(new TextBox { Text = _s.TmuxSession, Width = 200, ToolTip = "Letters, digits, _ . - (up to 32)" }, nameof(AppSettings.TmuxSession));
        var config = Bound(new TextBox { Text = _s.TmuxConfig, AcceptsReturn = true, AcceptsTab = true, Height = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap }.Mono(), nameof(AppSettings.TmuxConfig));
        Menus.ForText(config);
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        result.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var save = Btn("Save tmux settings", () =>
        {
            var name = session.Text.Trim();
            if (!Ssh.Tmux.ValidName(name)) { Dialogs.Error("tmux", "The session name may only have letters, digits, _ . - (up to 32)."); return; }
            if (System.Text.Encoding.UTF8.GetByteCount(config.Text) > Ssh.Tmux.MaxConfigBytes) { Dialogs.Error("tmux", $"The config is larger than {Ssh.Tmux.MaxConfigBytes / 1024} KB."); return; }
            _s.TmuxSession = name; _s.TmuxConfig = config.Text.Replace("\r\n", "\n");
            Save();
            AuditLog.Write("you", "terminal", "tmux settings saved", $"session {name} · config {(_s.TmuxConfig.Trim().Length > 0 ? _s.TmuxConfig.Split('\n').Length + " line(s)" : "none")}");
            result.Text = "✓ Saved. The next ⧉ tmux writes your config to the host and uses it.";
        }, "PrimaryButton");
        return Card("tmux",
            "A terminal's ⧉ tmux button (SSH to Linux or macOS) starts tmux on that host, or reattaches your session there. If tmux is missing it offers to install it with the host's package manager (apt, dnf, yum, zypper, pacman, apk or brew); sudo asks for its password in the terminal. Your config below is written to ~/.config/dcx-servermanagement/tmux.conf on the host (only you can read it) and used for the session: your own ~/.tmux.conf is never touched.",
            install, Labeled("SESSION NAME", session), L("YOUR TMUX CONFIG (PASTE IT HERE; EMPTY = TMUX'S DEFAULTS / YOUR ~/.tmux.conf)"), config, Row(save), result);
    }

    private Border EditorCard()
    {
        if (_s.EditorVimProfiles.Count == 0) _s.EditorVimProfiles = VimProfile.Defaults();
        var list = new ComboBox { Width = FieldWidth, DisplayMemberPath = "Name" };
        var rc = new TextBox { AcceptsReturn = true, AcceptsTab = true, Height = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap }.Mono();
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        result.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        bool loading = false;
        void Reload(VimProfile? select = null)
        {
            loading = true;
            list.ItemsSource = null; list.ItemsSource = _s.EditorVimProfiles;
            list.SelectedItem = select ?? _s.EditorVimProfiles.FirstOrDefault(p => p.Name == _s.EditorVimProfile) ?? _s.EditorVimProfiles[0];
            rc.Text = (list.SelectedItem as VimProfile)?.Rc ?? "";
            loading = false;
            Validate();
        }
        void Validate()
        {
            var c = VimConfig.Parse(rc.Text);
            result.Text = c.Errors.Count == 0
                ? $"✓ tab {c.TabStop}, shift {c.ShiftWidth}, {(c.ExpandTab ? "spaces" : "tabs")}{(c.IgnoreCase ? ", ignore case" + (c.SmartCase ? " (smart)" : "") : "")} · {c.Normal.Count} normal, {c.Insert.Count} insert, {c.Visual.Count} visual mapping(s)"
                : "⚠ " + string.Join("\n⚠ ", c.Errors.Take(8));
        }
        list.SelectionChanged += (_, _) =>
        {
            if (loading || list.SelectedItem is not VimProfile p) return;
            loading = true; rc.Text = p.Rc; loading = false;
            _s.EditorVimProfile = p.Name; Save(); Validate();
        };
        rc.TextChanged += (_, _) => { if (!loading && list.SelectedItem is VimProfile p) { p.Rc = rc.Text; Save(); Validate(); } };
        string? AskName(string title, string initial)
        {
            var n = Dialogs.Prompt(title, "Profile name:", initial)?.Trim();
            if (string.IsNullOrEmpty(n)) return null;
            if (n.Length > 60 || n.Any(char.IsControl)) { Dialogs.Error(title, "At most 60 characters."); return null; }
            if (_s.EditorVimProfiles.Any(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) { Dialogs.Error(title, "There is already a profile with that name."); return null; }
            return n;
        }
        void New(bool copy)
        {
            if (AskName(copy ? "Duplicate profile" : "New profile", copy && list.SelectedItem is VimProfile cur ? cur.Name + " (copy)" : "My profile") is not { } n) return;
            var p = new VimProfile { Name = n, Rc = copy && list.SelectedItem is VimProfile c ? c.Rc : "\" set tabstop=4 shiftwidth=4 expandtab\n" };
            _s.EditorVimProfiles.Add(p); _s.EditorVimProfile = n; Save(); Reload(p);
        }
        void Rename()
        {
            if (list.SelectedItem is not VimProfile p || AskName("Rename profile", p.Name) is not { } n) return;
            if (_s.EditorVimProfile == p.Name) _s.EditorVimProfile = n;
            p.Name = n; Save(); Reload(p);
        }
        void Delete()
        {
            if (list.SelectedItem is not VimProfile p) return;
            if (_s.EditorVimProfiles.Count == 1) { Dialogs.Info("Delete profile", "Keep at least one profile."); return; }
            if (!Dialogs.Confirm("Delete profile", $"Delete the Vim profile “{p.Name}”?", "Delete", danger: true)) return;
            _s.EditorVimProfiles.Remove(p); _s.EditorVimProfile = _s.EditorVimProfiles[0].Name; Save(); Reload();
        }
        Reload();
        return Card("Text editor",
            "The editor for local and remote files (File commander F4, Files → Edit) and the hex editor (Ctrl+F4, or any binary file). " +
            "Vim mode is switched in the editor (Ctrl+Alt+V). A Vim profile is a small vimrc: set tabstop / shiftwidth / expandtab / ignorecase / smartcase / wrap, let mapleader, and nnoremap / inoremap / vnoremap mappings. " +
            "Profiles only change keys and a few options — they can't run commands or read files.",
            Check("Use Vim keys in the editor", _s.EditorVimMode, v => _s.EditorVimMode = v),
            Row(Labeled("PROFILE", list), Btn("New…", () => New(false)), Btn("Duplicate…", () => New(true)), Btn("Rename…", Rename), Btn("Delete", Delete, "DangerButton")),
            L("PROFILE (vimrc)"), rc, result,
            Row(Btn("Restore the default profiles", () => { if (Dialogs.Confirm("Vim profiles", "Replace every profile with the defaults?", "Replace", danger: true)) { _s.EditorVimProfiles = VimProfile.Defaults(); _s.EditorVimProfile = "Default"; Save(); Reload(); } })));
    }
}
