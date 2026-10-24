using System.IO;
using System.Windows;
using System.Windows.Controls;
using DcxManager.Import;
using DcxManager.Services;
using Microsoft.Win32;

namespace DcxManager.Views;

/// <summary>
/// Settings → Data & backups → Import from another app (and Hosts → ⇩ Import): pick the app, read its file or this PC's
/// saved sessions, see every connection as a tick under its folder, choose where they go and whether saved passwords come
/// along, then import (a snapshot first). 0.5.0, ADR-061.
/// </summary>
public static class ConnectionImportDialog
{
    public static void Show(Window? owner, string? intoFolder = null)
    {
        if (BackupPolicy.ImportBlocked() is { } no) { Dialogs.Error("Import", no); return; }
        if (TestMode.Running) { Dialogs.RefusedInSelfTest("connection import dialog"); return; }
        var w = Dialogs.MakeWindow("Import connections from another app", 760, 680);
        w.Owner = owner;
        var root = new DockPanel { Margin = new Thickness(18) };
        var sources = new ListBox { Width = 230, ItemsSource = ConnectionImport.All, DisplayMemberPath = nameof(IConnectionSource.Title), Margin = new Thickness(0, 0, 14, 0) };
        DockPanel.SetDock(sources, Dock.Left); root.Children.Add(sources);

        var right = new DockPanel();
        var help = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Hint"], Margin = new Thickness(0, 0, 0, 8) };
        var pickFile = new Button { Content = "Choose file…", Style = (Style)Application.Current.Resources["PrimaryButton"], Margin = new Thickness(0, 0, 8, 0) };
        var fromPc = new Button { Content = "Read from this PC" };
        var top = new StackPanel();
        top.Children.Add(help);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        buttons.Children.Add(pickFile); buttons.Children.Add(fromPc);
        top.Children.Add(buttons);
        DockPanel.SetDock(top, Dock.Top); right.Children.Add(top);

        var bottom = new StackPanel();
        var warnings = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxHeight = 90, Margin = new Thickness(0, 6, 0, 6) };
        warnings.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
        var folder = new TextBox { Text = intoFolder ?? "Imported" };
        var withPasswords = new CheckBox { Content = "Import saved passwords into the encrypted vault", IsChecked = true, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        var advice = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Hint"], Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed,
            Text = "The file you picked holds passwords readable with that app's default key. Once imported here (encrypted), consider deleting it or protecting it with a master password in that app." };
        var go = new Button { Content = "Import", Style = (Style)Application.Current.Resources["PrimaryButton"], IsEnabled = false, IsDefault = true };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        bar.Children.Add(go); bar.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) });
        bottom.Children.Add(warnings);
        bottom.Children.Add(new TextBlock { Text = "PUT THEM IN FOLDER (their own folders go below it)", Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 6, 0, 4) });
        bottom.Children.Add(folder); bottom.Children.Add(withPasswords); bottom.Children.Add(advice); bottom.Children.Add(bar);
        DockPanel.SetDock(bottom, Dock.Bottom); right.Children.Add(bottom);

        var tree = new TreeView { MinHeight = 200 };
        var summary = new TextBlock { Margin = new Thickness(0, 4, 0, 4) };
        DockPanel.SetDock(summary, Dock.Top); right.Children.Add(summary);
        right.Children.Add(tree);
        root.Children.Add(right);
        w.Content = root;

        ImportResult? result = null;
        var ticks = new List<(CheckBox Box, ImportedHost Host)>();
        IConnectionSource? Source() => sources.SelectedItem as IConnectionSource;
        void ShowSource()
        {
            var s = Source();
            help.Text = s?.Help ?? "Pick the app you're coming from.";
            pickFile.Visibility = s is { Filter.Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            fromPc.Visibility = s?.FromThisPc == true ? Visibility.Visible : Visibility.Collapsed;
        }
        void UpdateGo() { int n = ticks.Count(t => t.Box.IsChecked == true); go.IsEnabled = n > 0; go.Content = n > 0 ? $"Import {n}" : "Import"; }
        void Show(ImportResult r)
        {
            result = r; ticks.Clear(); tree.Items.Clear();
            var nodes = new Dictionary<string, TreeViewItem>();
            TreeViewItem FolderNode(string path)
            {
                if (path.Length == 0) return null!;
                if (nodes.TryGetValue(path, out var n)) return n;
                var parentPath = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
                var box = new CheckBox { Content = "📁 " + path[(path.LastIndexOf('/') + 1)..], IsChecked = true };
                n = new TreeViewItem { Header = box, IsExpanded = true, Tag = path };
                box.Click += (_, _) => { foreach (var t in ticks.Where(t => t.Host.Folder == path || t.Host.Folder.StartsWith(path + "/"))) t.Box.IsChecked = box.IsChecked; foreach (var f in nodes.Where(kv => kv.Key.StartsWith(path + "/"))) ((CheckBox)f.Value.Header).IsChecked = box.IsChecked; UpdateGo(); };
                if (parentPath.Length == 0) tree.Items.Add(n); else FolderNode(parentPath).Items.Add(n);
                nodes[path] = n;
                return n;
            }
            foreach (var h in r.Hosts.OrderBy(h => h.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase))
            {
                var box = new CheckBox { Content = $"{(h.Protocol == "rdp" ? "🖥" : "⌨")} {h.Name}  ·  {h.Protocol} {(h.Username.Length > 0 ? h.Username + "@" : "")}{h.Address}:{h.Port}{(h.Password != null ? "  ·  🔑 password" : h.KeyFile != null ? "  ·  ⚿ key file" : "")}", IsChecked = true };
                box.Click += (_, _) => UpdateGo();
                ticks.Add((box, h));
                var item = new TreeViewItem { Header = box };
                if (h.Folder.Length == 0) tree.Items.Add(item); else FolderNode(h.Folder).Items.Add(item);
            }
            summary.Text = $"{r.Hosts.Count} connection(s) from {r.Source}" + (r.Warnings.Count > 0 ? $", {r.Warnings.Count} note(s) below" : "") + ". Untick what you don't want.";
            warnings.Text = string.Join("\n", r.Warnings.Take(30)) + (r.Warnings.Count > 30 ? $"\n… and {r.Warnings.Count - 30} more" : "");
            withPasswords.Visibility = advice.Visibility = r.HasPasswords ? Visibility.Visible : Visibility.Collapsed;
            if (folder.Text is "Imported" or "") folder.Text = intoFolder ?? "Imported/" + ConnectionImport.CleanFolder(r.Source.Replace('/', ' '));
            UpdateGo();
        }
        void ReadWith(Func<string?, ImportResult> read, string? master = null)
        {
            try { Show(read(master)); }
            catch (MasterPasswordNeededException ex)
            {
                var pw = Dialogs.Prompt("Master password", ex.Message + "\n\nThe master password (used only to read the file, never stored):", password: true);
                if (pw != null) ReadWith(read, pw);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException or FormatException or System.Security.SecurityException)
            { Dialogs.Error("Import", "That file can't be read: " + ex.Message); }
        }
        sources.SelectionChanged += (_, _) => ShowSource();
        pickFile.Click += (_, _) =>
        {
            if (Source() is not { } s) return;
            var dlg = new OpenFileDialog { Filter = s.Filter, Multiselect = s.MultipleFiles, Title = "Import from " + s.Title };
            if (dlg.ShowDialog(w) != true) return;
            ReadWith(master =>
            {
                var all = new ImportResult { Source = s.Title };
                foreach (var f in dlg.FileNames)
                {
                    var one = s.Read(f, master);
                    all.Hosts.AddRange(one.Hosts); all.Warnings.AddRange(one.Warnings);
                }
                return all;
            });
        };
        fromPc.Click += (_, _) => { if (Source() is { } s) ReadWith(m => s.Read(null, m)); };
        go.Click += (_, _) =>
        {
            if (result == null) return;
            var picked = ticks.Where(t => t.Box.IsChecked == true).Select(t => t.Host).ToList();
            var snap = Snapshots.Create("pre-import");
            if (!snap.Ok && !Dialogs.Confirm("Import", "The safety snapshot of your current data failed:\n" + snap.Message + "\n\nImport anyway, without a way back?", "Import anyway", danger: true)) return;
            try
            {
                var o = ConnectionImportApply.Apply(picked, result.Source, folder.Text, withPasswords.IsChecked == true);
                Dialogs.Info("Import", $"{o.Added} host(s) added to “{ConnectionImport.CleanFolder(folder.Text)}”, {o.Skipped} already here.\n{o.Passwords} password(s) and {o.Keys} key(s) went into the encrypted vault." +
                    (o.Notes.Count > 0 ? "\n\n" + string.Join("\n", o.Notes.Take(10)) : "") + "\n\nA snapshot from just before is in Settings → Snapshots.");
                w.DialogResult = true;
            }
            catch (Exception ex) { Dialogs.Error("Import", ex.Message); }
        };
        sources.SelectedIndex = 0;
        ShowSource();
        w.ShowDialog();
    }
}
