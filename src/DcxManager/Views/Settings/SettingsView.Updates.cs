using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Services;
using DcxManager.Sessions;
using DcxManager.Ssh;
using DcxManager.Vultr;
using Microsoft.Win32;

namespace DcxManager.Views;

// SettingsView: the Updates card (moved unchanged from SettingsView.cs in 0.2.0 phase 6½; 0.6.1: GitHub releases and the
// release key, ADR-079).
public sealed partial class SettingsView
{
    // ---- updates -----------------------------------------------------------------------------------------------------

    /// <summary>Settings → Updates (ADR-008, ADR-079): where to look (GitHub releases or an address), check, verified
    /// download, backup prompt, install; the opt-in automatic check (daily / weekly) and background download.</summary>
    private Border Updates()
    {
        if (!Updater.Enabled)
            return Card("Updates", $"Installed version {App.DisplayVersion}.",
                new TextBlock { Text = "⏸ " + Updater.DisabledMessage, TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
                    Text = Packaging.IsPackaged ? "Nothing here contacts the internet. Windows verifies the Store's signature on every update."
                        : "Nothing here contacts the internet.",
                });
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        var details = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        var notes = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6) };
        var pkg = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) }.Mono();
        var verify = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        var progress = new ProgressBar { Height = 6, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed, Maximum = 1 };
        var download = Btn("⇩ Download and verify", () => { }, "PrimaryButton");
        var install = Btn("Install… (backup first)", () => { }, "PrimaryButton");
        install.IsEnabled = false;
        details.Children.Add(L("RELEASE INFORMATION")); details.Children.Add(notes);
        details.Children.Add(L("PACKAGE")); details.Children.Add(pkg);
        details.Children.Add(Row(download, install)); details.Children.Add(progress); details.Children.Add(verify);
        UpdateInfo? info = null; string? verifiedPath = null;

        void Show(UpdateInfo i)
        {
            info = i;
            notes.Text = (i.Released.Length > 0 ? $"Released {i.Released}\n\n" : "") + (i.Notes.Length > 0 ? i.Notes : "(no release notes)") + (i.SchemaChange ? "\n\n⚠ This release changes the data format: a backup before upgrading is strongly recommended." : "");
            pkg.Text = $"{i.FileName} · {i.Bytes / 1048576.0:0.0} MB\nSHA-256 {i.Sha256}\n{i.Url}" + (i.ManifestSigned ? "\n✓ signed by the DarkCipher release key" : "");
            details.Visibility = Visibility.Visible;
        }

        var check = Btn("Check for updates", async () =>
        {
            result.Text = "checking…"; details.Visibility = Visibility.Collapsed; info = null; verifiedPath = null; install.IsEnabled = false; verify.Text = "";
            var (i, msg) = await Updater.CheckAsync(new CancellationTokenSource(TimeSpan.FromSeconds(40)).Token);
            _s.LastUpdateCheck = DateTime.Now; Save();
            result.Text = msg;
            if (i != null) Show(i);
        }, "PrimaryButton");

        // Downloaded and verified in the background by the automatic check (you allowed it): ready to install now.
        if (Updater.Ready is { } ready && File.Exists(ready.Path) && ready.Info.Version > Updater.Installed)
        {
            Show(ready.Info);
            verifiedPath = ready.Path;
            result.Text = $"Version {ready.Info.Version} was downloaded and verified in the background.";
            verify.Text = "✓ Verified: ready to install."; verify.SetResourceReference(TextBlock.ForegroundProperty, "Good");
            install.IsEnabled = true;
        }

        download.Click += async (_, _) =>
        {
            if (info is not { } i) return;
            download.IsEnabled = false; progress.Visibility = Visibility.Visible; progress.Value = 0; verify.Text = "downloading into the app's private temp folder…";
            try
            {
                var (path, v) = await Updater.DownloadAndVerifyAsync(i, new Progress<double>(p => progress.Value = p), CancellationToken.None);
                verify.Text = (v.Ok ? "✓ " : "✕ ") + v.Message + (v.Verdict is UpdateVerdict.NotSigned or UpdateVerdict.UntrustedPublisher
                    ? "\nNothing was installed: this release's update information isn't signed by the DarkCipher release key." : "");
                verify.SetResourceReference(TextBlock.ForegroundProperty, v.Ok ? "Good" : "Warn");
                verifiedPath = path;
                install.IsEnabled = v.Ok && path != null;
                AuditLog.Write("you", "app", "update downloaded", $"{i.Version} · {v.Verdict}");
            }
            finally { download.IsEnabled = true; progress.Visibility = Visibility.Collapsed; }
        };

        install.Click += (_, _) =>
        {
            if (info is not { } i || verifiedPath == null) return;
            // Re-verify the file right before running it (nothing may have changed since the check).
            var again = Updater.Verify(verifiedPath, i);
            if (!again.Ok) { Dialogs.Error("Update", "The downloaded installer no longer verifies: " + again.Message, explain: false); install.IsEnabled = false; return; }
            var choice = Dialogs.Choose("Back up before upgrading?",
                "Would you like to back up your configuration before upgrading?" + (i.SchemaChange ? "\n\n⚠ This release changes the data format — a backup is strongly recommended." : "\n\nUpgrades keep your encrypted data where it is, but a verified backup is the safe way to be sure."),
                new[] { "Back up and continue", "Continue without backup", "Cancel" }, cancelIndex: 2);
            if (choice is 2 or -1) return;
            if (choice == 0 && !BackupDialogs.ExportFull(Window.GetWindow(this), $"Backup before upgrading to {i.Version}."))
            { Dialogs.Info("Update", "The backup wasn't completed, so the update was not started."); return; }
            var how = i.IsSetup ? "the setup program will update" : "Windows Installer will upgrade";
            if (!Dialogs.Confirm("Install update", $"The app will close and {how} {App.Version} → {i.Version} (you may see a UAC prompt). Open sessions end. Your encrypted data stays where it is.\n\nAfter the restart the app checks that {i.Version} is really running.", "Close and install")) return;
            var snap = Snapshots.Create("pre-upgrade");
            if (!snap.Ok && !Dialogs.Confirm("Install update", "The local safety snapshot failed: " + snap.Message + "\n\nInstall anyway?", "Install anyway")) return;
            try { Updater.LaunchInstaller(verifiedPath, i); }
            catch (Exception ex) { Dialogs.Error("Update", "The update wasn't started: " + ex.Message, explain: false); return; }
            if (MainWindow.Instance is { } mw) { mw.ExitWithoutPrompt = true; mw.Close(); }
        };

        // Where to look: GitHub releases (default) or an https address of your own (empty = the website).
        var endpoint = new TextBox { Text = Updater.CustomEndpoint, Width = 460, Padding = new Thickness(5, 4, 5, 4) }.Mono();
        var setEndpoint = Btn("Save address", () =>
        {
            var v = endpoint.Text.Trim();
            if (Updater.ValidateEndpoint(v) is { } bad) { Dialogs.Error("Updates", bad); return; }
            _s.UpdateEndpoint = v == Updater.DefaultEndpoint ? "" : v; Save();
            AuditLog.Write("you", "app", "update address changed", v);
        });
        var resetEndpoint = Btn("Website address", () => { _s.UpdateEndpoint = ""; endpoint.Text = Updater.DefaultEndpoint; Save(); });
        var endpointRow = Row(endpoint, setEndpoint, resetEndpoint);
        endpointRow.Visibility = Updater.FromGitHub ? Visibility.Collapsed : Visibility.Visible;
        // A private repository (0.6.2): your own read-only token, encrypted with the settings, sent only to api.github.com.
        var tokenBox = new PasswordBox { Width = 320, Padding = new Thickness(5, 4, 5, 4) };
        var tokenState = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        void ShowTokenState() => tokenState.Text = _s.UpdateGitHubToken.Length > 0 ? "✓ a token is saved (encrypted)" : "none: the repository must be public";
        var saveToken = Btn("Save token", () =>
        {
            var t = tokenBox.Password.Trim();
            if (t.Length == 0) return;
            if (t.Length > 400 || t.Any(char.IsWhiteSpace)) { Dialogs.Error("Updates", "That doesn't look like a GitHub access token."); return; }
            _s.UpdateGitHubToken = t; Save(); tokenBox.Clear(); ShowTokenState();
            AuditLog.Write("you", "app", "update access token saved");
        });
        var clearToken = Btn("Remove", () => { _s.UpdateGitHubToken = ""; Save(); tokenBox.Clear(); ShowTokenState(); AuditLog.Write("you", "app", "update access token removed"); });
        ShowTokenState();
        var tokenPanel = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                L("GITHUB ACCESS TOKEN (ONLY WHILE THE REPOSITORY IS PRIVATE)"),
                Row(tokenBox, saveToken, clearToken, tokenState),
                new TextBlock { Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
                    Text = $"A fine-grained token with read-only Contents access to {Updater.GitHubRepo} only. Stored encrypted with your settings, never in a backup without secrets; sent only to api.github.com. Updates are still installed only when the DarkCipher release key's signature verifies." },
            },
            Visibility = Updater.FromGitHub ? Visibility.Visible : Visibility.Collapsed,
        };
        var source = Choice(new[] { ("github", $"GitHub releases (github.com/{Updater.GitHubRepo})"), ("url", "An https address of my own (or the website)") }, _s.UpdateSource, v =>
        {
            _s.UpdateSource = v; Save();
            endpointRow.Visibility = v == "url" ? Visibility.Visible : Visibility.Collapsed;
            tokenPanel.Visibility = v == "url" ? Visibility.Collapsed : Visibility.Visible;
            AuditLog.Write("you", "app", "update source changed", v);
        }, "Where Check for updates looks for a new version");

        var auto = Bound(new CheckBox { Content = "Check automatically (tells you in the status bar; never installs)", IsChecked = _s.AutoCheckUpdates, Margin = new Thickness(0, 4, 0, 0) }, nameof(AppSettings.AutoCheckUpdates));
        var interval = Choice(new[] { ("weekly", "Once a week"), ("daily", "Once a day") }, _s.UpdateCheckInterval, v => { _s.UpdateCheckInterval = v; Save(); AuditLog.Write("you", "app", "update check interval changed", v); },
            "How often the automatic check runs (shortly after the app starts, when that long has passed)");
        var autoDl = Bound(new CheckBox { Content = "Also download and verify a new version in the background (installing always asks you first)", IsChecked = _s.AutoDownloadUpdates, Margin = new Thickness(0, 6, 0, 0) }, nameof(AppSettings.AutoDownloadUpdates));
        auto.Click += (_, _) =>
        {
            if (auto.IsChecked == true && !NetUi.ConfirmEnable("The automatic update check", Updater.Endpoint)) { auto.IsChecked = false; return; }
            _s.AutoCheckUpdates = auto.IsChecked == true; Save();
            AuditLog.Write("you", "app", _s.AutoCheckUpdates ? "automatic update check enabled" : "automatic update check disabled");
        };
        autoDl.Click += (_, _) =>
        {
            _s.AutoDownloadUpdates = autoDl.IsChecked == true; Save();
            AuditLog.Write("you", "app", _s.AutoDownloadUpdates ? "background update download enabled" : "background update download disabled");
        };

        var trustNote = new TextBlock
        {
            Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            Text = $"Trust: an update is installed only when its update information is signed by the DarkCipher release key pinned in this app (ECDSA P-256, {Updater.ReleaseKeyX[..8]}…{Updater.ReleaseKeyY[^8..]})" +
                   (Updater.TrustedPublisherThumbprints.Length > 0 ? $" or the installer carries a pinned DarkCipher Systems code-signing certificate ({Updater.TrustedPublisherThumbprints.Length} pinned)" : "") +
                   ". GitHub or any other server only carries the files: nothing it changes can pass. Older versions are never offered.",
        };
        return Card("Updates", $"Installed version {App.DisplayVersion}. Checks only happen when you click, or on the schedule you choose below. A check is a plain download of the update information (and its signature) — no identifiers, hosts, notes or settings are sent.",
            NetUi.External("Internet access", "“Check for updates” contacts the source below; the installer comes from the same place (GitHub: github.com, api.github.com and its download host only).", new StackPanel { Children = { Labeled("UPDATES FROM", source), endpointRow, tokenPanel } }),
            Row(check), result, details,
            NetUi.External("Automatic external communication", "Off by default. When on, the app checks the source above at most once per interval, shortly after it starts, and shows a message in the status bar. Installing always asks you.",
                new StackPanel { Children = { auto, Labeled("HOW OFTEN", interval), autoDl } }),
            trustNote);
    }
}
