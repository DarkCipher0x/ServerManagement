using System.Windows;
using DcxManager.Library;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>Shared library actions used by the Hosts and Credentials screens (ADR-033). Every move between local and
/// shared is explicit: Copy to my hosts / vault creates a NEW local item; Publish to library shows exactly what goes where.
/// Nothing here ever changes or removes a local host or credential.</summary>
public static class SharedLibraryUi
{
    public sealed record Target(LibraryClient Client, string Cid, string Label);

    /// <summary>Collections this PC may write to.</summary>
    public static List<Target> Writable() =>
        SharedLibraries.Clients.SelectMany(c => c.State.Collections.Keys.Where(c.CanEdit).Select(cid => new Target(c, cid, $"{c.State.Name} / {c.CollectionName(cid)}"))).ToList();

    public static CachedItem? Cached(string id) =>
        SharedLibraries.Resolve(id) is { } r ? r.Client.ItemsSnapshot().FirstOrDefault(i => i.Item == r.Item) : null;

    /// <summary>A shared reference (credential, sudo credential, jump host) inside a library item: an item id of the same library.</summary>
    private static string? ToItemRef(LibraryClient c, string? id, string what)
    {
        if (id == null) return null;
        if (SharedLibraries.Resolve(id) is { } r && r.Client == c) return r.Item;
        throw new LibraryException($"A shared host can only use a {what} from the same library ({c.State.Name}). Pick a shared one, or none.");
    }

    /// <summary>The item form of a shared host as edited on this PC.</summary>
    public static HostEntry ToItem(LibraryClient c, HostEntry h, string group)
    {
        var x = h.Clone();
        x.SharedFrom = null; x.Transient = null; x.IsAdHoc = false; x.Favorite = false; x.LastConnected = null;
        x.BitwardenItem = ""; x.PasswordSafeAccount = "";   // 0.6.17: this user's own Bitwarden / Password Safe links stay on this PC
        x.CredentialId = ToItemRef(c, h.CredentialId, "credential");
        x.SudoCredentialId = ToItemRef(c, h.SudoCredentialId, "sudo credential");
        x.JumpHostId = ToItemRef(c, h.JumpHostId, "jump host");
        x.TerminalProfileId = null;
        x.Group = group;
        x.Id = "";
        return x;
    }

    public static IEnumerable<object?> HostMenu(List<HostEntry> sel, Window? owner)
    {
        var shared = sel.Where(h => h.IsShared).ToList();
        var local = sel.Where(h => !h.IsShared).ToList();
        if (shared.Count > 0)
        {
            yield return Menus.Item($"⇩ Copy {(shared.Count == 1 ? "it" : shared.Count + " hosts")} to my hosts", () => CopyHostsToMine(shared),
                tip: "Creates new local hosts; a shared credential stays a live reference to the library");
            var one = shared.Count == 1 ? shared[0] : null;
            var can = one != null && SharedLibraries.Resolve(one.Id) is { } r && Cached(one.Id) is { } ci && r.Client.CanEdit(ci.Cid);
            yield return Menus.Item("✎ Edit in the shared library…", () => EditHost(one!, owner), enabled: can, tip: can ? null : "Your role in this collection is reader");
            yield return Menus.Item("✕ Delete from the shared library…", () => DeleteShared(shared.Select(h => h.Id).ToList(), "host"), enabled: shared.All(h => SharedLibraries.Resolve(h.Id) is { } rr && Cached(h.Id) is { } c2 && rr.Client.CanEdit(c2.Cid)));
            yield return Menus.Item("⇄ Open Shared library", () => MainWindow.Instance.Navigate("library"));
            yield return Menus.Sep();
        }
        if (local.Count > 0 && SharedLibraries.Clients.Count > 0)
            yield return Menus.Item($"⇪ Publish {(local.Count == 1 ? "to" : local.Count + " hosts to")} a shared library…", () => PublishHosts(local), enabled: Writable().Count > 0,
                tip: "Copies the selected hosts into a collection you can edit (preview first). Your local hosts are not changed.");
    }

    public static void CopyHostsToMine(List<HostEntry> hosts)
    {
        foreach (var h in hosts)
        {
            var copy = h.Clone();
            copy.Id = Guid.NewGuid().ToString("N");
            copy.SharedFrom = null;
            copy.Name = (string.IsNullOrWhiteSpace(h.Name) ? $"{h.Username}@{h.Address}" : h.Name) + " (from library)";
            copy.Group = "Shared copies";
            copy.LastConnected = null;
            AppData.Current.Hosts.Add(copy);
            if (SharedLibraries.Resolve(h.Id) is { } r) _ = Task.Run(() => r.Client.Audit("host copied to a member's own hosts", copy.Name));
        }
        AppData.Save();
        AuditLog.Write("you", "shared library", $"copied {hosts.Count} shared host(s) to my hosts");
        MainWindow.Instance?.Status($"Copied {hosts.Count} host(s) into “Shared copies”.");
    }

    public static void EditHost(HostEntry h, Window? owner)
    {
        if (SharedLibraries.Resolve(h.Id) is not { } r || Cached(h.Id) is not { } ci) return;
        var seed = h.Clone();
        seed.Group = ci.Payload.Host?.Group ?? "General";
        var w = new HostEditWindow(seed) { Owner = owner };
        if (w.ShowDialog() != true || w.Result is not { } edited) return;
        try
        {
            r.Client.SaveHost(ci.Cid, ci.Item, ToItem(r.Client, edited, edited.Group));
            Ssh.SshPool.Drop(h.Id);
            SharedLibraries.Rebuild();
        }
        catch (LibraryException ex) { Dialogs.Error("Shared library", ex.Message, explain: false); }
    }

    public static void DeleteShared(List<string> ids, string what)
    {
        var items = ids.Select(id => (Id: id, R: SharedLibraries.Resolve(id), C: Cached(id))).Where(x => x.R != null && x.C != null).ToList();
        if (items.Count == 0) return;
        if (!Dialogs.Confirm("Delete from the shared library", $"Delete {string.Join(", ", items.Select(x => LibraryClient.Title(x.C!)))} from the shared library for EVERY member?\n\n" +
                "A signed delete is written to the share; copies members made to their own hosts or vault are not touched.", "Delete for everyone", danger: true)) return;
        foreach (var x in items)
            try { x.R!.Value.Client.DeleteItem(x.R.Value.Item); }
            catch (LibraryException ex) { Dialogs.Error("Shared library", ex.Message, explain: false); }
        SharedLibraries.Rebuild();
    }

    /// <summary>The fields that go to the library, as the preview shows them. Never the secret itself.</summary>
    public static string Preview(HostEntry h, Credential? c) =>
        $"{h.Display}: {h.Endpoint}, {h.Protocol}, folder “{h.Group}”, tags “{h.Tags}”, environment “{h.Environment}”, notes {(h.Notes.Length > 0 ? h.Notes.Length + " characters" : "none")}" +
        (c != null ? $"\n   credential “{c.Name}” ({c.KindText}) — its {(c.Kind == CredentialKind.Password ? "password" : "private key")} becomes readable by every member of the collection" : "");

    public static void PublishHosts(List<HostEntry> hosts)
    {
        var t = Dialogs.Pick("Publish to a shared library", "Into which collection?", Writable(), x => x.Label);
        if (t == null) return;
        var creds = hosts.Select(h => h.CredentialId).Concat(hosts.Select(h => h.SudoCredentialId)).Where(id => id != null).Distinct()
            .Select(id => AppData.Current.FindCredential(id)).Where(c => c is { IsShared: false, IsExternal: false }).Cast<Credential>().ToList();   // 0.6.17: Bitwarden / Password Safe ones stay behind
        var jumpsDropped = hosts.Count(h => h.JumpHostId != null && !hosts.Any(x => x.Id == h.JumpHostId));
        var text = $"Into {t.Label}:\n\n" + string.Join("\n", hosts.Select(h => "• " + Preview(h, null))) +
                   (creds.Count > 0 ? $"\n\nCredentials they use ({creds.Count}): " + string.Join(", ", creds.Select(c => $"“{c.Name}”")) +
                                      "\nThey are published too, so their secrets become readable by every member of the collection." : "") +
                   (jumpsDropped > 0 ? $"\n\n{jumpsDropped} jump host reference(s) point at hosts not being published and are left out." : "") +
                   "\n\nYour own hosts and credentials stay exactly as they are.";
        if (!Dialogs.Confirm("Publish to a shared library", text, "Publish")) return;
        try { Publish(t, hosts, creds); }
        catch (LibraryException ex) { Dialogs.Error("Shared library", ex.Message, explain: false); }
    }

    /// <summary>Publishes local hosts (and the local credentials named) into a collection. Returns host item ids.</summary>
    public static List<string> Publish(Target t, List<HostEntry> hosts, List<Credential> creds)
    {
        var credMap = new Dictionary<string, string>();
        foreach (var c in creds.Where(c => !c.IsExternal))   // a pointer into this user's own Bitwarden / Password Safe means nothing to the others
        {
            var x = c.Clone(); x.Id = ""; x.SharedFrom = null;
            credMap[c.Id] = t.Client.SaveCredential(t.Cid, null, x);
        }
        var hostMap = hosts.ToDictionary(h => h.Id, _ => LibraryCrypto.NewId());
        var ids = new List<string>();
        foreach (var h in hosts)
        {
            var x = h.Clone();
            x.SharedFrom = null; x.Transient = null; x.Favorite = false; x.LastConnected = null; x.TerminalProfileId = null; x.Id = "";
            x.BitwardenItem = ""; x.PasswordSafeAccount = "";   // 0.6.17: nor its direct links (ids and labels of this user's items)
            string? Map(string? id) => id == null ? null : credMap.TryGetValue(id, out var m) ? m
                : SharedLibraries.Resolve(id) is { } r && r.Client == t.Client ? r.Item : null;
            x.CredentialId = Map(h.CredentialId); x.SudoCredentialId = Map(h.SudoCredentialId);
            x.JumpHostId = h.JumpHostId != null && hostMap.TryGetValue(h.JumpHostId, out var j) ? j : null;
            ids.Add(t.Client.SaveHost(t.Cid, hostMap[h.Id], x));
        }
        SharedLibraries.Rebuild();
        return ids;
    }

    // ---- credentials ----------------------------------------------------------------------------------------------

    public static IEnumerable<object?> CredentialMenu(Credential c, Window? owner)
    {
        if (c.IsShared)
        {
            var can = SharedLibraries.Resolve(c.Id) is { } r && Cached(c.Id) is { } ci && r.Client.CanEdit(ci.Cid);
            yield return Menus.Item("⇩ Copy to my vault", () => CopyCredentialToMine(c), tip: "A new local credential; later changes in the library don't reach it");
            yield return Menus.Item("✎ Edit in the shared library…", () => EditCredential(c, owner), enabled: can);
            yield return Menus.Item("✕ Delete from the shared library…", () => DeleteShared(new() { c.Id }, "credential"), enabled: can);
            yield return Menus.Item("⇄ Open Shared library", () => MainWindow.Instance.Navigate("library"));
        }
        else if (SharedLibraries.Clients.Count > 0)
            yield return Menus.Item("⇪ Publish to a shared library…", () => PublishCredential(c), enabled: Writable().Count > 0 && !c.IsExternal,
                tip: "Copies it into a collection you can edit; every member of that collection can then use the secret");
    }

    public static void CopyCredentialToMine(Credential c)
    {
        var x = c.Clone(); x.Id = Guid.NewGuid().ToString("N"); x.SharedFrom = null; x.Name = c.Name + " (from library)"; x.Created = DateTime.Now;
        AppData.Current.Credentials.Add(x);
        AppData.Save();
        if (SharedLibraries.Resolve(c.Id) is { } r) _ = Task.Run(() => r.Client.Audit("secret copied to a member's own vault", c.Name));
        AuditLog.Write("you", "vault", $"copied shared credential {c.Name} to my vault");
    }

    public static void EditCredential(Credential c, Window? owner)
    {
        if (SharedLibraries.Resolve(c.Id) is not { } r || Cached(c.Id) is not { } ci) return;
        var w = new CredentialEditWindow(c.Clone()) { Owner = owner };
        if (w.ShowDialog() != true || w.Result is not { } edited) return;
        var x = edited.Clone(); x.Id = ""; x.SharedFrom = null;
        try { r.Client.SaveCredential(ci.Cid, ci.Item, x); SharedLibraries.Rebuild(); }
        catch (LibraryException ex) { Dialogs.Error("Shared library", ex.Message, explain: false); }
    }

    public static void PublishCredential(Credential c)
    {
        var t = Dialogs.Pick("Publish to a shared library", "Into which collection?", Writable(), x => x.Label);
        if (t == null) return;
        if (!Dialogs.Confirm("Publish to a shared library", $"Publish “{c.Name}” ({c.KindText}) into {t.Label}?\n\nEvery member of that collection can then use and reveal the " +
                $"{(c.Kind == CredentialKind.Password ? "password" : "private key")}. Your own copy stays as it is.", "Publish", danger: true)) return;
        try
        {
            var x = c.Clone(); x.Id = ""; x.SharedFrom = null;
            t.Client.SaveCredential(t.Cid, null, x);
            SharedLibraries.Rebuild();
        }
        catch (LibraryException ex) { Dialogs.Error("Shared library", ex.Message, explain: false); }
    }
}
