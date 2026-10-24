using System.Windows;
using System.Windows.Controls;
using DcxManager.Cloud;
using DcxManager.Infra;
using DcxManager.Notes;
using DcxManager.Services;

namespace DcxManager.Views;

// DashboardView: the cards added with Edit dashboard (0.5.1 #12) — a host, a folder, an infrastructure or cloud provider,
// and the integrations (Grafana, Sentinel, Pingdom, BeyondTrust, Puppet, Entra ID). They read what the app already knows;
// a button contacts a service only when pressed. A locked host, folder or provider shows its name and padlock only.
public sealed partial class DashboardView
{
    /// <summary>Builds the cards added since the last layout and drops removed ones.</summary>
    private void SyncAddedCards()
    {
        var added = DashboardCatalog.Cards(S);
        foreach (var gone in _cards.Keys.Where(k => k.StartsWith("x:", StringComparison.Ordinal) && added.All(a => a.Id != k)).ToList())
        {
            (_cards[gone].Parent as Panel)?.Children.Remove(_cards[gone]);
            _cards.Remove(gone); _body.Remove(gone);
        }
        foreach (var a in added.Where(a => !_cards.ContainsKey(a.Id))) BuildCard(new Overview.Card(a.Id, a.Title, DashboardCatalog.Find(a.Type)?.Explain ?? ""));
    }

    private static bool IsAdded(string id) => id.StartsWith("x:", StringComparison.Ordinal);

    private void DrawAdded(string id, StackPanel body, List<Overview.SessionRow> rows)
    {
        if (DashboardCatalog.Cards(S).FirstOrDefault(c => c.Id == id) is not { } card) return;
        switch (card.Type)
        {
            case "host": DrawHostCard(body, card, rows); break;
            case "folder": DrawFolderCard(body, card, rows); break;
            case "infra": DrawProviderCard(body, card); break;
            case "cloud": DrawCloudCard(body, card); break;
            case "grafana": DrawGrafanaCard(body); break;
            case "sentinel": DrawSentinelCard(body); break;
            case "pingdom": DrawPingdomCard(body); break;
            case "beyondtrust": DrawBeyondTrustCard(body); break;
            case "puppet": DrawPuppetCard(body); break;
            case "entra": DrawEntraCard(body); break;
        }
    }

    private Button[] HostButtons(HostEntry h) => h.Protocol switch
    {
        "rdp" => new[] { Small("🖥", () => MainWindow.Instance.OpenRdp(h), "Remote Desktop") },
        "vnc" => new[] { Small("🖵", () => MainWindow.Instance.OpenVnc(h), "VNC desktop") },
        _ => new[] { Small("⌨", () => MainWindow.Instance.OpenTerminal(h), "Terminal"), Small("▣", () => MainWindow.Instance.OpenServer(h), "Manage"), Small("⇅", () => MainWindow.Instance.OpenFiles(h), "Files") },
    };

    private void DrawHostCard(StackPanel body, DashCard card, List<Overview.SessionRow> rows)
    {
        if (AppData.Current.FindHost(card.Target) is not { } h) { body.Children.Add(Hint("This host no longer exists — remove the card (⋯).")); return; }
        if (Locks.Hidden("host:" + h.Id)) { body.Children.Add(Row("🔒 " + h.Display, null, null, HostButtons(h))); body.Children.Add(Hint("Locked: unlock it to see its details here.")); return; }
        body.Children.Add(Row($"{h.Display}   {h.Endpoint}", Envs.For(h), null, HostButtons(h), mono: true));
        int open = rows.Count(r => r.Title == h.Display);
        var incidents = Incidents.IncidentStore.All.Count(i => i.IsActive && i.HostIds.Contains(h.Id));
        body.Children.Add(Line($"{open} session(s) open · last connected {(h.LastConnected is { } t ? t.ToString("dd MMM HH:mm") : "never")} · {NoteStore.ForHost(h.Id).Count} note(s){(incidents > 0 ? $" · ⚑ {incidents} open incident(s)" : "")}", FontStep.Small));
        var checks = AppData.Current.DashboardTiles.Where(t => t.HostId == h.Id || t.Target.Equals(h.Address, StringComparison.OrdinalIgnoreCase)).ToList();
        if (checks.Count > 0) body.Children.Add(Line("Pinned checks: " + string.Join(" · ", checks.Select(c => $"{c.KindTitle} {(c.LastOk == true ? "✓" : c.LastOk == false ? "✕" : "○")} {c.LastValue}")), FontStep.Small));
    }

    private void DrawFolderCard(StackPanel body, DashCard card, List<Overview.SessionRow> rows)
    {
        if (Locks.Hidden("folder:" + card.Target)) { body.Children.Add(Hint($"🔒 The folder {card.Target} is locked: unlock it to see its hosts here.")); return; }
        var hosts = AppData.Current.Hosts.Where(h => !h.IsAdHoc && AppData.InFolder(AppData.NormalizeFolder(h.Group), card.Target)).OrderBy(h => h.Display).ToList();
        if (hosts.Count == 0) { body.Children.Add(Hint($"No hosts in {card.Target}.")); return; }
        foreach (var h in hosts.Take(12))
        {
            var locked = Locks.Hidden("host:" + h.Id);
            int open = rows.Count(r => r.Title == h.Display);
            body.Children.Add(Row(locked ? "🔒 " + h.Display : $"{(open > 0 ? "● " : "")}{h.Display}   {(h.LastConnected is { } t ? t.ToString("dd MMM HH:mm") : "")}", locked ? null : Envs.For(h), null, HostButtons(h), mono: true));
        }
        if (hosts.Count > 12) body.Children.Add(Hint($"…and {hosts.Count - 12} more in Hosts → {card.Target}."));
    }

    private void DrawProviderCard(StackPanel body, DashCard card)
    {
        if (AppData.Current.InfraProviders.FirstOrDefault(p => p.Id == card.Target) is not { } c) { body.Children.Add(Hint("This provider no longer exists — remove the card (⋯).")); return; }
        if (Locks.Hidden("infra:" + c.Id)) { body.Children.Add(Hint($"🔒 {c.Name} is locked: unlock it in Infrastructure to see its state here.")); return; }
        var last = InfraStatus.For(c.Id);
        body.Children.Add(Row($"{Infra.Infra.KindTitle(c.Kind)} · {(last?.Text ?? "○ not checked")}", c.Environment, () => MainWindow.Instance.Navigate("infra"),
            new[] { Small("Check", () => _ = RunChecksAsync("manual", only: "infra:" + c.Id), "Contact this provider once, now") }));
        if (last != null) body.Children.Add(Line($"checked {last.At:HH:mm:ss} · {last.By}", FontStep.Small));
    }

    private void DrawCloudCard(StackPanel body, DashCard card)
    {
        if (CloudProviders.Find(card.Target) is not { } p) { body.Children.Add(Hint("Unknown cloud provider.")); return; }
        if (p.ActiveAccount is not { } acct) { body.Children.Add(Hint($"No {p.Title} account yet (Settings → Cloud providers).")); return; }
        if (Locks.Hidden(p.LockTarget(acct))) { body.Children.Add(Hint($"🔒 {p.Title} account {acct.Name} is locked.")); return; }
        _cloud.TryGetValue(p.Id, out var r);
        body.Children.Add(Row($"{p.Title} · {acct.Name}", null, () => MainWindow.Instance.Navigate(p.NavKey(p.InstanceDef)),
            new[] { Small("Check", () => _ = RunChecksAsync("manual", only: p.Id), "Ask the provider once, now") }));
        if (r == null) { body.Children.Add(Hint("Not checked yet — Check (or the dashboard's check rule) asks the provider.")); return; }
        if (r.Error != null) { body.Children.Add(Hint("✕ " + r.Error)); return; }
        var list = r.Instances ?? new();
        body.Children.Add(Line((r.Summary != null ? r.Summary + " · " : "") + string.Join(" · ", list.GroupBy(i => i.State).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}")), FontStep.Small));
    }

    private void DrawGrafanaCard(StackPanel body)
    {
        if (S.GrafanaUrl.Trim().Length == 0) { body.Children.Add(Hint("No Grafana server yet: Settings → Integrations → Dashboards & Grafana.")); return; }
        body.Children.Add(Row($"Grafana · {S.GrafanaUrl.Trim()}", null, () => MainWindow.Instance.Navigate("observe"),
            new[] { Small("Open", () => MainWindow.Instance.Navigate("observe"), "Observability → Dashboards lists and opens them in the app's browser") }, mono: true));
    }

    private void DrawSentinelCard(StackPanel body)
    {
        var sinks = Notify.LogShipper.Statuses.Where(x => x.Kind == "azuremonitor").ToList();
        if (sinks.Count == 0) { body.Children.Add(Hint("No Azure Monitor / Sentinel log sink yet: Settings → Integrations → Log sinks & Sentinel.")); return; }
        foreach (var x in sinks)
            body.Children.Add(Row($"{x.Name} · {x.State} · {x.Sent:N0} sent{(x.LastSent is { } t ? $", last {t:HH:mm}" : "")}{(x.LastError != null ? " · ✕ " + x.LastError : "")}", null, () => MainWindow.Instance.Navigate("observe"),
                glyph: x.LastError != null ? "✕" : "●", glyphBrush: x.LastError != null ? "Bad" : "Good"));
        body.Children.Add(Row("Microsoft Sentinel incidents (Azure portal)", null, () => PortalUi.OpenAddress("https://portal.azure.com/#view/Microsoft_Azure_Security_Insights/MainMenuBlade/~/Incidents"), glyph: "↗"));
    }

    private static List<Notify.PingdomCheck>? _pingdom;
    private static DateTime _pingdomAt;

    private void DrawPingdomCard(StackPanel body)
    {
        body.Children.Add(Row(_pingdom == null ? "Pingdom: not read in this session" : $"Pingdom: {_pingdom.Count} check(s), {_pingdom.Count(c => c.Status == "up")} up · read {_pingdomAt:HH:mm}", null, null,
            new[] { Small("Read now", async () =>
            {
                try { _pingdom = await Notify.PingdomClient.ChecksAsync(); _pingdomAt = DateTime.Now; }
                catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException or OfflineBlockedException) { _status.Text = "Pingdom: " + ex.Message.Split('\n')[0]; }
                Schedule();
            }, "Ask Pingdom once, now (the token stays encrypted on this PC)") }));
        foreach (var c in (_pingdom ?? new()).Where(c => c.Status != "up").Take(8))
            body.Children.Add(Row($"{c.Name} · {c.Hostname} · {c.Status}", null, null, glyph: "✕", glyphBrush: "Bad", mono: true));
    }

    private void DrawBeyondTrustCard(StackPanel body)
    {
        if (!S.BeyondTrustEnabled) { body.Children.Add(Hint("The BeyondTrust integration is off (Settings → Integrations → BeyondTrust).")); return; }
        var active = PasswordSafeSessions.Active;
        body.Children.Add(Row(active.Count == 0 ? "No Password Safe checkouts open." : $"{active.Count} Password Safe checkout(s) open", null, () => MainWindow.Instance.Navigate("beyondtrust"),
            new[] { Small("Open", () => MainWindow.Instance.Navigate("beyondtrust")) }));
        foreach (var c in active.Take(8)) body.Children.Add(Line($"   ⚿ {c.Label} · until {c.Until:HH:mm}", FontStep.Small));
    }

    private void DrawPuppetCard(StackPanel body)
    {
        var server = AppData.Current.FindHost(S.PuppetServerHostId);
        var db = AppData.Current.FindHost(S.PuppetDbHostId);
        if (server == null && db == null) { body.Children.Add(Hint("No Puppet server set up yet (AUTOMATION → Puppet).")); return; }
        body.Children.Add(Row($"Puppet server: {server?.Display ?? "—"} · PuppetDB: {db?.Display ?? "—"}", null, () => MainWindow.Instance.Navigate("puppet"),
            new[] { Small("Open", () => MainWindow.Instance.Navigate("puppet")) }));
    }

    private void DrawEntraCard(StackPanel body)
    {
        var dirs = Profiles.LoadWithDefault().Profiles.Where(p => p.Kind == "directory" && p.Directory != null).ToList();
        if (dirs.Count == 0) { body.Children.Add(Hint("No profile signs in with Entra ID or a directory yet (Settings → Identity & directory).")); return; }
        foreach (var p in dirs)
        {
            var problem = p.Directory!.Problem();
            body.Children.Add(Row($"{p.Name} · {p.Directory.Describe()}", null, null, glyph: problem == null ? "●" : "⚠", glyphBrush: problem == null ? "Good" : "Warn"));
        }
    }

    // ---- the editor ----------------------------------------------------------------------------------------------------

    /// <summary>Edit dashboard: show or hide the built-in cards, add cards from the catalogue, reset to default.</summary>
    public void OpenEditor()
    {
        if (DashboardCatalog.Locked(S) is { } why) { Dialogs.Info("Edit dashboard", why); return; }
        DashboardEditor.Show(Window.GetWindow(this), this);
    }

    /// <summary>After the editor or a card's menu changed the layout.</summary>
    internal void AfterEdit() { Save(); SyncAddedCards(); Layout(); Schedule(); }

    /// <summary>Asks first, then puts the default dashboard back (built-in cards, default order, nothing added).</summary>
    public void ResetToDefault(bool ask = true)
    {
        if (DashboardCatalog.Locked(S) is { } why) { Dialogs.Info("Reset dashboard", why); return; }
        if (ask && !Dialogs.Confirm("Reset to default", "Put the dashboard back as it came: every built-in card in its place, none hidden, the cards you added removed? Pinned checks stay.", "Reset", danger: true)) return;
        DashboardCatalog.Reset(S);
        AfterEdit();
        _status.Text = "Dashboard reset to the default.";
    }

    private IEnumerable<object?> AddedCardMenu(string id)
    {
        if (DashboardCatalog.Cards(S).FirstOrDefault(c => c.Id == id) is not { } card) yield break;
        yield return Menus.Item("Rename…", () =>
        {
            if (Dialogs.Prompt("Rename card", "Title:", card.Title) is { Length: > 0 and <= 80 } t) { DashboardCatalog.Replace(S, card with { Title = t.Trim() }); _cards.Remove(id); _body.Remove(id); AfterEdit(); }
        });
        yield return Menus.Item(card.Wide ? "Normal width" : "Full width", () => { DashboardCatalog.Replace(S, card with { Size = card.Wide ? "normal" : "wide" }); AfterEdit(); });
        yield return Menus.Item("✕ Remove card", () => { DashboardCatalog.Remove(S, id); AfterEdit(); });
    }
}
