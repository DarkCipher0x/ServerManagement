using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Library;
using DcxManager.Services;
using Microsoft.Win32;

namespace DcxManager.Views;

/// <summary>The Shared library screen (ADR-033): each library this PC belongs to, its state (synced / cached / waiting for
/// approval / removed), items with conflicts and warnings, members and join requests, the rotation checklist and the shared
/// audit. Every change goes through <see cref="LibraryClient"/>; this screen only asks and shows.</summary>
public sealed class SharedLibraryView : UserControl, IDisposable
{
    private readonly StackPanel _body = new();

    public sealed record ItemRow(string Kind, string Name, string Collection, string Version, string State, CachedItem Item);
    public sealed record MemberRow(string Name, string Role, string Access, string Fingerprint, RosterEntry Entry);
    public sealed record RequestRow(string Name, string Fingerprint, string Time, string Valid, RequestInfo Info);
    public sealed record TaskRow(string Credential, string Collection, string Because, string Created, string Done, RotationTask Task);

    public SharedLibraryView()
    {
        var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock { Text = "Shared library", Style = (Style)FindResource("H1") });
        head.Children.Add(new TextBlock
        {
            Text = "Hosts and team credentials shared through a folder or file share. Only ciphertext, signatures and public keys are written there: each item is AES-256-GCM under a collection key that is wrapped for each member's own key, and every change is signed. " +
                   "Shared items appear in Hosts and Credentials marked ⇄ shared, next to your own, and never replace them.",
            Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        Button B(string t, Action a, string? style = null, string? tip = null) { var b = new Button { Content = t, Margin = new Thickness(0, 0, 6, 4), ToolTip = tip }; if (style != null) b.Style = (Style)FindResource(style); b.Click += (_, _) => Guard(a); bar.Children.Add(b); return b; }
        B("＋ Create a library…", Create, "PrimaryButton", "Start a new library in an empty folder; you become its owner and get the add-on to hand to your team");
        B("⇩ Join a library…", Join, tip: "Install the team's library add-on first (Add-ons), then join: your keys are made on this PC and an admin approves you");
        B("⇩ Restore my keys…", RestoreKeys, tip: "Use your library keys from a password-sealed backup (a new PC, or after reinstalling)");
        B("⟳ Sync all", () => { foreach (var c in SharedLibraries.Clients) _ = SyncThenRender(c); });
        B("🔑 New SSH key…", () =>
        {
            if (SharedLibraryUi.Writable().FirstOrDefault() is not { } into) { Dialogs.Info("New SSH key", "You can't edit any collection yet: an admin gives you editor rights, or create a library."); return; }
            SshKeyWindow.Show(Window.GetWindow(this), into);
        }, tip: "Make a key pair straight into a collection you can edit (signed and encrypted for its members)");
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        SharedLibraries.Changed += Render;
        Render();
    }

    public void Dispose() => SharedLibraries.Changed -= Render;

    private static void Guard(Action a)
    {
        try { a(); }
        catch (LibraryException ex) { Dialogs.Error("Shared library", ex.Message, explain: false); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Dialogs.Error("Shared library", ex.Message); }
    }

    private async Task SyncThenRender(LibraryClient c) { await SharedLibraries.SyncAsync(c); Render(); }

    private TextBlock Text(string t, string? style = null, bool mono = false)
    {
        var tb = new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
        if (style != null) tb.Style = (Style)FindResource(style);
        if (mono) tb.Mono();
        return tb;
    }

    private ListView Table<T>(IEnumerable<T> rows, params (string Header, string Path, double Width)[] cols)
    {
        var gv = new GridView();
        foreach (var (h, p, w) in cols) gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) });
        var lv = new ListView { View = gv, ItemsSource = rows.ToList(), MaxHeight = 260, Margin = new Thickness(0, 4, 0, 8) };
        return lv;
    }

    public void Render()
    {
        _body.Children.Clear();
        var clients = SharedLibraries.Clients;
        var joinable = SharedLibraries.Addons.Where(a => a.Enabled && SharedLibraries.Profile(a, out _) is { IsGeneral: false } p && SharedLibraries.Client(p.LibraryId) == null).ToList();
        if (clients.Count == 0 && joinable.Count == 0)
            _body.Children.Add(Text("No shared library on this PC yet. Create one, or install the library add-on your team's admin gave you (Add-ons → Install) and then Join.", "Hint"));
        foreach (var a in joinable)
            _body.Children.Add(new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 10), Child = Text($"◇ {a.Manifest.Name} is installed but this PC hasn't joined it yet. Use “Join a library…”.") });
        foreach (var c in clients) _body.Children.Add(Card(c));
        foreach (var id in SharedLibraries.Orphans())
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var forget = new Button { Content = "Forget on this PC…", Style = (Style)FindResource("DangerButton") };
            forget.Click += (_, _) => { if (Dialogs.Confirm("Forget library keys", "Delete this PC's member keys and cached copy for this library? The share is untouched; you would have to join again.", "Forget", danger: true)) SharedLibraries.Forget(id); };
            DockPanel.SetDock(forget, Dock.Right);
            row.Children.Add(forget);
            row.Children.Add(Text($"Member keys for library {id[..8]}… are kept on this PC, but its add-on is not installed."));
            _body.Children.Add(row);
        }
    }

    private Border Card(LibraryClient c)
    {
        var st = c.State;
        var p = new StackPanel();
        p.Children.Add(new TextBlock { Text = "⇄ " + st.Name, Style = (Style)FindResource("H2") });
        var status = st.RemovedFromLibrary ? "You are NOT a member any more: the cached items and keys were removed from this PC."
            : st.Pending ? (c.MyCode is { } code
                ? $"Waiting for approval. Your code: {code} — tell {c.MyCodeAdmin ?? "your admin"} this code when they ask (over a call or chat you trust)."
                : "Waiting for an admin to approve you. When they start the check, a 6-digit code appears here (Sync) for you to read to them.")
            : st.LastError != null ? $"Using the copy cached {st.LastSync:yyyy-MM-dd HH:mm} — the share can't be used now: {st.LastError}"
            : $"Synced {st.LastSync:yyyy-MM-dd HH:mm}.";
        p.Children.Add(Text(status, st.LastError != null || st.RemovedFromLibrary ? "Hint" : null));
        p.Children.Add(Text($"Share: {st.SharePath}   ·   your role: {(c.MyRole.Length > 0 ? Roles.Label(c.MyRole) : "none")}   ·   sync interval: {(st.SyncMinutes > 0 ? st.SyncMinutes + " min" : "off")}{(st.Outbox.Count > 0 ? $"   ·   {st.Outbox.Count} edit(s) waiting to be sent" : "")}"));
        p.Children.Add(Text($"Your fingerprint: {c.Keys.Fingerprint}\nOwner fingerprint (from the add-on): {st.OwnerFingerprint}", mono: true));
        foreach (var prob in st.Problems.Take(8)) p.Children.Add(Text("⚠ " + prob));
        if (st.Header is { } hdr && LibraryClient.CurrentOwner(hdr) is { } owner) p.Children.Add(Text($"Owner: {c.NameOf(owner.Id)} ({LibraryCrypto.Fingerprint(owner.EncPub, owner.SigPub)}){((hdr.Transfers?.Count ?? 0) > 0 ? $" · ownership handed over {hdr.Transfers!.Count} time(s)" : "")}", "Hint"));

        var bar = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        void B(string t, Action a, bool enabled = true, string? style = null) { var b = new Button { Content = t, Margin = new Thickness(0, 0, 6, 4), IsEnabled = enabled }; if (style != null) b.Style = (Style)FindResource(style); b.Click += (_, _) => Guard(a); bar.Children.Add(b); }
        B("⟳ Sync", () => _ = SyncThenRender(c));
        B("＋ Collection…", () => { if (Dialogs.Prompt("New collection", "Name (e.g. Core infra, Customer X, Network):") is { Length: > 0 } n) { c.CreateCollection(n); SharedLibraries.Rebuild(); } }, c.IsAdmin);
        B("⇪ Export team add-on…", () => ExportPackage(c), c.IsAdmin);
        B("✉ Invite by e-mail…", () => _ = InviteAsync(c), c.IsAdmin);
        B("⚿ Permissions…", () => { LibraryAccessUi.Matrix(c, Window.GetWindow(this)); Render(); }, c.IsAdmin);
        B("👥 Groups…", () => { LibraryAccessUi.Groups(c, Window.GetWindow(this)); Render(); }, c.IsAdmin);
        B("☰ Shared audit…", () => ShowAudit(c), c.IsAdmin);
        B("⚿ Back up my keys…", () => BackupKeys(c), !st.RemovedFromLibrary);
        var offer = c.PendingTransfer();
        if (c.IsOwner) B(offer == null ? "Hand over ownership…" : "Withdraw ownership offer", () => { if (offer == null) HandOver(c); else { c.WithdrawOwnershipOffer(); Render(); } });
        if (offer != null && offer.ToId == c.Me) B("✓ Accept ownership…", () =>
        {
            if (!Dialogs.Confirm("Accept ownership", $"{c.NameOf(offer.FromId)} offers you ownership of “{st.Name}”. You become the owner (you appoint and remove admins); they stay an admin. Back up your keys afterwards: the owner key exists only on your PC.", "Accept")) return;
            c.AcceptOwnership(); SharedLibraries.Rebuild(); Render();
        }, style: "PrimaryButton");
        B("Sync interval…", () =>
        {
            if (Dialogs.Prompt("Sync interval", "Minutes between syncs (0 = off: sync at start, on Sync, and on changes in a local folder):", st.SyncMinutes.ToString()) is { } s && int.TryParse(s, out var m))
            { SharedLibraries.SetInterval(c, m); Render(); }
        });
        p.Children.Add(bar);

        // Items
        var items = c.ItemsSnapshot().OrderBy(i => c.CollectionName(i.Cid)).ThenBy(LibraryClient.Title).Select(i => new ItemRow(
            i.Payload.Kind == "cred" ? "credential" : "host", LibraryClient.Title(i), c.CollectionName(i.Cid), $"r{i.Rev} by {c.NameOf(i.Author)}",
            i.Conflicts.Count > 0 ? $"CONFLICT ({i.Conflicts.Count + 1} versions)" : i.Warning != null ? "⚠ " + i.Warning : i.Pending ? "waiting to be sent" : (c.CanEdit(i.Cid) ? "editable" : "read-only"), i)).ToList();
        p.Children.Add(Text($"ITEMS ({items.Count}) in {st.Collections.Count} collection(s): " + string.Join(", ", st.Collections.Select(x => x.Value + (c.CanEdit(x.Key) ? "" : " (read)"))), "Label"));
        var il = Table(items, ("Kind", "Kind", 90), ("Name", "Name", 220), ("Collection", "Collection", 160), ("Version", "Version", 170), ("State", "State", 360));
        Menus.ForList(il, this, () => il.SelectedItem is not ItemRow r ? Array.Empty<object?>() : ItemMenu(c, r.Item));
        p.Children.Add(il);

        // Members
        if (st.Roster is { } roster && !st.Pending && !st.RemovedFromLibrary)
        {
            var members = roster.Members.Select(m => new MemberRow(c.NameOf(m.Id) + (m.Id == c.Me ? " (you)" : ""), Roles.Label(m.Role),
                Roles.IsAdmin(m.Role) ? "all collections (editor)" : string.Join(", ", roster.EffectiveAccess(m).Select(a => $"{c.CollectionName(a.Key)}: {a.Value}"))
                    + (roster.Groups.Any(g => g.Members.Contains(m.Id)) ? $"  · groups: {string.Join(", ", roster.Groups.Where(g => g.Members.Contains(m.Id)).Select(g => g.Name))}" : ""),
                LibraryCrypto.Fingerprint(m.EncPub, m.SigPub), m)).ToList();
            if (roster.Groups.Count > 0) p.Children.Add(Text("GROUPS: " + string.Join(" · ", roster.Groups.Select(g => $"{g.Name} ({g.Members.Count}) → {string.Join(", ", g.Access.Select(a => c.CollectionName(a.Key) + ": " + a.Value))}")), "Hint"));
            p.Children.Add(Text($"MEMBERS ({members.Count})", "Label"));
            var ml = Table(members, ("Name", "Name", 200), ("Role", "Role", 80), ("Access", "Access", 330), ("Fingerprint", "Fingerprint", 330));
            Menus.ForList(ml, this, () => ml.SelectedItem is not MemberRow r || !c.IsAdmin || r.Entry.Role == Roles.Owner || r.Entry.Id == c.Me ? Array.Empty<object?>() : new object?[]
            {
                Menus.Item("Change role / access…", () => Guard(() => ChangeAccess(c, r.Entry))),
                Menus.Item("✕ Remove from the library…", () => Guard(() => RemoveMember(c, r.Entry))),
            });
            p.Children.Add(ml);
            if (c.IsAdmin)
            {
                var reqs = c.Requests().Select(q => new RequestRow(q.Name, q.Request.Fingerprint, q.Request.Time.Length >= 16 ? q.Request.Time[..16].Replace('T', ' ') : q.Request.Time, q.Valid ? "signed" : "INVALID", q)).ToList();
                p.Children.Add(Text($"JOIN REQUESTS ({reqs.Count}) — right-click: ① Start the check, ② when their code shows, Approve", "Label"));
                var rl = Table(reqs.Select(x => x with { Valid = x.Valid + " · " + CodeState(c, x.Info) }).ToList(), ("Name", "Name", 200), ("Fingerprint", "Fingerprint", 330), ("Requested (UTC)", "Time", 140), ("Signature · code", "Valid", 260));
                Menus.ForList(rl, this, () => rl.SelectedItem is not RequestRow r ? Array.Empty<object?>() : new object?[]
                {
                    Menus.Item("① Start the check (6-digit code)", () => Guard(() => { c.StartCheck(r.Info.Request.Id); Dialogs.Info("Check started", $"Ask {r.Name} to press Sync in their Shared library screen: a 6-digit code appears there. Then press Sync here; their code appears in this list. Approve when you've compared them over a call or chat you trust."); Render(); }),
                        enabled: r.Info.Valid && r.Info.Request.Commit.Length == 64, tip: "No long fingerprint to read out: both screens show the same 6-digit code"),
                    Menus.Item("② ✓ Approve (codes match)…", () => Guard(() => ApproveByCode(c, r.Info)), enabled: c.CodeFor(r.Info) != null),
                    Menus.Item("✓ Approve by full fingerprint…", () => Guard(() => Approve(c, r.Info)), enabled: r.Info.Valid, tip: "The older way: they read you their 32-character fingerprint"),
                    Menus.Item("✕ Deny", () => Guard(() => { c.Deny(r.Info.Request.Id); Render(); })),
                });
                p.Children.Add(rl);
            }
        }

        // Rotation checklist
        var open = st.Rotation.Where(t => t.Done == null).ToList();
        if (st.Rotation.Count > 0)
        {
            p.Children.Add(Text($"ROTATION CHECKLIST — {open.Count} credential(s) still to change on their targets", "Label"));
            p.Children.Add(Text("Re-keying protects future changes only: these secrets were readable by someone who has left. Change each one where it is used, update it here, then mark it rotated.", "Hint"));
            var tl = Table(st.Rotation.OrderBy(t => t.Done != null).Select(t => new TaskRow(t.Name, t.Collection, t.Because, t.Created.ToString("yyyy-MM-dd HH:mm"), t.Done?.ToString("yyyy-MM-dd HH:mm") ?? "OPEN", t)),
                ("Credential", "Credential", 200), ("Collection", "Collection", 160), ("Why", "Because", 220), ("Listed", "Created", 130), ("Rotated", "Done", 130));
            Menus.ForList(tl, this, () => tl.SelectedItem is not TaskRow r ? Array.Empty<object?>() : new object?[]
            {
                Menus.Item("✓ Mark rotated", () => { c.RotationDone(r.Task); Render(); }, enabled: r.Task.Done == null),
            });
            p.Children.Add(tl);
        }
        return new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 12), Child = p };
    }

    private IEnumerable<object?> ItemMenu(LibraryClient c, CachedItem i)
    {
        var id = SharedLibraries.ItemId(c.Id, i.Item);
        if (i.Conflicts.Count > 0)
            yield return Menus.Item("Resolve the conflict…", () => Guard(() =>
            {
                var versions = i.Conflicts.Prepend(i).ToList();
                var keep = Dialogs.Pick("Resolve conflict", $"“{LibraryClient.Title(i)}” was saved by two people from the same version. Keep which one? (The other is superseded by a signed revision; nothing is merged silently.)",
                    versions, v => $"r{v.Rev} by {c.NameOf(v.Author)} at {v.Time} — " + (v.Payload.Host is { } h ? $"{h.Endpoint}, {h.Group}" : v.Payload.Cred is { } cr ? $"{cr.Name}, {cr.KindText}" : v.Payload.Kind));
                if (keep != null) { c.ResolveConflict(i.Item, keep.RevId); SharedLibraries.Rebuild(); }
            }), enabled: c.CanEdit(i.Cid));
        if (c.IsAdmin && i.Payload.Kind != "tomb")
            yield return Menus.Item("🔒 Limit who can see this…", () => Guard(() => { LibraryAccessUi.Restrict(c, i, Window.GetWindow(this)); Render(); }), tip: "Moves it into its own folder that only the members you tick can open (enforced by encryption)");
        if (i.Warning != null && c.IsAdmin && i.Warning.Contains("VANISHED"))
            yield return Menus.Item("Accept that it was removed", () => Guard(() => { c.AcceptVanished(i.Item); SharedLibraries.Rebuild(); }));
        if (i.Payload.Kind == "host" && SharedLibraries.FindHost(id) is { } host)
        {
            yield return Menus.Item("⌨ Connect", () => { if (host.Protocol == "rdp") MainWindow.Instance.OpenRdp(host); else MainWindow.Instance.OpenTerminal(host); });
            foreach (var x in SharedLibraryUi.HostMenu(new() { host }, Window.GetWindow(this))) yield return x;
        }
        else if (SharedLibraries.FindCredential(id) is { } cred)
            foreach (var x in SharedLibraryUi.CredentialMenu(cred, Window.GetWindow(this))) yield return x;
    }

    private void Approve(LibraryClient c, RequestInfo r)
    {
        var fp = Dialogs.Prompt("Check the fingerprint", $"Request from “{r.Name}”.\n\nAsk them to read the fingerprint shown on THEIR screen (Shared library → Your fingerprint) over a call you trust, and type what they read:\n\n(the request says: {r.Request.Fingerprint})");
        if (fp == null) return;
        if (LibraryAccessUi.Ask(c, r.Name, null, Window.GetWindow(this)) is not { } a) return;
        c.Approve(r.Request.Id, fp, a.Role, a.Access);
        SharedLibraries.Rebuild();
        Render();
    }

    /// <summary>"waiting for their app" / the code / "not started" (old requests: "fingerprint only").</summary>
    private static string CodeState(LibraryClient c, RequestInfo r) =>
        r.Request.Commit.Length != 64 ? "fingerprint only (older app)" : c.CodeFor(r) is { } code ? "code " + code : c.State.Challenges.ContainsKey(r.Request.Id) ? "waiting for their app (Sync)" : "check not started";

    private void ApproveByCode(LibraryClient c, RequestInfo r)
    {
        if (c.CodeFor(r) is not { } code) { Dialogs.Info("Approve", "Their app hasn't answered yet: ask them to press Sync, then Sync here."); return; }
        if (!Dialogs.Confirm("Compare the codes", $"Your screen shows:\n\n        {code}\n\nDoes {r.Name}'s screen show exactly the same code? Ask them over a call or chat you trust — don't read yours out first.", "Same code — continue")) return;
        if (LibraryAccessUi.Ask(c, r.Name, null, Window.GetWindow(this)) is not { } a) return;
        c.ApproveWithCode(r, code, a.Role, a.Access);
        SharedLibraries.Rebuild();
        Render();
    }

    /// <summary>E-mails the team add-on (it holds no secret) with the steps to join, through the SMTP server set in Settings.</summary>
    private async Task InviteAsync(LibraryClient c)
    {
        var s = AppSettings.Current;
        if (s.SmtpHost.Trim().Length == 0 || s.SmtpFrom.Trim().Length == 0) { Dialogs.Info("Invite by e-mail", "Set up an SMTP server first: Settings → Integrations → E-mail notifications (the server and the From address)."); return; }
        if (Dialogs.Prompt("Invite by e-mail", $"Who should join “{c.State.Name}”? Their e-mail address:") is not { Length: > 0 } to) return;
        try { Notify.SmtpSender.Recipients(to); } catch (FormatException ex) { Dialogs.Error("Invite by e-mail", ex.Message, explain: false); return; }
        var dir = System.IO.Path.Combine(AppInfo.CacheDir, "tmp", "invite-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var pkg = System.IO.Path.Combine(dir, "shared-library-" + string.Concat(c.State.Name.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')) + ".dcxaddon");
        try
        {
            SharedLibraries.WritePackage(c.Profile(), c.NameOf(c.Me), pkg);
            var body = $"{c.NameOf(c.Me)} invites you to the shared library “{c.State.Name}” in DarkCipher ServerManagement.\n\n" +
                       "1. Save the attached add-on, then in ServerManagement: Add-ons → Install, and pick it.\n" +
                       $"2. Shared library → Join a library. The library's folder: {c.State.SharePath}\n" +
                       "3. Your admin starts a short check: press Sync, and read them the 6-digit code that appears — over a call or chat you trust.\n\n" +
                       $"The add-on holds no password or key. It names the library and its owner's fingerprint ({c.State.OwnerFingerprint}) so your app can tell it's the real one.";
            await Notify.SmtpSender.SendAsync(Notify.SmtpSender.FromSettings(s), [to.Trim()], $"Join the shared library “{c.State.Name}”", body,
                [(System.IO.Path.GetFileName(pkg), await System.IO.File.ReadAllBytesAsync(pkg))]);
            c.Audit("invitation e-mailed", to.Trim());
            AuditLog.Write("you", "shared library " + c.State.Name, "invitation e-mailed", to.Trim());
            Dialogs.Info("Invite by e-mail", $"Sent to {to.Trim()} with the team add-on attached. When their join request appears here, right-click it → ① Start the check.");
        }
        catch (Exception ex) { Dialogs.Error("Invite by e-mail", ex.Message); }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }

    private void ChangeAccess(LibraryClient c, RosterEntry e)
    {
        if (LibraryAccessUi.Ask(c, c.NameOf(e.Id), e, Window.GetWindow(this)) is not { } a) return;
        var (role, access) = a;
        var tasks = c.SetAccess(e.Id, role, access);
        SharedLibraries.Rebuild();
        if (tasks.Count > 0) Dialogs.ShowText("Credentials to rotate", "Collections this member can no longer read were re-keyed. These credentials were readable to them; change them on their targets:\n\n" + string.Join("\n", tasks.Select(t => $"• {t.Collection} / {t.Name}")));
        Render();
    }

    private void RemoveMember(LibraryClient c, RosterEntry e)
    {
        var name = c.NameOf(e.Id);
        if (!Dialogs.Confirm("Remove member", $"Remove {name} from “{c.State.Name}”?\n\nEvery collection they could read gets a new key and is re-encrypted, and the library's metadata key is replaced. " +
                "They have already seen the secrets, so the credentials they could read go on a rotation checklist.", "Remove", danger: true, typeToConfirm: name)) return;
        var tasks = c.Remove(e.Id);
        SharedLibraries.Rebuild();
        Dialogs.ShowText("Credentials to rotate", $"{name} was removed and {tasks.Select(t => t.Collection).Distinct().Count()} collection(s) were re-keyed.\n\nChange these on their targets (also listed under ROTATION CHECKLIST):\n\n" +
            (tasks.Count == 0 ? "(none)" : string.Join("\n", tasks.Select(t => $"• {t.Collection} / {t.Name}"))));
        Render();
    }

    private void ShowAudit(LibraryClient c)
    {
        var entries = c.ReadAudit();
        Dialogs.ShowText("Shared audit — " + c.State.Name,
            "Each member's PC signs and hash-chains its own lines; reads on a member's PC are reported by their copy of the app.\n\n" +
            string.Join("\n", entries.Select(a => $"{(a.Time.Length >= 19 ? a.Time[..19].Replace('T', ' ') : a.Time)}  {a.Name,-18} {a.Action}: {a.Target}{(a.Problem != null ? "   ⚠ " + a.Problem : "")}")));
    }

    private void HandOver(LibraryClient c)
    {
        var admins = c.State.Roster?.Members.Where(m => m.Role == Roles.Admin).ToList() ?? new();
        if (admins.Count == 0) { Dialogs.Info("Hand over ownership", "Ownership goes to an admin. Make someone an admin first (Members → Change role / access…)."); return; }
        var to = Dialogs.Pick("Hand over ownership", "Offer ownership to which admin? Nothing changes until they accept on their PC; you stay an admin.", admins, m => $"{c.NameOf(m.Id)}   {LibraryCrypto.Fingerprint(m.EncPub, m.SigPub)}");
        if (to == null) return;
        c.OfferOwnership(to.Id);
        Dialogs.Info("Ownership offered", $"{c.NameOf(to.Id)} sees “Accept ownership…” in their Shared library screen after their next sync. You can withdraw the offer until then.");
        Render();
    }

    private static void BackupKeys(LibraryClient c)
    {
        var pw = Dialogs.Prompt("Back up my keys", "A password for the backup file (at least 12 characters). Anyone with the file AND the password can act as you in this library.", password: true);
        if (pw == null) return;
        if (Dialogs.Prompt("Back up my keys", "Type the password again:", password: true) != pw) { Dialogs.Error("Back up my keys", "The passwords don't match.", explain: false); return; }
        var bytes = c.ExportKeys(pw);
        var dlg = new SaveFileDialog { FileName = "library-keys-" + c.State.Name.Replace(' ', '-').ToLowerInvariant() + ".dcxkey", Filter = "Library key backup|*.dcxkey" };
        if (dlg.ShowDialog() != true) return;
        if (!StorageGuard.ConfirmDestination(System.IO.Path.GetDirectoryName(dlg.FileName)!, "Library key backup", encrypted: true)) return;
        System.IO.File.WriteAllBytes(dlg.FileName, bytes);
        Dialogs.Info("Back up my keys", $"Saved {System.IO.Path.GetFileName(dlg.FileName)} (sealed with your password: PBKDF2-SHA256, 600,000 iterations, AES-256-GCM). Keep it off this PC.");
    }

    private void RestoreKeys()
    {
        var dlg = new OpenFileDialog { Filter = "Library key backup|*.dcxkey|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        if (Dialogs.Prompt("Restore my keys", "The backup's password:", password: true) is not { } pw) return;
        var st = LibraryClient.ImportKeys(System.IO.File.ReadAllBytes(dlg.FileName), pw);
        var c = SharedLibraries.Restore(st);
        _ = SyncThenRender(c);
        Dialogs.Info("Restore my keys", $"Your keys for “{st.Name}” are back on this PC (fingerprint {c.Keys.Fingerprint}). It syncs now.");
    }

    private static void ExportPackage(LibraryClient c)
    {
        var dlg = new SaveFileDialog { FileName = "shared-library-" + c.State.Name.Replace(' ', '-').ToLowerInvariant() + ".dcxaddon", Filter = "Add-on package|*.dcxaddon" };
        if (dlg.ShowDialog() != true) return;
        SharedLibraries.WritePackage(c.Profile(), c.NameOf(c.Me), dlg.FileName);
        Dialogs.Info("Team add-on", $"Saved {System.IO.Path.GetFileName(dlg.FileName)}. It holds no secret: give it to your team. Each person installs it, joins, and reads you their fingerprint before you approve them.\n\nOwner fingerprint in it: {c.State.OwnerFingerprint}");
    }

    private void Create()
    {
        if (!Restrictions.Check("library.create")) return;
        var dlg = new OpenFolderDialog { Title = "Empty folder for the library (a file share, NAS folder or synced folder)" };
        if (dlg.ShowDialog() != true) return;
        if (Dialogs.Prompt("Create a library", "Library name (shown to members):", "Team") is not { Length: > 0 } name) return;
        if (Dialogs.Prompt("Create a library", "Your name (shown to members):", AppIdentity.Name) is not { Length: > 0 } me) return;
        if (Dialogs.Prompt("Create a library", "First collection:", "General") is not { Length: > 0 } coll) return;
        // 0.3.0 §22: no add-on needed; members join from the folder with the owner's fingerprint. Export one later for older versions.
        var c = SharedLibraries.Create(dlg.FolderName, name.Trim(), me.Trim(), coll.Trim());
        Dialogs.Info("Library created", $"“{name}” is ready, with you as its owner. Members join from its folder — read them this owner fingerprint over a call or chat you trust:\n\n{c.Keys.Fingerprint}\n\nBack up this profile (Settings → Security): your owner key lives only here. (“Export team add-on” is for members on older versions.)");
        Render();
    }

    private void Join()
    {
        if (!Restrictions.Check("library.join")) return;
        var addons = SharedLibraries.Addons.Where(a => a.Enabled && SharedLibraries.Profile(a, out _) is { IsGeneral: false } p && (SharedLibraries.Client(p.LibraryId) == null || SharedLibraries.Client(p.LibraryId)!.State.RemovedFromLibrary)).ToList();
        // 0.3.0 §22: join straight from the share with the owner's fingerprint — no add-on needed. An installed team add-on
        // (older versions) is still offered.
        if (addons.Count == 0 || Dialogs.Choose("Join a library", "Join from the library's folder (the owner reads you their fingerprint), or with an installed team add-on?", new[] { "From its folder…", "With the add-on…", "Cancel" }, cancelIndex: 2) == 0)
        {
            var dlg = new OpenFolderDialog { Title = "The library's folder (file share, NAS or synced folder)" };
            if (dlg.ShowDialog() != true) return;
            if (Dialogs.Prompt("Join a library", "The owner's fingerprint (8 groups of 4 characters), read to you by the owner over a call or chat you trust:") is not { Length: > 0 } fp) return;
            if (Dialogs.Prompt("Join a library", "Your name (shown to the admins):", AppIdentity.Name) is not { Length: > 0 } me2) return;
            var c2 = SharedLibraries.JoinShare(dlg.FolderName, fp, me2.Trim());
            Dialogs.Info("Join request sent", $"Your fingerprint: {c2.Keys.Fingerprint}\n\nAn admin starts a check; press Sync here to see your 6-digit code and read it to them. Once approved, Sync shows the shared hosts and credentials.");
            Render();
            return;
        }
        var a = addons.Count == 1 ? addons[0] : Dialogs.Pick("Join a library", "Which library?", addons, x => x.Manifest.Name);
        if (a == null || SharedLibraries.Profile(a, out _) is not { } prof) return;
        var path = Dialogs.Prompt("Join a library", "Where is the library's folder on this PC or network?", prof.Path);
        if (path is not { Length: > 0 }) return;
        if (Dialogs.Prompt("Join a library", "Your name (shown to the admins):", AppIdentity.Name) is not { Length: > 0 } me) return;
        var c = SharedLibraries.Join(a, path.Trim(), me.Trim());
        Dialogs.Info("Join request sent", $"Read this fingerprint to an admin of “{prof.Name}” over a call you trust:\n\n{c.Keys.Fingerprint}\n\nOnce they approve you, Sync shows the shared hosts and credentials.");
        Render();
    }
}
