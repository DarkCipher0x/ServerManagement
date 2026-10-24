namespace DcxManager.Services;

/// <summary>One card the user added to the dashboard (0.5.1 #12): which kind, what it shows (a host, a folder, a provider),
/// its title and its width. Saved as one line in <see cref="AppSettings.DashboardCards"/> ("id|type|target|title|size"),
/// so Group Policy can force a layout as a plain list.</summary>
public sealed record DashCard(string Id, string Type, string Target, string Title, string Size)
{
    public bool Wide => Size == "wide";

    public string Encode() => string.Join('|', Id, Type, Esc(Target), Esc(Title), Size);

    public static DashCard? Parse(string line)
    {
        var p = line.Split('|');
        if (p.Length < 4 || DashboardCatalog.Find(p[1]) == null) return null;
        var id = p[0].Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^x:[A-Za-z0-9-]{1,40}$")) return null;
        var title = Unesc(p[3]).Trim();
        return new DashCard(id, p[1], Unesc(p[2]).Trim(), title.Length is > 0 and <= 80 ? title : DashboardCatalog.Find(p[1])!.Title, p.Length > 4 && p[4] == "wide" ? "wide" : "normal");
    }

    private static string Esc(string s) => s.Replace("%", "%25").Replace("|", "%7C");
    private static string Unesc(string s) => s.Replace("%7C", "|").Replace("%25", "%");
}

/// <summary>A kind of card the editor offers: its group, what it shows, and what has to be picked (a host, a folder, an
/// infrastructure provider, a cloud provider) or nothing.</summary>
public sealed record DashCardType(string Type, string Group, string Title, string Explain, string? Pick);

/// <summary>
/// The dashboard's card catalogue (0.5.1 #12). Built-in cards are shown or hidden; these are added (several of the same
/// kind for different hosts or providers). Every card reads what the app already knows — nothing here contacts a server;
/// the remote checks stay with the dashboard's check rule. A card never shows a locked item (items 9 and 10).
/// </summary>
public static class DashboardCatalog
{
    public static readonly DashCardType[] Types =
    {
        new("host", "Hosts", "A host", "One saved host: environment, open sessions, last connected, its notes and incidents, and buttons to connect", "host"),
        new("folder", "Hosts", "A host folder", "The hosts in one folder: which have sessions open and when each was last used", "folder"),
        new("infra", "Infrastructure", "An infrastructure provider", "One provider's last known state (Proxmox, vCenter, Hyper-V, Docker, Unraid, Synology…): reachability and VM / container counts", "infra"),
        new("cloud", "Cloud", "A cloud provider", "One cloud provider's instances by state from the last check, and its account", "cloud"),
        new("grafana", "Integrations", "Grafana dashboards", "Your Grafana dashboards (Settings → Integrations → Dashboards), opened in the app's browser", null),
        new("sentinel", "Integrations", "Microsoft Sentinel", "The Azure Monitor / Sentinel log sink: what was sent, the last error, and a link to Sentinel's incidents", null),
        new("pingdom", "Integrations", "Pingdom checks", "Your Pingdom checks as the app last saw them", null),
        new("beyondtrust", "Integrations", "BeyondTrust", "Password Safe checkouts open right now and when they end", null),
        new("puppet", "Integrations", "Puppet", "The Puppet server and PuppetDB set up here, and a link to their screen", null),
        new("entra", "Integrations", "Entra ID & directory", "The directory profiles and whether you are signed in to each", null),
    };

    public static DashCardType? Find(string type) => Types.FirstOrDefault(t => t.Type == type);

    /// <summary>The added cards in force: Group Policy's list when it sets one, else the user's.</summary>
    public static List<DashCard> Cards(AppSettings s) => s.DashboardCards.Select(DashCard.Parse).OfType<DashCard>().GroupBy(c => c.Id).Select(g => g.First()).ToList();

    /// <summary>Why the layout can't be changed here (Group Policy), or null.</summary>
    public static string? Locked(AppSettings s) =>
        s.DashboardLocked ? "The dashboard layout is set by your organisation (Group Policy: DashboardLocked)."
        : PolicySettings.Get(nameof(AppSettings.DashboardCards)) != null ? "The dashboard's cards are set by your organisation (Group Policy: DashboardCards)." : null;

    public static DashCard Add(AppSettings s, string type, string target, string? title = null)
    {
        var t = Find(type) ?? throw new ArgumentException("unknown card type " + type);
        var card = new DashCard("x:" + Guid.NewGuid().ToString("N")[..12], type, target, (title ?? DefaultTitle(t, target)).Trim() is { Length: > 0 and <= 80 } tt ? tt : t.Title, "normal");
        s.DashboardCards.Add(card.Encode());
        return card;
    }

    public static void Replace(AppSettings s, DashCard card)
    {
        int i = s.DashboardCards.FindIndex(l => DashCard.Parse(l)?.Id == card.Id);
        if (i >= 0) s.DashboardCards[i] = card.Encode();
    }

    public static void Remove(AppSettings s, string id)
    {
        s.DashboardCards.RemoveAll(l => DashCard.Parse(l)?.Id == id);
        s.DashboardOrder.Remove(id);
        s.DashboardHidden.Remove(id);
    }

    /// <summary>Back to the default dashboard: the built-in cards in their order, nothing hidden, nothing added.</summary>
    public static void Reset(AppSettings s) { s.DashboardOrder.Clear(); s.DashboardHidden.Clear(); s.DashboardCards.Clear(); }

    private static string DefaultTitle(DashCardType t, string target) => t.Type switch
    {
        "host" => AppData.Current.FindHost(target)?.Display ?? "Host",
        "folder" => "Folder " + target,
        "infra" => AppData.Current.InfraProviders.FirstOrDefault(p => p.Id == target)?.Name ?? "Infrastructure provider",
        "cloud" => Cloud.CloudProviders.Find(target)?.Title ?? "Cloud provider",
        _ => t.Title,
    };
}
