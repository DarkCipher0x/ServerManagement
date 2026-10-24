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

// SettingsView: the AI features (switch + lock), Offline mode, Portal browser and Local API cards (moved unchanged from SettingsView.cs in 0.2.0 phase 6½).
public sealed partial class SettingsView
{
    // ---- local API & broadcast -------------------------------------------------------------------------------------------

    /// <summary>Settings → Local API &amp; broadcast (ADR-011): loopback-only listener, capability tokens, active broadcasts.</summary>
    // ---- offline / air-gapped mode (ADR-016) -------------------------------------------------------------------------

    /// <summary>The live settings object: it can be replaced (restore, import) while this tab stays open, so newer cards read
    /// through this rather than the captured <see cref="_s"/>.</summary>
    private static AppSettings S => AppSettings.Current;

    /// <summary>AI features on/off and its lock (0.2.0 phase 5). The one AI-related card there is when AI is off. The switch
    /// applies on the next start; the lock (FeatureLock) guards the switch, "Allow internet AI providers" and itself.</summary>
    private Border AiFeaturesCard()
    {
        bool policy = AiFeatures.Current.Source == AiFeatures.From.Policy;
        var state = new TextBlock { Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        var on = new RadioButton { Content = new TextBlock { Text = "On — load the AI module: engines, the AI Engineer and AI operator, “✦ … with AI” actions, AI report analysis and help answers", TextWrapping = TextWrapping.Wrap }, GroupName = "aifeatures", Margin = new Thickness(0, 2, 0, 2) };
        var off = new RadioButton { Content = new TextBlock { Text = "Off — no AI anywhere: no AI screens, menus, settings, background work or connections (offline digests, rule-based reports and help search still work)", TextWrapping = TextWrapping.Wrap }, GroupName = "aifeatures", Margin = new Thickness(0, 2, 0, 2) };
        var restart = Btn("Restart now", RestartForAiFeatures, "PrimaryButton");
        SettingTag.Set(on, nameof(AppSettings.AiFeatures)); SettingTag.Set(off, nameof(AppSettings.AiFeatures));   // 0.5.0: policy lock / 🛡
        var lockState = new TextBlock { Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap };
        Action refresh = () => { };
        var setLock = Btn("Lock with a password…", () => { SetAiLock(); refresh(); });
        var change = Btn("Change password…", () => { ChangeAiLock(); refresh(); });
        var remove = Btn("Remove lock…", () => { RemoveAiLock(); refresh(); });

        void Refresh()
        {
            var cur = AiFeatures.Current;
            bool setting = _s.AiFeatures == true;
            bool locked = FeatureLock.IsSet;
            state.Text = $"This run: AI features {cur.Describe()} — the AI module {(cur.On ? "is loaded" : "is not loaded")}."
                + (policy ? "\nSet by your administrator (machine policy): it can't be changed here." : "")
                + (cur.Source == AiFeatures.From.Environment ? $"\n{AiFeatures.EnvVar} decides this run; the setting below applies when it isn't set." : "")
                + (AiFeatures.RestartPending ? $"\nAfter a restart: {(setting ? "on" : "off")}." : "");
            on.IsChecked = setting; off.IsChecked = !setting;
            on.IsEnabled = off.IsEnabled = !policy;
            restart.Visibility = AiFeatures.RestartPending ? Visibility.Visible : Visibility.Collapsed;
            lockState.Text = locked && FeatureLock.IsHello
                ? "Locked with Windows Hello: turning AI features on or off, “Allow internet AI providers” and removing the lock need Windows Hello (face, fingerprint or PIN)."
                : locked
                ? "Locked: turning AI features on or off, “Allow internet AI providers” and the lock itself need the password." + (FeatureLock.WaitLeft is { TotalSeconds: > 0 } w ? $" Too many wrong passwords — next try in {Math.Ceiling(w.TotalSeconds):0} s." : "")
                : $"Not locked. A lock (at least {FeatureLock.MinLength} characters) stops anyone else at this PC from changing these settings. There is no way to recover a forgotten password.";
            setLock.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
            remove.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
            change.Visibility = locked && !FeatureLock.IsHello ? Visibility.Visible : Visibility.Collapsed;
        }
        async Task Choose(bool v)
        {
            if ((_s.AiFeatures == true) == v) return;
            if (FeatureLock.Gate(v ? "turn AI features on" : "turn AI features off", AskAiLockPassword) is { } why) { Dialogs.Info("AI features", why); Refresh(); return; }
            // Admin-protected (0.3.0 §1): the setting changes only with admin approval.
            try
            {
                if (await ProtectedSettings.ChangeAsync(v ? "turn AI features on" : "turn AI features off", "ai.features", v))
                    AuditLog.Write("you", "ai-features", v ? "AI features turned on (applies after a restart)" : "AI features turned off (applies after a restart)");
            }
            catch (Exception ex) { Dialogs.Error("AI features", ex.Message); }
            Refresh();
        }
        on.Click += async (_, _) => await Choose(true);
        off.Click += async (_, _) => await Choose(false);
        refresh = Refresh;
        Refresh();
        return Card("AI features",
            "Whether the app has AI at all. Off removes every AI screen, menu item, setting and background task; your AI settings and chats stay in the encrypted store, so turning it back on restores them. Changes apply after a restart. " +
            "Administrators can force it with the machine policy HKLM\\SOFTWARE\\Policies\\DarkCipher Systems\\ServerManagement → AiFeatures (DWORD 0 = off, 1 = on).",
            state, on, off, Row(restart),
            L("LOCK"), lockState, Row(setLock, change, remove));
    }

    /// <summary>The lock's password prompt: one field in the app (never a Windows credential dialog).</summary>
    private static string? AskAiLockPassword() => Dialogs.Prompt("AI features lock", "Password for the AI features lock:", password: true);

    private static void SetAiLock()
    {
        if (FeatureLock.IsSet) return;
        // Windows Hello or a password (0.3.0 §2).
        var how = Dialogs.Choose("Lock AI features", "Lock with Windows Hello (face, fingerprint or PIN: nothing to remember or type) or with a password of its own?",
            new[] { "Windows Hello", "A password", "Cancel" }, cancelIndex: 2);
        if (how == 0)
        {
            if (WindowsHello.Confirm("Lock AI features with Windows Hello") != WindowsHello.Outcome.Verified) { Dialogs.Error("Lock AI features", "Windows Hello didn't confirm (cancelled or not set up for your account). Nothing was changed."); return; }
            FeatureLock.SetHello();
            return;
        }
        if (how != 1) return;
        if (Dialogs.Prompt("Lock AI features", $"Choose a password (at least {FeatureLock.MinLength} characters). It is needed to turn AI features on or off, to allow internet AI providers and to change or remove the lock. It can't be recovered.", password: true) is not { } a) return;
        if (a.Length < FeatureLock.MinLength) { Dialogs.Error("Lock AI features", $"Use at least {FeatureLock.MinLength} characters."); return; }
        if (Dialogs.Prompt("Lock AI features", "Type the same password again:", password: true) is not { } b) return;
        if (a != b) { Dialogs.Error("Lock AI features", "The two passwords differ. Nothing was changed."); return; }
        FeatureLock.Set(a);
    }

    private static void ChangeAiLock()
    {
        if (AskAiLockPassword() is not { } cur) return;
        if (Dialogs.Prompt("Change the lock password", $"New password (at least {FeatureLock.MinLength} characters):", password: true) is not { } a) return;
        if (Dialogs.Prompt("Change the lock password", "Type the new password again:", password: true) is not { } b) return;
        if (a != b) { Dialogs.Error("Change the lock password", "The two new passwords differ. Nothing was changed."); return; }
        var r = FeatureLock.Change(cur, a);
        if (r != FeatureLock.Result.Ok) Dialogs.Error("Change the lock password", LockMessage(r));
    }

    private static void RemoveAiLock()
    {
        if (FeatureLock.IsHello)
        {
            if (FeatureLock.RemoveHello() != FeatureLock.Result.Ok) Dialogs.Error("Remove the lock", "Windows Hello didn't confirm. The lock stays.");
            return;
        }
        if (AskAiLockPassword() is not { } cur) return;
        var r = FeatureLock.Remove(cur);
        if (r != FeatureLock.Result.Ok) Dialogs.Error("Remove the lock", LockMessage(r));
    }

    private static string LockMessage(FeatureLock.Result r) => r switch
    {
        FeatureLock.Result.Wait => $"Too many wrong passwords — try again in {Math.Ceiling(FeatureLock.WaitLeft.TotalSeconds):0} s.",
        FeatureLock.Result.TooShort => $"Use at least {FeatureLock.MinLength} characters.",
        FeatureLock.Result.NotSet => "There is no lock.",
        _ => "Wrong password.",
    };

    /// <summary>Restarts the app so the AI features setting applies (the same relaunch as after a snapshot restore).</summary>
    private static void RestartForAiFeatures()
    {
        if (!Dialogs.Confirm("Restart now", "Restart the app now so the AI features setting applies? Open terminals and sessions close; the workspace reopens them disconnected.", "Restart")) return;
        App.RestartRequested = true;
        MainWindow.Instance.Close();
        if (MainWindow.Instance.IsVisible) App.RestartRequested = false;   // the close was cancelled
    }

    private Border OfflineCard()
    {
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        var blocked = new ListBox { Height = 120, Margin = new Thickness(0, 4, 0, 4) }.Mono();
        var allowed = new TextBox
        {
            Text = string.Join("\n", S.OfflineAllowed), AcceptsReturn = true, Height = 110, Padding = new Thickness(5, 4, 5, 4),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap,
            ToolTip = "One per line: a host name (api.example.com), *.suffix (*.corp.example.com), an IP (203.0.113.10) or a CIDR range (203.0.113.0/24). Lines starting with # are ignored.",
        }.Mono();
        var enable = new CheckBox { Content = new TextBlock { Text = "Offline / air-gapped mode: block every destination outside this PC, your private network and the approved list", TextWrapping = TextWrapping.Wrap }, IsChecked = S.OfflineMode, Margin = new Thickness(0, 8, 0, 0) };
        var resolve = new CheckBox { Content = new TextBlock { Text = "Allow DNS names whose every address is private (the names are looked up with your normal DNS server)", TextWrapping = TextWrapping.Wrap }, IsChecked = S.OfflineResolvePrivateNames, Margin = new Thickness(0, 6, 0, 0) };
        SettingTag.Set(enable, nameof(AppSettings.OfflineMode)); SettingTag.Set(allowed, nameof(AppSettings.OfflineAllowed));
        var addons = new CheckBox { Content = new TextBlock { Text = "Allow add-ons while offline (they are separate programs; the sandbox can't keep them off the network yet)", TextWrapping = TextWrapping.Wrap }, IsChecked = S.OfflineAllowAddons, Margin = new Thickness(0, 4, 0, 0) };
        void Refresh()
        {
            int n = Offline.BlockedTotal;
            state.Text = Offline.On
                ? $"⛔ ON — AI, Vultr, Contabo, infrastructure APIs, SSH, Remote Desktop, web windows, browser links{(S.OfflineAllowAddons ? "" : ", add-ons")} are checked before connecting · {n} blocked this session"
                : "○ Off — the app reaches the destinations you configure (each still marked with its network class).";
            state.SetResourceReference(TextBlock.ForegroundProperty, Offline.On ? "Good" : "Muted");
            var list = Offline.Blocked;
            blocked.ItemsSource = list.Count == 0 ? new List<string> { "nothing blocked this session" } : list.Select(b => $"{b.Time:HH:mm:ss}  {b.Purpose} → {b.Host}: {b.Reason}").ToList();
            enable.IsChecked = S.OfflineMode;
            resolve.IsChecked = S.OfflineResolvePrivateNames;
            addons.IsChecked = S.OfflineAllowAddons;
            if (!allowed.IsKeyboardFocusWithin) allowed.Text = string.Join("\n", S.OfflineAllowed);   // never overwrite what is being typed
        }
        // Static events: only while on screen (see LocalApiCard).
        Action onChange = () => Dispatcher.BeginInvoke(Refresh);
        blocked.Loaded += (_, _) => { Offline.Changed += onChange; Offline.BlockedChanged += onChange; Refresh(); };
        blocked.Unloaded += (_, _) => { Offline.Changed -= onChange; Offline.BlockedChanged -= onChange; };
        // Offline mode, its approved list and add-ons while offline are admin-protected (0.3.0 §1); turning offline mode on
        // or shortening the list applies at once for this session, the administrator makes it stick.
        SettingTag.Set(addons, nameof(AppSettings.OfflineAllowAddons)); SettingTag.Set(resolve, nameof(AppSettings.OfflineResolvePrivateNames));
        enable.Click += async (_, _) =>
        {
            bool want = enable.IsChecked == true;
            enable.IsEnabled = false;
            try { await ProtectedSettings.ChangeAsync(want ? "turn offline mode on" : "turn offline mode off", "net.offline", want); }
            finally { enable.IsEnabled = true; Refresh(); }
        };
        resolve.Click += (_, _) =>
        {
            if (Offline.SetAllowed(S.OfflineAllowed, resolve.IsChecked == true) is { } err) Dialogs.Error("Offline mode", err);
            Refresh();
        };
        addons.Click += async (_, _) =>
        {
            bool want = addons.IsChecked == true;
            addons.IsEnabled = false;
            try { await ProtectedSettings.ChangeAsync(want ? "allow add-ons while offline" : "stop add-ons while offline", "net.offlineAllowAddons", want); }
            finally { addons.IsEnabled = true; Refresh(); }
        };
        var apply = Btn("Save approved list", async () =>
        {
            var list = allowed.Text.Split('\n').Select(e => e.Trim()).Where(e => e.Length > 0 && !e.StartsWith('#')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count > 200) { Dialogs.Error("Offline mode", "At most 200 approved destinations."); return; }
            foreach (var e in list) if (Offline.Validate(e) is { } bad) { Dialogs.Error("Offline mode", $"“{e}”: {bad}"); return; }
            if (await ProtectedSettings.ChangeAsync("change the offline approved list", "net.offlineAllowed", new System.Text.Json.Nodes.JsonArray(list.Select(x => (System.Text.Json.Nodes.JsonNode)x).ToArray())))
                MainWindow.Instance.Status($"Offline mode: {S.OfflineAllowed.Count} approved destination(s) saved.");
            allowed.Text = string.Join("\n", S.OfflineAllowed);
            Refresh();
        }, "PrimaryButton");
        Refresh();
        return Card("Offline mode", "For air-gapped or restricted networks (spec §53). When on, every connection is checked before it opens: this PC, private / LAN addresses and the destinations you approve are allowed; anything else is refused, listed below and written to the audit log. " +
                "Not covered: Windows' and the WebView2 runtime's own update services, what a server reaches by itself, and what an external program (mstsc, ssh.exe, Claude Code) does after it starts.",
            enable, state,
            L("APPROVED DESTINATIONS (one per line: host, *.suffix, IP or CIDR)"), allowed, Row(apply),
            resolve, addons,
            L("BLOCKED THIS SESSION (newest first)"), blocked);
    }

    /// <summary>The portal browser, built in since 0.1.17 (ADR-032 addendum).</summary>
    private Border PortalBrowserCard()
    {
        var addon = Browser.PortalBrowser.Active != null;
        var c = Bound(new CheckBox { Content = new TextBlock { Text = "Portal browser: open router, NAS, hypervisor and provider web UIs as tabs (Portals in the sidebar, 🌐 Open web UI on hosts)", TextWrapping = TextWrapping.Wrap }, IsChecked = _s.PortalBrowserBuiltIn, Margin = new Thickness(0, 4, 0, 0) }, nameof(AppSettings.PortalBrowserBuiltIn));
        c.Click += (_, _) => Browser.PortalBrowser.SetBuiltIn(c.IsChecked == true);
        var note = new TextBlock { Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = "Part of the app; no add-on needed. A browser profile per environment keeps sign-ins apart, self-signed certificates are pinned after you compare them, passwords are never saved, downloads ask first." +
                   (addon ? " The portal browser add-on is also installed: it keeps Portals on even when this is off (disable it on the Add-ons screen)." : "") };
        return Card("Portal browser", "", c, note);
    }

    private Border LocalApiCard()
    {
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        var tokens = new ListBox { Height = 120, Margin = new Thickness(0, 4, 0, 4) };
        var casts = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var port = Bound(new TextBox { Text = _s.LocalApiPort.ToString(), Width = 80, Padding = new Thickness(5, 3, 5, 3) }.Mono(), nameof(AppSettings.LocalApiPort));
        void Refresh()
        {
            var running = Api.LocalServer.Running;
            state.Text = running ? $"● Listening on 127.0.0.1:{Api.LocalServer.Port} (this PC only)" + (_s.LocalApiEnabled ? " · API on" : " · API off — serving broadcasts only")
                : _s.LocalApiEnabled ? "✕ Not listening: " + (Api.LocalServer.LastError ?? (AppLock.IsLocked ? "the app is locked" : "stopped"))
                : "○ Off — nothing is listening.";
            state.SetResourceReference(TextBlock.ForegroundProperty, running ? "Good" : _s.LocalApiEnabled ? "Warn" : "Muted");
            tokens.ItemsSource = _s.ApiTokens.OrderByDescending(t => t.Valid).ThenByDescending(t => t.Created).Select(t => new ListBoxItem
            {
                Tag = t,
                Content = $"{(t.Valid ? "●" : "○")} {t.Name,-20} {t.Prefix}…  {string.Join(",", t.Scopes),-40}  {t.StateText}" + (t.LastUsed is { } lu ? $" · used {t.Uses}× (last {lu:dd MMM HH:mm})" : " · never used"),
            }.Mono()).ToList();
            var all = Api.Broadcasts.All;
            casts.Text = all.Count == 0 ? "No broadcasts. Start one from a terminal's right-click menu (📡 Broadcast to OBS…) or an incident (📡 Broadcast status card…)."
                : string.Join("\n", all.Select(b => $"📡 {b.Kind} “{b.Title}” until {b.Expires:HH:mm}" + (b.LastViewed > DateTime.MinValue ? $" · last fetched {b.LastViewed:HH:mm:ss}" : " · not fetched yet") + (b.Hidden ? " · input hidden" : "")));
            casts.SetResourceReference(TextBlock.ForegroundProperty, all.Count > 0 ? "Bad" : "Muted");
        }
        // Static events: subscribed only while the card is on screen, or every closed Settings tab would stay in memory
        // (found by the closed-view leak check, s23).
        Action onState = () => Dispatcher.BeginInvoke(Refresh);
        tokens.Loaded += (_, _) => { Api.LocalServer.StateChanged += onState; Api.Broadcasts.Changed += onState; };
        tokens.Unloaded += (_, _) => { Api.LocalServer.StateChanged -= onState; Api.Broadcasts.Changed -= onState; };
        tokens.IsVisibleChanged += (_, _) => { if (tokens.IsVisible) Refresh(); };

        var enable = Bound(new CheckBox { Content = new TextBlock { Text = "Enable the local API (scripts on this PC, with a token)", TextWrapping = TextWrapping.Wrap }, IsChecked = _s.LocalApiEnabled, Margin = new Thickness(0, 8, 0, 0) }, nameof(AppSettings.LocalApiEnabled));
        enable.Click += async (_, _) =>
        {
            bool on = enable.IsChecked == true;
            enable.IsChecked = _s.LocalApiEnabled;
            if (on && !Dialogs.Confirm("Enable the local API?", "Programs on THIS PC that hold a token you create can read what that token's scopes allow (hosts without credentials, incidents) and add events/notes. " +
                "Nothing listens on the network: the address is 127.0.0.1 only, browsers are refused, and every write is audited.\n\nTurn it on?", "Enable"))
                return;
            // Admin-protected (0.3.0 §1).
            if (await ProtectedSettings.ChangeAsync(on ? "enable the local API" : "disable the local API", "net.localApi", on))
                AuditLog.Write("you", "api", on ? "local API enabled" : "local API disabled");
            enable.IsChecked = _s.LocalApiEnabled;
            Refresh();
        };
        var applyPort = Btn("Apply port", async () =>
        {
            if (!int.TryParse(port.Text.Trim(), out var p) || p is < 1024 or > 65535) { Dialogs.Error("Local API", "Use a port between 1024 and 65535."); return; }
            await ProtectedSettings.ChangeAsync("change the local API port", "net.localApiPort", p);
            port.Text = _s.LocalApiPort.ToString();
            Refresh();
        });
        Api.ApiToken? Sel() => (tokens.SelectedItem as ListBoxItem)?.Tag as Api.ApiToken;
        var create = Btn("＋ New token…", () => { NewToken(); Refresh(); }, "PrimaryButton");
        var revoke = Btn("Revoke", () => { if (Sel() is { Valid: true } t && Dialogs.Confirm("Revoke token", $"Revoke “{t.Name}”? Programs using it stop working immediately.", "Revoke", danger: true)) { Api.ApiTokens.Revoke(t); Refresh(); } }, "DangerButton");
        var revokeAll = Btn("Revoke all", () => { if (Dialogs.Confirm("Revoke all tokens", "Revoke every API token now?", "Revoke all", danger: true)) { Api.ApiTokens.RevokeAll(); Refresh(); } }, "DangerButton");
        var purge = Btn("Remove revoked/expired", () => { Api.ApiTokens.Purge(); Refresh(); });
        var stopAll = Btn("■ Stop all broadcasts", () => { Api.Broadcasts.StopAll("stopped by you (Settings)"); Refresh(); }, "DangerButton");
        var reference = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 132, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6), Margin = new Thickness(0, 4, 0, 0),
            Text = string.Join("\n", Api.LocalApi.Endpoints.Select(e => $"{e.Method,-5}{e.Path,-40}[{e.Scope}]  {e.Text}")) +
                   $"\n\nPowerShell:  Invoke-RestMethod -Headers @{{Authorization = \"Bearer dcx_…\"}} http://127.0.0.1:{_s.LocalApiPort}/api/v1/status" +
                   $"\ncurl:        curl -H \"Authorization: Bearer dcx_…\" http://127.0.0.1:{_s.LocalApiPort}/api/v1/hosts" +
                   "\nLimits: 10 requests/s per token · 64 KB bodies · browsers (Origin header) refused · 20 failed tokens/min → 60 s lockout.",
        }.Mono();
        Refresh();
        return Card("Local API & broadcast", "A loopback-only door for your own scripts and for OBS. Off by default; never reachable from the network (use an SSH tunnel if you must). " +
                "Tokens are shown once and stored only as a hash; they never give access to credentials, keys, settings, terminals or files.",
            NetUi.Badge(NetClass.Local, "127.0.0.1 only — not reachable from other machines"),
            enable, Row(Labeled("PORT", port), applyPort), state,
            L("TOKENS"), tokens, Row(create, revoke, revokeAll, purge),
            L("BROADCASTS"), casts, Row(stopAll),
            L("ENDPOINTS"), reference);
    }

    private void NewToken()
    {
        var w = Dialogs.MakeWindow("New API token", 560);
        var p = new StackPanel { Margin = new Thickness(18) };
        var name = new TextBox { Padding = new Thickness(5, 3, 5, 3), Text = "monitoring script" };
        p.Children.Add(L("NAME (what uses it)")); p.Children.Add(name);
        p.Children.Add(L("SCOPES — only what the program needs"));
        var boxes = Api.ApiTokens.Scopes.Select(s => new CheckBox { Content = $"{s.Scope} — {s.Text}", Tag = s.Scope, IsChecked = s.Scope == "status.read", Margin = new Thickness(0, 3, 0, 0) }).ToList();
        foreach (var b in boxes) p.Children.Add(b);
        var expiry = new ComboBox { Width = 160, ItemsSource = new[] { "1 day", "7 days", "30 days", "90 days", "never (not recommended)" }, SelectedIndex = 2 };
        p.Children.Add(L("EXPIRES")); p.Children.Add(expiry);
        var ok = new Button { Content = "Create token", Style = (Style)FindResource("PrimaryButton"), IsDefault = true, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => w.DialogResult = true;
        p.Children.Add(ok);
        w.Content = p;
        if (w.ShowDialog() != true) return;
        TimeSpan? life = expiry.SelectedIndex switch { 0 => TimeSpan.FromDays(1), 1 => TimeSpan.FromDays(7), 2 => TimeSpan.FromDays(30), 3 => TimeSpan.FromDays(90), _ => null };
        try
        {
            var (t, secret) = Api.ApiTokens.Create(name.Text, boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag), life);
            var show = Dialogs.MakeWindow("Your new token", 600);
            var sp = new StackPanel { Margin = new Thickness(18) };
            sp.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"“{t.Name}” · {string.Join(", ", t.Scopes)} · {t.StateText}\n\nCopy it now: it is shown ONCE and only a hash is kept. Treat it like a password.", Margin = new Thickness(0, 0, 0, 8) });
            var box = new TextBox { Text = secret, IsReadOnly = true, Padding = new Thickness(5, 4, 5, 4) }.Mono();
            sp.Children.Add(box);
            var copy = new Button { Content = "Copy", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            copy.Click += (_, _) => { Menus.Clip(secret); show.Close(); };
            sp.Children.Add(copy);
            show.Content = sp;
            show.ShowDialog();
        }
        catch (Exception ex) { Dialogs.Error("New token", ex.Message, explain: false); }
    }
}
