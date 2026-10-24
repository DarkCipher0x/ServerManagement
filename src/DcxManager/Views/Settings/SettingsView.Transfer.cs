using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DcxManager.Services;
using Microsoft.Win32;

namespace DcxManager.Views;

// SettingsView: the Import & export tab and the About card (0.2.1, the DarkCipher house theme's layout).
public sealed partial class SettingsView
{
    private Border TransferCard()
    {
        var result = new TextBlock { Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 8, 0, 0) };
        void Export()
        {
            var dlg = new SaveFileDialog { Title = "Export appearance", FileName = "darkcipher-appearance.json", Filter = "Appearance settings (*.json)|*.json" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, AppearanceTransfer.Export(_s), new System.Text.UTF8Encoding(false));
                AuditLog.Write("you", "settings", "appearance exported", "appearance and navigation only, unencrypted (no secrets)");
                result.Text = $"✓ Exported to {Path.GetFileName(dlg.FileName)} (not encrypted: it holds no secrets).";
            }
            catch (Exception ex) { Dialogs.Error("Export appearance", ex.Message); }
        }
        void Import()
        {
            var dlg = new OpenFileDialog { Title = "Import appearance", Filter = "Appearance settings (*.json)|*.json|All files|*.*" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                if (new FileInfo(dlg.FileName).Length > AppearanceTransfer.MaxBytes) throw new InvalidDataException("the file is larger than an appearance export can be");
                var plan = AppearanceTransfer.Read(File.ReadAllText(dlg.FileName), _s, MainWindow.Instance?.AllNavKeys, MainWindow.NavSections);
                if (plan.Changes.Count == 0) { Dialogs.Info("Import appearance", AppearanceTransfer.Describe(plan)); return; }
                if (!Dialogs.Confirm("Import appearance", AppearanceTransfer.Describe(plan) + "\n\nApply these?", "Apply")) return;
                ApplyImport(plan);
                Current?.RevealCategory("Import & export");
            }
            catch (InvalidDataException ex) { Dialogs.Error("Import appearance", "That file can't be imported: " + ex.Message + "."); }
            catch (Exception ex) { Dialogs.Error("Import appearance", ex.Message); }
        }
        // 0.4.0 (DarkCipher0x): the encrypted, pick-what-you-want export lives here too, as the first thing on the tab — it was only
        // reachable from Data & backups and the Hosts menu before. Everything in it is a tick (hosts by folder, credentials,
        // cloud keys, notes, incidents, reports…) and the file is PBKDF2 + AES-256-GCM under a password you choose.
        return Card("Import & export",
            "Data: an encrypted export of exactly what you tick — hosts by folder, credentials, cloud API keys, notes, incidents, reports, settings — as a password-protected .dcxbackup (PBKDF2-SHA256, AES-256-GCM), and the matching import with its own tick tree. " +
            "Appearance: the theme, accent, density, fonts, sidebar and navigation as a small JSON file that never holds secrets. Imports are validated and previewed before anything changes.",
            L("DATA (encrypted; pick what to include)"),
            Row(Btn("Export data…", () => { if (BackupDialogs.ExportFull(Window.GetWindow(this))) result.Text = "✓ Encrypted export written and read back."; }, "PrimaryButton"),
                Btn("Import data…", () => BackupDialogs.Import(Window.GetWindow(this))), Btn("Import from another app…", () => ConnectionImportDialog.Show(Window.GetWindow(this)))),
            L("APPEARANCE AND NAVIGATION (plain JSON, no secrets)"),
            Row(Btn("Export appearance…", Export), Btn("Import appearance…", Import)),
            result,
            Row(Btn("Reset all appearance and navigation…", () =>
            {
                if (!Dialogs.Confirm("Reset appearance and navigation", "Reset the theme, layout and text, typography, the sidebar and navigation to the defaults?\n\nAccounts, keys, terminal settings and everything else stay as they are.", "Reset", danger: true)) return;
                foreach (var card in new[] { "Theme", "Layout and text", "Typography", "Sidebar", "Navigation & explorers" }) ResetValues(card);
                Save(); AuditLog.Write("you", "settings", "appearance and navigation reset to defaults"); ApplyLook();
            }, "DangerButton")));
    }

    /// <summary>Applies an import plan, saves, repaints and records it (self-test seam: no dialog).</summary>
    internal void ApplyImport(AppearanceTransfer.Plan plan)
    {
        AppearanceTransfer.Apply(plan, _s);
        Save();
        AuditLog.Write("you", "settings", "appearance imported", $"{plan.Changes.Count} setting(s): {string.Join(", ", plan.Changes.Select(c => c.Name))}");
        ApplyLook();
    }

    /// <summary>The defaults of one resettable card, without asking or repainting (Reset all).</summary>
    private void ResetValues(string title)
    {
        var d = new AppSettings();
        switch (title)
        {
            case "Theme": _s.Theme = d.Theme; _s.ThemePreset = d.ThemePreset; _s.AccentColor = d.AccentColor; _s.StatusIntensity = d.StatusIntensity; _s.FollowWindowsHighContrast = d.FollowWindowsHighContrast; break;
            case "Layout and text": _s.Density = d.Density; _s.UiScale = d.UiScale; _s.UiTextSize = d.UiTextSize; _s.Corners = d.Corners; _s.Motion = d.Motion; break;
            case "Typography": _s.UiFont = d.UiFont; _s.MonoFont = d.MonoFont; _s.EditorFont = d.EditorFont; _s.TerminalFont = d.TerminalFont; _s.MonoAccents = d.MonoAccents; break;
            case "Sidebar": _s.SidebarStyle = d.SidebarStyle; _s.SidebarWidth = d.SidebarWidth; _s.LandingPage = d.LandingPage; _s.PinnedNavItems = new(); break;
            case "Navigation & explorers": _s.NavSectionOrder = new(); _s.NavItemOrder = new(); _s.HiddenNavSections = new(); _s.HiddenNavItems = new(); _s.RememberNavExpansion = d.RememberNavExpansion; _s.ExpandedNavSections = new(); break;
        }
    }

    /// <summary>"ServerManagement v0.5.0" (DarkCipher0x, 0.5.0: the company line underneath already names DarkCipher Systems); a
    /// product name set by branding or a theme add-on is shown as it is.</summary>
    internal static string AboutTitle(string productName, string version) =>
        (productName == AppInfo.ProductName ? "ServerManagement" : productName) + " v" + version;

    private Border AboutCard()
    {
        var row = new DockPanel();
        var icon = new Image { Width = 96, Height = 96, Margin = new Thickness(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Top };
        try { icon.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")); } catch { icon.Visibility = Visibility.Collapsed; }
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        System.Windows.Automation.AutomationProperties.SetName(icon, $"{AppInfo.ProductName} icon");
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = AboutTitle(ThemeManager.ProductName, App.DisplayVersion), Style = (Style)FindResource("PageTitle") });
        text.Children.Add(new TextBlock { Text = "DarkCipher Systems · built by DarkCipher0x", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 2, 0, 8) });
        text.Children.Add(new TextBlock { Text = "Interface: Segoe UI Variable Text · code and consoles: Cascadia Mono · icons: Segoe Fluent Icons (all ship with Windows; nothing is bundled).", Style = (Style)FindResource("Caption") });
        text.Children.Add(new TextBlock { Text = "Unsigned build: verify downloads against the release's SHA256SUMS file. A software bill of materials (SBOM) ships with every release.", Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 4, 0, 0) });
        row.Children.Add(text);
        return Card("About", "", row);
    }
}
