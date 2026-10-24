using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;

namespace DcxManager.Views;

// SettingsView: the Admin protection card (0.3.0 §1, ADR-039) — whether this user's security settings are protected,
// what is covered, who changed what, and "Protect them now".
public sealed partial class SettingsView
{
    private Border ProtectionCard()
    {
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        var list = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Caption") };
        var history = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Caption") };
        Button protect = null!;
        protect = AdminApproval.Button("🛡 Protect these settings now", async () =>
        {
            // Asks for the current values as they are: the first request carries every protected setting.
            if (await ProtectedSettings.ChangeAsync("protect the security settings", new Dictionary<string, System.Text.Json.Nodes.JsonNode?>()))
                MainWindow.Instance?.Status("Security settings are now protected: changing them needs admin approval.");
            Refresh();
        });

        void Refresh()
        {
            var r = ProtectedSettings.Current;
            if (ProtectedSettings.Problem is { } p && r == null)
            {
                state.Text = "⚠ Not protected: the protected record was ignored — " + p + ". Until an administrator fixes it, your own settings apply.";
                state.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
            }
            else if (r != null)
            {
                state.Text = $"🛡 Protected — these settings change only with admin approval (UAC). Stored in {ProtectedSettings.Store.Describe} for your Windows user."
                    + (ProtectedSettings.Problem is { } p2 ? $"\n⚠ {p2}" : "");
                state.SetResourceReference(TextBlock.ForegroundProperty, "Good");
            }
            else
            {
                state.Text = AdminGate.CanElevate
                    ? "○ Not protected yet: anything running as you could change these settings. Protect them once (one UAC prompt); after that every change needs admin approval."
                    : "○ Not protected: this copy isn't the installed app (Program Files), so it can't ask Windows for admin approval. Install the MSI to protect these settings.";
                state.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
            }
            protect.Visibility = r == null && AdminGate.CanElevate ? Visibility.Visible : Visibility.Collapsed;
            list.Text = string.Join("\n", ProtectedSettings.Items.GroupBy(i => i.Group).Select(g =>
                g.Key + ": " + string.Join(" · ", g.Select(i => $"{i.Title} ({i.Show(i.Get(_s))}{(ProtectedSettings.IsProtected(i.Key) ? "" : ", not protected")})"))));
            static string Names(string keys)
            {
                var k = keys.Split(", ", StringSplitOptions.RemoveEmptyEntries);
                if (k.Length >= ProtectedSettings.Items.Count) return "protected every security setting";
                return string.Join(", ", k.Select(x => ProtectedSettings.Find(x)?.Title ?? x));
            }
            history.Text = r == null || r.History.Count == 0 ? "No changes yet."
                : string.Join("\n", r.History.Take(5).Select(h => $"{h.Utc.ToLocalTime():dd MMM yyyy HH:mm} · approved by {AppIdentity.Show(h.By)} · {Names(h.Keys)}"));
        }
        Action<IReadOnlyCollection<string>> onChange = _ => Dispatcher.BeginInvoke(Refresh);
        state.Loaded += (_, _) => { AdminGate.Changed += onChange; Refresh(); };
        state.Unloaded += (_, _) => AdminGate.Changed -= onChange;
        Refresh();
        return Card("Admin protection",
            "Security settings — AI features and internet AI providers, offline mode, Telnet, the local API, the app lock, Windows Hello — are kept where only administrators can write. " +
            "Changing one asks Windows for admin approval (UAC); a second window then shows exactly what changes before anything is written. " +
            "Making something stricter (offline mode on, Telnet off) works at once for this session. Passwords and keys are never stored there.",
            state, Row(protect),
            L("WHAT IS PROTECTED"), list,
            L("RECENT CHANGES"), history);
    }

    /// <summary>Settings set by Group Policy (0.3.0 §10, ADR-040): each one, its value and where it comes from, plus any
    /// policy value that had to be ignored.</summary>
    private Border PolicyCard()
    {
        var values = PolicySettings.Values.OrderBy(v => v.Entry.Category).ThenBy(v => v.Entry.Title).ToList();
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        state.Text = values.Count == 0
            ? $"○ No setting is managed by policy on this PC. Administrators can set any of {PolicySettings.Catalog.Count} settings with Group Policy, Intune or the registry; the ADMX templates are in the release's policy folder."
            : $"🏢 {values.Count} setting(s) set by your organisation. They can't be changed here; a change to one is put back and the status bar says why.";
        state.SetResourceReference(TextBlock.ForegroundProperty, values.Count == 0 ? "Muted" : "Info");
        var list = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Caption"),
            Text = string.Join("\n", values.GroupBy(v => v.Entry.Category).Select(g => g.Key + ": " + string.Join(" · ", g.Select(v => $"{v.Entry.Title} = {v.Shown} ({v.Scope})")))),
        };
        var problems = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Caption"), Text = string.Join("\n", PolicySettings.Problems.Select(p => "⚠ " + p)) };
        problems.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
        var parts = new List<UIElement> { state };
        if (values.Count > 0) { parts.Add(L("MANAGED SETTINGS")); parts.Add(list); }
        if (PolicySettings.Problems.Count > 0) { parts.Add(L("IGNORED POLICY VALUES")); parts.Add(problems); }
        return Card("Managed by your organisation",
            $"Policies live in HKLM\\{PolicySettings.Key} (this computer) and HKCU\\{PolicySettings.Key} (you); the computer's value wins, and both win over admin protection and your own settings. Value names are the settings' names (docs: POLICIES.md).",
            parts.ToArray());
    }
}
