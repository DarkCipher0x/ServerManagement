using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;

namespace DcxManager.Views;

// SettingsView: Settings → Windows management (0.6.39, ADR-119). The switch and the rules live in the app, not the
// module, so they can be seen and set while the module is off (and not loaded). Every option is a Group Policy
// (prefix WinTools).
public sealed partial class SettingsView
{
    internal const string WinToolsCategory = "Windows management";

    /// <summary>Windows management built in: on/off, admin-protected, Group Policy <c>WinToolsFeatures</c>.</summary>
    private Border WinToolsFeaturesCard()
    {
        bool policy = WinToolsFeatures.Current.Source == WinToolsFeatures.From.Policy;
        var state = new TextBlock { Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        var on = new RadioButton { Content = new TextBlock { Text = "On — load Windows management: the WINDOWS section in the sidebar, its console and Ctrl+Alt+W", TextWrapping = TextWrapping.Wrap }, GroupName = "wintoolsfeatures", Margin = new Thickness(0, 2, 0, 2) };
        var off = new RadioButton { Content = new TextBlock { Text = "Off — none of its code is loaded: no console, menu or shortcut", TextWrapping = TextWrapping.Wrap }, GroupName = "wintoolsfeatures", Margin = new Thickness(0, 2, 0, 2) };
        SettingTag.Set(on, nameof(AppSettings.WinToolsFeatures)); SettingTag.Set(off, nameof(AppSettings.WinToolsFeatures));
        var restart = Btn("Restart now", RestartForChat, "PrimaryButton");

        void Refresh()
        {
            var cur = WinToolsFeatures.Current;
            state.Text = $"This run: Windows management {cur.Describe()} — the module {(cur.On ? "is loaded" : "is not loaded")}."
                + (policy ? "\nSet by your administrator (machine policy): it can't be changed here." : "")
                + (cur.Source == WinToolsFeatures.From.Environment ? $"\n{WinToolsFeatures.EnvVar} decides this run; the setting below applies when it isn't set." : "")
                + (WinToolsFeatures.RestartPending ? $"\nAfter a restart: {(S.WinToolsFeatures ? "on" : "off")}." : "");
            on.IsChecked = S.WinToolsFeatures; off.IsChecked = !S.WinToolsFeatures;
            on.IsEnabled = off.IsEnabled = !policy;
            restart.Visibility = WinToolsFeatures.RestartPending || ChatFeatures.RestartPending ? Visibility.Visible : Visibility.Collapsed;
        }
        async Task Choose(bool v)
        {
            if (S.WinToolsFeatures == v) return;
            try
            {
                if (await ProtectedSettings.ChangeAsync(v ? "turn Windows management on" : "turn Windows management off", "wintools.features", v))
                    AuditLog.Write("you", "windows tools", v ? "Windows management turned on (applies after a restart)" : "Windows management turned off (applies after a restart)");
            }
            catch (Exception ex) { Dialogs.Error("Windows management", ex.Message); }
            Refresh();
        }
        on.Click += async (_, _) => await Choose(true);
        off.Click += async (_, _) => await Choose(false);
        Refresh();

        return Card("Windows management",
            "An MMC-style console for Windows: services, processes, event logs, storage, users and groups, shares, scheduled tasks, network, drivers, updates and a WMI console that also runs wmic commands — for this PC and other Windows computers. It uses WMI and the event log service directly (no PowerShell, no wmic.exe). " +
            "An administrator can force it: HKLM\\SOFTWARE\\Policies\\DarkCipher Systems\\ServerManagement → WinToolsFeatures (DWORD 0 = off, 1 = on). Every option on this page has a policy of the same name.",
            state, on, off, Row(restart));
    }

    /// <summary>What Windows management may do here: change things, other computers, the console, the Security log, how many events.</summary>
    private Border WinToolsRulesCard()
    {
        var limit = new TextBox { Text = S.WinToolsEventLimit.ToString(), Width = 70 };
        SettingTag.Set(limit, nameof(AppSettings.WinToolsEventLimit));
        limit.LostFocus += (_, _) =>
        {
            if (int.TryParse(limit.Text, out var n) && n is >= 50 and <= 5000) { if (S.WinToolsEventLimit != n) { S.WinToolsEventLimit = n; Save(); } }
            else limit.Text = S.WinToolsEventLimit.ToString();
        };
        return Card("Windows management rules",
            "What the console may do on this PC. Reading is always allowed; every change is confirmed and written to the audit log. Each rule is also a Group Policy; a rule set by policy shows its lock. Changes to the console's rules apply the next time it opens.",
            L("CHANGES"),
            ProtectedCheck("wintools.changes", "Change things: start, stop and restart services and set their start-up type, end processes, enable or disable local accounts, run, enable or disable scheduled tasks (off = look only)"),
            L("OTHER COMPUTERS"),
            Check("Manage other Windows computers (WMI and event logs, with the host's saved credential or your Windows sign-in)", _s.WinToolsAllowRemote, v => _s.WinToolsAllowRemote = v),
            L("CONSOLE AND LOGS"),
            Check("The WMI console (WQL and wmic-style commands; it only reads)", _s.WinToolsAllowConsole, v => _s.WinToolsAllowConsole = v),
            Check("The Security event log (needs administrator rights on the computer)", _s.WinToolsAllowSecurityLog, v => _s.WinToolsAllowSecurityLog = v),
            Row(Labeled("Most events read per event log view (50–5000)", limit)));
    }
}
