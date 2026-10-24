using System.Text;
using System.Text.RegularExpressions;
using DcxManager.Cloud;
using DcxManager.Ssh;
using System.Text.Json;
using System.Text.Json.Nodes;
using DcxManager.Services;
using DcxManager.Views;
using DcxManager.Sessions;

namespace DcxManager;

/// <summary>
/// The features' capabilities (ADR-028 as the AI tool registry's built-ins; 0.2.0 phase 2: registered into
/// <see cref="Capabilities"/> by the composition root, after <see cref="OperatorCapabilities"/>): infrastructure providers,
/// cloud screens, the terminal target, notes, incidents, documents, transfers, portals and shared libraries. Each handler
/// keeps its own safeguards (allow-changes, environment, provider permissions, the terminal bridge's review) and returns
/// server data inside untrusted markers. This file knows the features and some screens; it moves with them in phase 6.
/// </summary>
public static class FeatureCapabilities
{
    private const int MaxRows = Capabilities.MaxRows;

    public static void Register() => RegisterBuiltIns();

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static string S(JsonObject a, string k, string def = "") => Capabilities.Arg(a, k, def);
    private static string Short(string s, int n) => Capabilities.Short(s, n);
    private static string CloudIds(string separator) => string.Join(separator, CloudProviders.All.Select(p => p.Id));
    private static string Untrusted(string source, string text, int max = Capabilities.MaxText) => Capabilities.Untrusted(source, text, max);

    private static Infra.ProviderConfig ProviderArg(JsonObject a)
    {
        var n = S(a, "provider").Trim();
        var c = AppData.Current.InfraProviders.FirstOrDefault(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase) || p.Id == n)
                ?? throw new InvalidOperationException($"No infrastructure provider called “{n}”. Use infra_list_providers.");
        Locks.Demand("infra:" + c.Id, c.Name);   // 0.5.1 #10: a closed padlock refuses before anything connects
        return c;
    }

    /// <summary>A saved host by name, refused while its padlock (or its folder's) is closed.</summary>
    private static HostEntry HostNamed(string name)
    {
        var h = AppData.Current.FindHostByName(name) ?? throw new InvalidOperationException($"No saved host called '{name}'. Use list_hosts.");
        Locks.Demand("host:" + h.Id, h.Display);
        return h;
    }

    // ---- registrations ------------------------------------------------------------------------------------------------
    private static void RegisterBuiltIns()
    {
        Capabilities.Register(new Capability("current_context", "What the user is looking at in the app right now (screen, host, terminal target, cloud provider and account, infrastructure provider). Metadata only.", CapabilityRisk.Read,
            Array.Empty<(string, string, string, bool)>(), (_, _, _) => Task.FromResult(Views.ScreenContext.Describe())));

        // Infrastructure (Docker, Proxmox, Unraid, vCenter, Hyper-V) — the same rules as the Infrastructure screen.
        Capabilities.Register(new Capability("infra_list_providers", "List the infrastructure providers set up in the app (Docker, Proxmox, Unraid, VMware vCenter, Hyper-V): name, kind, environment, whether changes are allowed, last known state.", CapabilityRisk.Read,
            Array.Empty<(string, string, string, bool)>(), (_, _, _) => Task.FromResult(JsonSerializer.Serialize(AppData.Current.InfraProviders.Select(p => Locks.Hidden("infra:" + p.Id) ? (object)new { name = p.Name, locked = true } : new
            {
                name = p.Name, kind = Infra.Infra.KindTitle(p.Kind), environment = p.Environment, changes_allowed = p.AllowChanges,
                last_state = Infra.InfraStatus.For(p.Id) is { } st ? $"{st.Text} (checked {st.At:HH:mm})" : "not checked this session",
            })))));

        Capabilities.Register(new Capability("infra_inventory", "Connect to one infrastructure provider and list its objects (VMs, containers, hosts, datastores, checkpoints…) with id, kind, name, status and details.", CapabilityRisk.Read,
            new[] { ("provider", "string", "Provider name from infra_list_providers", true), ("kind", "string", "Optional filter: vm, container, host, checkpoint …", false) },
            async (a, _, ct) =>
            {
                var c = ProviderArg(a);
                var p = Infra.Infra.Create(c);
                try
                {
                    var caps = await p.DetectAsync(ct);
                    if (!caps.Reachable) return $"Not reachable: {caps.Detail}";
                    var items = await p.InventoryAsync(ct);
                    Infra.InfraStatus.Record(c, caps, items, "AI tool");
                    var kind = S(a, "kind");
                    var rows = items.Where(o => kind.Length == 0 || o.Kind == kind).Take(MaxRows).Select(o => $"{o.Kind}\t{o.Id}\t{o.Name}\t{o.Status}\t{o.Parent}\t{o.Detail}");
                    return $"{c.Name}: {caps.Detail} · environment {c.Environment} · changes {(c.AllowChanges ? "allowed (each one still asks)" : "NOT allowed (read-only)")}\n" +
                           Untrusted($"infrastructure:{c.Name}", "kind\tid\tname\tstatus\ton\tdetails\n" + string.Join("\n", rows));
                }
                finally { (p as IDisposable)?.Dispose(); }
            }));

        Capabilities.Register(new Capability("infra_action", "Run an action on an infrastructure object (start, stop, shut down, checkpoint, clone…). Uses the same checks as the Infrastructure screen: the provider must allow changes, the target must permit it, and the user approves it.", CapabilityRisk.Change,
            new[] { ("provider", "string", "Provider name", true), ("object_id", "string", "Object id from infra_inventory", true), ("action", "string", "Action id, e.g. start, shutdown, stop, reboot, checkpoint, clone", true), ("inputs", "object", "Values the action needs, e.g. {\"name\": \"before-upgrade\"}", false) },
            async (a, ctx, ct) =>
            {
                var c = ProviderArg(a);
                var p = Infra.Infra.Create(c);
                try
                {
                    var caps = await p.DetectAsync(ct);
                    var items = caps.Reachable && caps.CanRead ? await p.InventoryAsync(ct) : Array.Empty<Infra.InfraObject>();
                    var o = items.FirstOrDefault(x => x.Id == S(a, "object_id")) ?? throw new InvalidOperationException("No such object; use infra_inventory for ids.");
                    var offer = Infra.Infra.Offer(p, o, caps).FirstOrDefault(x => x.Action.Id == S(a, "action"));
                    if (offer == null) return $"ERROR: “{S(a, "action")}” isn't an action for this {o.Kind}. Possible: {string.Join(", ", p.ActionsFor(o).Select(x => x.Id))}";
                    if (!offer.Enabled) return $"REFUSED: {offer.Why}";
                    var input = new Dictionary<string, string>();
                    if (a["inputs"] is JsonObject inp) foreach (var kv in inp) if (kv.Value is JsonValue v) input[kv.Key] = v.ToString();
                    foreach (var i in offer.Action.Inputs) if (i.Validate(input.GetValueOrDefault(i.Name) ?? "") is { } bad) return "ERROR: " + bad;
                    if (!await ctx.Approve($"AI wants to: {offer.Action.Label.Trim()} — {o.Name}", Infra.Infra.ConfirmText(p, offer.Action, o, input), null)) return "User declined.";
                    var r = await p.RunAsync(offer.Action, o, input, ct);
                    AuditLog.Write(ctx.Actor, $"{c.Name}:{o.Name}", $"{offer.Action.Id} {o.Kind} (AI, approved)", (r.Verified ? "VERIFIED · " : r.Accepted ? "NOT VERIFIED · " : "REFUSED · ") + r.Message);
                    return (r.Verified ? "VERIFIED: " : r.Accepted ? "accepted, NOT verified: " : "REFUSED: ") + r.Message;
                }
                finally { (p as IDisposable)?.Dispose(); }
            }));

        // Any cloud provider's resource lists (read-only), straight from its catalog: only screens that exist can be read.
        Capabilities.Register(new Capability("cloud_list", "List a resource of a connected cloud provider (" + CloudIds(", ") + "), e.g. Volumes, Firewalls, Networks, Load Balancers, DNS zones. Read-only; columns as the app shows them.", CapabilityRisk.Read,
            new[] { ("provider", "string", CloudIds("|"), true), ("resource", "string", "The screen title, e.g. Volumes (call with an unknown title to get the list)", true) },
            async (a, _, ct) =>
            {
                var p = CloudProviders.Find(S(a, "provider").ToLowerInvariant()) ?? throw new InvalidOperationException("provider must be one of " + string.Join(", ", CloudProviders.All.Select(x => x.Id)));
                var def = p.Catalog.FirstOrDefault(d => d.Title.Equals(S(a, "resource"), StringComparison.OrdinalIgnoreCase) && !d.IsChild);
                if (def == null) return $"ERROR: {p.Title} resources: {string.Join(", ", p.Catalog.Where(d => !d.IsChild).Select(d => d.Title))}";
                var api = p.Require();
                if (def.Single) return Untrusted($"{p.Id}:{def.Title}", J.Pretty(await api.GetAsync(def.Path, ct)));
                var rows = await api.ListAsync(def.Path, def.Key, ct);
                var cols = def.Columns.Select(c => c.Key).ToList();
                var sb = new StringBuilder(string.Join("\t", def.Columns.Select(c => c.Header))).Append('\n');
                foreach (var r in rows.Take(MaxRows)) sb.Append(string.Join("\t", cols.Select(k => J.Str(r, k).Replace('\t', ' ').Replace('\n', ' ')))).Append('\n');
                return $"{p.Title} · account {api.AccountName} · {def.Title}: {rows.Count} item(s){(rows.Count > MaxRows ? $", first {MaxRows} shown" : "")}\n" + Untrusted($"{p.Id}:{def.Title}", sb.ToString());
            }));

        // Terminals: only the session the user chose in the AI Engineer; every command reviewed by the bridge.
        Capabilities.Register(new Capability("list_terminal_sessions", "List the open terminal sessions and which one (if any) the user chose as the AI's target. You can only run commands in the chosen one.", CapabilityRisk.Read,
            Array.Empty<(string, string, string, bool)>(), (_, _, _) =>
            {
                var t = TerminalBridge.Target;
                var sb = new StringBuilder();
                sb.AppendLine(t != null ? $"TARGET (chosen by the user): {t.Describe()}" + (TerminalBridge.Problem(t, out _) is { } p ? $" — currently unusable: {p}" : "") : "No target chosen: ask the user to pick one under TARGET TERMINAL in the AI Engineer.");
                foreach (var v in TerminalBridge.Candidates()) sb.AppendLine($"session {v.SessionId}: {v.Target.Title} · {v.Target.Endpoint} · shell {v.Shell}{(t?.SessionId == v.SessionId ? " · TARGET" : "")}");
                foreach (var (title, why) in TerminalBridge.Unsupported()) sb.AppendLine($"(not usable) {title}: {why}");
                return Task.FromResult(sb.ToString());
            }));

        Capabilities.Register(new Capability("terminal_run", "Run a command (or a short script) in the terminal session the user chose as target and read the result. Read-only commands run straight away if the user turned on auto-run; anything else is shown with its risk and approved first (risky ones need more). Returns the exit status, directory and output.", CapabilityRisk.Change,
            new[] { ("command", "string", "The exact command(s) for that shell (bash/sh or PowerShell). Non-interactive; must end on its own.", true), ("purpose", "string", "One sentence: why, and what it is expected to do", true) },
            async (a, ctx, ct) =>
            {
                var t = TerminalBridge.Target;
                if (TerminalBridge.Problem(t, out var view) is { } why) return "REFUSED: " + why;
                var cmd = S(a, "command");
                if (cmd.Trim().Length == 0) return "ERROR: empty command";
                if (!TerminalBridge.ReviewOrAuto(t!, view!, cmd, S(a, "purpose"), ctx.Actor, out var risk, out var auto)) return "User declined to run this command.";
                var r = await TerminalBridge.RunAsync(t!, cmd, ctx.Actor, risk, ct);
                return r.Sent ? (auto ? "(ran automatically: read-only)\n" : "") + TerminalBridge.ForAi(t!, cmd, r) : "NOT SENT: " + r.Message;
            }, Module: "ai"));

        // Direct control of the chosen session (ADR-034): read the screen, send keys, interrupt, switch target, open a session.
        Capabilities.Register(new Capability("terminal_read_screen", "Read what the target terminal session shows now (the visible rows), e.g. after terminal_send_keys or to see a prompt. Only the session the user chose. Asks first unless auto-run of read-only commands is on.", CapabilityRisk.Private,
            new[] { ("lines", "integer", "How many of the last rows (5-200, default 60)", false) },
            async (a, ctx, _) =>
            {
                var t = TerminalBridge.Target;
                if (TerminalBridge.Problem(t, out var view) is { } why) return "REFUSED: " + why;
                if (!AppSettings.Current.AiAutoRunReadOnlyTarget && !await ctx.Approve("AI wants to read a terminal screen", $"Target: {t!.Describe()}\n\nThe visible text (masked) is sent to the AI.", view!.Host)) return "User declined.";
                return await TerminalBridge.ReadScreenAsync(t!, view!, int.TryParse(S(a, "lines", "60"), out var n) ? n : 60, ctx.Actor);
            }, Module: "ai"));

        Capabilities.Register(new Capability("terminal_send_keys", "Type keys into the target session: answers to a prompt, a pager or a TUI (q, y{ENTER}, {CTRL+C}, {UP}, {TAB}). Special keys: {ENTER} {TAB} {ESC} {UP} {DOWN} {LEFT} {RIGHT} {BACKSPACE} {HOME} {END} {PGUP} {PGDN} {DELETE} {SPACE} {CTRL+A}…{CTRL+Z}. The user reviews the keys first; text ending in {ENTER} is judged like a command. Prefer terminal_run for commands.", CapabilityRisk.Change,
            new[] { ("keys", "string", "The keys, e.g. \"q\" or \"yes{ENTER}\"", true), ("purpose", "string", "Why", true) },
            (a, ctx, _) =>
            {
                var t = TerminalBridge.Target;
                if (TerminalBridge.Problem(t, out var view) is { } why) return Task.FromResult("REFUSED: " + why);
                var spec = S(a, "keys");
                if (TerminalBridge.Keys(spec, out var err) is not { Length: > 0 } keys) return Task.FromResult("ERROR: " + (err ?? "no keys (or more than 4000)"));
                // A line ended with Enter is a command: the same risk review (and typed confirmation) as terminal_run.
                var line = string.Join("\n", spec.Split("{ENTER}", StringSplitOptions.None).SkipLast(1).Select(l => Regex.Replace(l, @"\{[A-Za-z+]+\}", "")).Where(l => l.Trim().Length > 0));
                var risk = line.Length > 0 ? CommandRisk.Assess(line) : new RiskAssessment(RiskLevel.Change, new[] { "keystrokes into a running program" }, false, false);
                var text = TerminalBridge.ReviewText(t!, TerminalBridge.ShowKeys(spec), risk, S(a, "purpose")).Replace("The exact text that will be typed into this terminal:", "The exact keys that will be sent ({…} = a special key):");
                bool prod = Envs.IsProduction(t!.EnvLabel);
                string? type = risk.Level switch { RiskLevel.Destructive => t.Title, RiskLevel.Sensitive => "RUN", RiskLevel.Change when prod => t.Title, _ => null };
                if (!TerminalBridge.Confirm("The AI wants to send keys to a terminal", text, risk.Level >= RiskLevel.Change || prod, type, view!.Host)) return Task.FromResult("User declined.");
                return Task.FromResult(TerminalBridge.SendKeys(t, view, keys, spec, ctx.Actor));
            }, Module: "ai"));

        Capabilities.Register(new Capability("terminal_interrupt", "Press Ctrl+C in the target session (stops the AI's own running command at once; anything else asks the user first).", CapabilityRisk.Change,
            Array.Empty<(string, string, string, bool)>(), (_, ctx, _) =>
            {
                var t = TerminalBridge.Target;
                if (TerminalBridge.Problem(t, out var view) is { } why && !TerminalBridge.Busy) return Task.FromResult("REFUSED: " + why);
                if (view == null) { TerminalBridge.Interrupt(); return Task.FromResult("Ctrl+C sent to the running AI command."); }
                return Task.FromResult(TerminalBridge.InterruptTarget(t!, view, ctx.Actor));
            }, Module: "ai"));

        Capabilities.Register(new Capability("terminal_switch_target", "Ask the user to make another open session (from list_terminal_sessions) the target. Nothing changes unless they say yes.", CapabilityRisk.Change,
            new[] { ("session_id", "string", "Session id", true), ("reason", "string", "Why that session", true) },
            (a, ctx, _) => Task.FromResult(TerminalBridge.SwitchTarget(S(a, "session_id"), S(a, "reason"), ctx.Actor)), Module: "ai"));

        Capabilities.Register(new Capability("terminal_open_session", "Ask the user to open a new SSH session to a saved host and make it the target. Nothing opens unless they say yes; it waits up to 30 s for the connection.", CapabilityRisk.Change,
            new[] { ("host", "string", "Saved host name or address", true), ("reason", "string", "Why", true) },
            async (a, ctx, ct) =>
            {
                if (!AppSettings.Current.AiTerminalBridge) return "REFUSED: running AI commands in terminals is switched off (Settings → AI & models → AI permissions & safety).";
                var h = HostNamed(S(a, "host"));
                if (!h.IsSsh) return $"REFUSED: {h.Display} isn't an SSH host.";
                if (!await ctx.Approve("AI asks to open a terminal session", $"Open an SSH session to {h.Display} ({h.Endpoint}) and make it the AI's terminal target?\n\nWhy: {Short(S(a, "reason"), 300)}\n\nCommands still follow your review settings.", h)) return "User declined.";
                var main = MainWindow.Instance ?? throw new InvalidOperationException("no main window");
                var before = TerminalBridge.Candidates().Select(v => v.SessionId).ToHashSet();
                var known = main.Panes.AllTabs.Select(t => t.Content).OfType<TerminalView>().Select(v => v.SessionId).ToHashSet();
                main.OpenTerminal(h);
                var tv = main.Panes.AllTabs.Select(t => t.Content).OfType<TerminalView>().FirstOrDefault(v => !known.Contains(v.SessionId));
                if (tv == null) return "ERROR: the session didn't open.";
                for (int i = 0; i < 120 && !tv.IsConnected && !tv.IsDisposed; i++) await Task.Delay(250, ct);
                if (!tv.IsConnected) return $"Opened a tab for {h.Display}, but it isn't connected yet ({tv.DiagStatus.Split('\'')[1]}). Ask the user to check it; it is not the target.";
                TerminalBridge.Choose(tv);
                AuditLog.Write(ctx.Actor, "ai", "terminal session opened and chosen as target at the AI's request (approved by you)", TerminalBridge.Target!.Describe());
                return "Opened and chosen as target: " + TerminalBridge.Target!.Describe();
            }, Module: "ai"));

        // Notes, incidents and documents: off unless the user allows it; every read asks; results cite their source.
        Capabilities.Register(new Capability("search_notes", "Search the user's encrypted notes (words, tag:, host:, dates). Returns titles, ids, dates and short snippets. Only works when the user allowed AI access to notes; asks each time.", CapabilityRisk.Private,
            new[] { ("query", "string", "Search text, e.g. 'nginx tag:runbook' or 'host:web-01'", true) },
            async (a, ctx, _) =>
            {
                if (!AppSettings.Current.AiAllowNotes) return "REFUSED: AI access to notes is off (Settings → AI & models → AI permissions & safety).";
                var q = S(a, "query");
                if (!await ctx.Approve("AI wants to search your notes", $"Search: {q}\n\nTitles, dates and short snippets (masked) of matching notes are sent to the AI.", null)) return "User declined.";
                var hits = Notes.NoteStore.Search(q, 20).Where(h => !Locks.Hidden("note:" + h.Note.Id)).ToList();   // closed padlocks: not searched
                AuditLog.Write(ctx.Actor, "notes", "searched notes (approved)", q);
                return hits.Count == 0 ? "No notes match." : Untrusted("notes search",
                    string.Join("\n", hits.Select(h => $"[note {h.Note.Id}] “{h.Note.Title}” · updated {h.Note.Updated:yyyy-MM-dd} · tags {string.Join(",", h.Note.Tags)}\n  {h.Snippet}")), 6000);
            }));

        Capabilities.Register(new Capability("read_note", "Read one note (by id from search_notes). Asks first. Cite it as [note <id>] when you use it.", CapabilityRisk.Private,
            new[] { ("note_id", "string", "Note id", true) },
            async (a, ctx, _) =>
            {
                if (!AppSettings.Current.AiAllowNotes) return "REFUSED: AI access to notes is off (Settings → AI & models → AI permissions & safety).";
                var n = Notes.NoteStore.Find(S(a, "note_id"));
                if (n == null || n.Deleted != null) return "No such note.";
                Locks.Demand("note:" + n.Id, n.Title);
                if (!await ctx.Approve("AI wants to read a note", $"“{n.Title}” (updated {n.Updated:yyyy-MM-dd HH:mm}, {n.Body.Length:N0} characters)\n\nIts text is masked and sent to the AI.", null)) return "User declined.";
                AuditLog.Write(ctx.Actor, "notes", "note read by the AI (approved)", n.Title);
                return $"Source: [note {n.Id}] “{n.Title}”, version of {n.Updated:yyyy-MM-dd HH:mm}, tags {string.Join(",", n.Tags)}\n" + Untrusted($"note {n.Id}", n.Body);
            }));

        Capabilities.Register(new Capability("list_incidents", "List incidents (title, id, status, severity, environment, dates). Titles only, no content. Needs AI access to notes and incidents.", CapabilityRisk.Private,
            Array.Empty<(string, string, string, bool)>(), (_, _, _) => Task.FromResult(!AppSettings.Current.AiAllowNotes ? "REFUSED: AI access to notes and incidents is off." :
                Untrusted("incidents", string.Join("\n", Incidents.IncidentStore.All.Take(50).Select(i => $"[incident {i.Id}] {i.Title} · {i.Status} · {Incidents.IncidentStore.SeverityText(i.Severity)} · {i.Environment} · updated {i.Updated:yyyy-MM-dd}")), 6000))));

        Capabilities.Register(new Capability("read_incident", "Read one incident's timeline as Markdown (by id). Asks first. Cite it as [incident <id>].", CapabilityRisk.Private,
            new[] { ("incident_id", "string", "Incident id", true) },
            async (a, ctx, _) =>
            {
                if (!AppSettings.Current.AiAllowNotes) return "REFUSED: AI access to notes and incidents is off.";
                var i = Incidents.IncidentStore.Find(S(a, "incident_id"));
                if (i == null || i.Deleted != null) return "No such incident.";
                if (!await ctx.Approve("AI wants to read an incident", $"“{i.Title}” ({i.Status})\n\nIts timeline (masked) is sent to the AI.", null)) return "User declined.";
                AuditLog.Write(ctx.Actor, "incidents", "incident read by the AI (approved)", i.Title);
                return $"Source: [incident {i.Id}] “{i.Title}”, updated {i.Updated:yyyy-MM-dd HH:mm}\n" + Untrusted($"incident {i.Id}", Incidents.IncidentStore.Markdown(i));
            }));

        Capabilities.Register(new Capability("read_pdf", "Read the text of a PDF on this PC or a saved host (pages with markers). Asks first. Scanned PDFs without text are reported as such.", CapabilityRisk.Private,
            new[] { ("host", "string", "Saved host name, or 'local' for this PC", true), ("path", "string", "Path of the PDF", true), ("from_page", "integer", "First page (default 1)", false), ("to_page", "integer", "Last page (default 20)", false) },
            async (a, ctx, ct) =>
            {
                var host = S(a, "host").Trim();
                Files.FsRef r = host.Length == 0 || host.Equals("local", StringComparison.OrdinalIgnoreCase) ? Files.FsRef.Local
                    : Files.FsRef.ForHost(HostNamed(host));
                var path = S(a, "path");
                if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return "ERROR: not a .pdf path";
                int from = int.TryParse(S(a, "from_page", "1"), out var f) ? Math.Max(1, f) : 1, to = int.TryParse(S(a, "to_page", "20"), out var t2) ? Math.Clamp(t2, from, from + 49) : from + 19;
                if (!await ctx.Approve("AI wants to read a PDF", $"{(r.Kind == "local" ? "This PC" : host)}:{path}\npages {from}–{to}, text masked", r.Host)) return "User declined.";
                using var fs = r.Open();
                await using var s = await fs.OpenReadAsync(path, ct);
                using var ms = new System.IO.MemoryStream();
                await s.CopyToAsync(ms, ct);
                var x = await Documents.PdfText.ExtractAsync(ms.ToArray(), ct);
                return $"Source: {path} — {x.Label}\n" + (x.HasText ? Untrusted($"pdf {path}", x.Text(from, to)) : "(no text to read)");
            }));

        Capabilities.Register(new Capability("create_note", "Create an encrypted note (e.g. a runbook or summary built from real data). The user sees the full text and approves it. Tagged 'ai'.", CapabilityRisk.Change,
            new[] { ("title", "string", "Note title", true), ("body", "string", "Note text (Markdown)", true), ("host", "string", "Optional saved host to link it to", false) },
            async (a, ctx, _) =>
            {
                var title = S(a, "title").Trim(); var body = S(a, "body");
                if (title.Length == 0 || body.Trim().Length == 0) return "ERROR: title and body are required";
                var h = S(a, "host") is { Length: > 0 } hn ? AppData.Current.FindHostByName(hn) : null;
                if (!await ctx.Approve("AI wants to create a note", $"Title: {title}{(h != null ? $"\nLinked to: {h.Display}" : "")}\n\n{Short(body, 4000)}", h)) return "User declined.";
                var n = Notes.NoteStore.Create(title, h != null ? new[] { h.Id } : null, $"[{DateTime.Now:yyyy-MM-dd HH:mm}] Written by the AI ({Assist.Current?.ModelName ?? "assistant"}) and approved by you.\n\n{body}");
                n.Tags.Add("ai"); Notes.NoteStore.Save(n);
                AuditLog.Write(ctx.Actor, "notes", "note created by the AI (approved)", title);
                return $"Created note [note {n.Id}] “{title}”.";
            }));

        RegisterIntegrations();
    }

    // ---- more of the app as tools (ADR-034) -----------------------------------------------------------------------------

    private static string Clean(string label) => label.Trim().TrimStart('▶', '↻', '■', '⏻', '⟲', '✎', '＋', '⚙', ' ').Trim();

    private static HostEntry? LinkedHost(CloudProvider p, string id) =>
        AppData.Current.Hosts.FirstOrDefault(h => CloudProviders.ForHost(h) is { } l && l.Provider == p && l.InstanceId == id);

    private static void RegisterIntegrations()
    {
        // Infrastructure: what can be done to an object right now (the 0.1.14 provider-coverage actions included).
        Capabilities.Register(new Capability("infra_actions", "List the actions possible on one infrastructure object right now (start, snapshots, clone, migrate, backup, pause…), each with whether it is allowed now and the inputs it needs. Use before infra_action.", CapabilityRisk.Read,
            new[] { ("provider", "string", "Provider name", true), ("object_id", "string", "Object id from infra_inventory", true) },
            async (a, _, ct) =>
            {
                var c = ProviderArg(a);
                var p = Infra.Infra.Create(c);
                try
                {
                    var caps = await p.DetectAsync(ct);
                    if (!caps.Reachable) return $"Not reachable: {caps.Detail}";
                    var o = (await p.InventoryAsync(ct)).FirstOrDefault(x => x.Id == S(a, "object_id")) ?? throw new InvalidOperationException("No such object; use infra_inventory for ids.");
                    var offers = Infra.Infra.Offer(p, o, caps).ToList();
                    if (offers.Count == 0) return $"No actions for this {o.Kind}.";
                    return $"{c.Name} · {o.Kind} {o.Name} ({o.Status}) · changes {(c.AllowChanges ? "allowed (each one still asks)" : "NOT allowed: read-only provider")}\n" + string.Join("\n", offers.Select(x =>
                        $"{x.Action.Id}: {x.Action.Label.Trim()}{(x.Enabled ? "" : " — not now: " + x.Why)}{(x.Action.Inputs.Count > 0 ? " · inputs: " + string.Join(", ", x.Action.Inputs.Select(i => $"{i.Name}{(i.Required ? "" : "?")}{(i.Options is { Length: > 0 } op ? " [" + string.Join("|", op) + "]" : "")}")) : "")}"));
                }
                finally { (p as IDisposable)?.Dispose(); }
            }));

        // Cloud screens: any catalog action of a resource (not deletes, consoles or anything that needs a typed name or a password).
        Capabilities.Register(new Capability("cloud_action", "Run an action from a cloud provider's screen (" + CloudIds(", ") + ") on one item, e.g. attach a volume, enable backups, create a snapshot, set reverse DNS. The user approves the exact request first. Call with an unknown action to get the list for that resource. Deletes, consoles, anything that needs a typed name or a password stay on the screen.", CapabilityRisk.Change,
            new[] { ("provider", "string", CloudIds("|"), true), ("resource", "string", "Screen title, e.g. Instances, Volumes", true), ("action", "string", "Action label as listed", true), ("item_id", "string", "Item id (from cloud_list) unless the action works without one", false), ("inputs", "object", "Field values by name", false) },
            async (a, ctx, ct) =>
            {
                var p = CloudProviders.Find(S(a, "provider").ToLowerInvariant()) ?? throw new InvalidOperationException("provider must be one of " + string.Join(", ", CloudProviders.All.Select(x => x.Id)));
                var def = p.Catalog.FirstOrDefault(d => d.Title.Equals(S(a, "resource"), StringComparison.OrdinalIgnoreCase) && !d.IsChild);
                if (def == null) return $"ERROR: {p.Title} resources: {string.Join(", ", p.Catalog.Where(d => !d.IsChild).Select(d => d.Title))}";
                var usable = def.Actions.Where(x => x.Special == null && x.TypeToConfirm == null && x.Fields?.Any(f => f.Kind == Cloud.FieldKind.Password) != true).ToList();
                var act = usable.FirstOrDefault(x => Clean(x.Label).Equals(Clean(S(a, "action")), StringComparison.OrdinalIgnoreCase));
                if (act == null)
                    return $"ERROR: “{S(a, "action")}” isn't an action the AI can run on {def.Title}. Available: " +
                           (usable.Count == 0 ? "none" : string.Join("; ", usable.Select(x => Clean(x.Label) + (x.NoRow ? " (no item)" : "") + (x.Fields is { Length: > 0 } fs ? " — inputs: " + string.Join(", ", fs.Select(f => f.Name + (f.Required ? "" : "?") + (f.Options is { Length: > 0 } o ? "[" + string.Join("|", o) + "]" : ""))) : ""))));
                var api = p.Require();
                System.Text.Json.JsonElement? raw = null;
                string itemName = def.Title, id = S(a, "item_id");
                if (!act.NoRow)
                {
                    var rows = await api.ListAsync(def.Path, def.Key, ct);
                    var row = rows.FirstOrDefault(r => J.Str(r, def.IdField) == id);
                    if (row.ValueKind == System.Text.Json.JsonValueKind.Undefined) return $"ERROR: no {def.Title} item with id {id} on account {api.AccountName}.";
                    if (!def.Allows(act.When, row)) return $"REFUSED: not possible while it is {J.Str(row, def.StatusField ?? "status")}.";
                    raw = row;
                    itemName = J.Str(row, def.NameField) is { Length: > 0 } nm ? nm : id;
                }
                var inputs = a["inputs"] as JsonObject ?? new JsonObject();
                var obj = new JsonObject();
                foreach (var f in act.Fields ?? Array.Empty<Cloud.Field>())
                {
                    var given = inputs[f.Name] is JsonValue gv ? gv.ToString() : inputs[f.Name]?.ToJsonString();
                    var v = given ?? (f.Default != null ? Cloud.Tpl.Fill(f.Default, raw, null) : null);
                    if (string.IsNullOrEmpty(v)) { if (f.Required) return $"ERROR: input “{f.Name}” ({f.Label}) is required."; continue; }
                    if (f.Options is { Length: > 0 } opts && f.Kind is Cloud.FieldKind.Select or Cloud.FieldKind.IntSelect && !opts.Contains(v)) return $"ERROR: {f.Name} must be one of {string.Join(", ", opts)}";
                    try
                    {
                        J.SetPath(obj, f.Name, f.Kind switch
                        {
                            Cloud.FieldKind.Int or Cloud.FieldKind.IntSelect => JsonValue.Create(long.Parse(v)),
                            Cloud.FieldKind.Bool => JsonValue.Create(v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1"),
                            Cloud.FieldKind.List or Cloud.FieldKind.MultiLookup => new JsonArray(v.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => (JsonNode)JsonValue.Create(x.Trim('"'))!).ToArray()),
                            Cloud.FieldKind.Json => JsonNode.Parse(v),
                            Cloud.FieldKind.Base64 => JsonValue.Create(Convert.ToBase64String(Encoding.UTF8.GetBytes(v))),
                            _ => JsonValue.Create(v),
                        });
                    }
                    catch (Exception ex) when (ex is FormatException or OverflowException or System.Text.Json.JsonException) { return $"ERROR: “{f.Name}” has the wrong format ({f.Kind})."; }
                }
                foreach (var (k, t) in act.Fixed ?? new()) J.SetPath(obj, k, Cloud.Tpl.Fill(t, raw, null));
                var form = (JsonObject)obj.DeepClone();
                foreach (var f in act.Fields?.Where(f => f.InPath) ?? Enumerable.Empty<Cloud.Field>()) obj.Remove(f.Name);
                string? body = obj.Count > 0 || act.Fields?.Any(f => !f.InPath) == true || act.Fixed != null ? obj.ToJsonString() : null;
                var path = Cloud.Tpl.Fill(act.Path, raw, null, url: true, form: form);
                var text = (act.Confirm != null ? Cloud.Tpl.Fill(act.Confirm, raw, null, form: form) + "\n\n" : "") +
                           $"{p.Title} · account {api.AccountName} · {def.Title}: {itemName}\n{act.Method} {path}" + (body != null ? "\n" + Short(Redaction.Mask(body), 1500) : "");
                if (!await ctx.Approve($"AI wants to: {Clean(act.Label)} — {itemName}", text, raw != null && def.IsInstance ? LinkedHost(p, id) : null)) return "User declined.";
                var r = await api.SendAsync(new System.Net.Http.HttpMethod(act.Method), path, body, ct, ctx.Actor, act.Idempotent);
                AuditLog.Write(ctx.Actor, $"{p.Id}:{itemName}", $"{Clean(act.Label)} on {def.Title} (AI, approved)", $"{act.Method} {path}");
                return $"Done: {Clean(act.Label)} on {itemName}.\n" + Untrusted($"{p.Id}:{def.Title} result", J.Pretty(r), 4000);
            }));

        // Notes and incidents: write with approval (reads are above).
        Capabilities.Register(new Capability("append_to_note", "Add text to the end of an existing note (by id). The user sees the full text and approves it; the previous version is kept in the note's history.", CapabilityRisk.Change,
            new[] { ("note_id", "string", "Note id", true), ("text", "string", "Text to add (Markdown)", true) },
            async (a, ctx, _) =>
            {
                var n = Notes.NoteStore.Find(S(a, "note_id"));
                if (n == null || n.Deleted != null) return "No such note.";
                Locks.Demand("note:" + n.Id, n.Title);
                var add = S(a, "text").Trim();
                if (add.Length == 0) return "ERROR: empty text";
                if (!await ctx.Approve("AI wants to add to a note", $"Note: “{n.Title}”\n\nAdded at the end:\n\n{Short(add, 4000)}", null)) return "User declined.";
                var prev = n.Body;
                n.Body = prev.TrimEnd() + $"\n\n[{DateTime.Now:yyyy-MM-dd HH:mm}] Added by the AI ({Assist.Current?.ModelName ?? "assistant"}), approved by you:\n{add}";
                Notes.NoteStore.Save(n, n.Title, prev, snapshot: true);
                AuditLog.Write(ctx.Actor, "notes", "text added to a note by the AI (approved)", n.Title);
                return $"Added to [note {n.Id}] “{n.Title}”.";
            }));

        Capabilities.Register(new Capability("create_incident", "Open a new incident (title, severity sev1-sev4, optional linked host). The user approves it first.", CapabilityRisk.Change,
            new[] { ("title", "string", "Incident title", true), ("severity", "string", "sev1|sev2|sev3|sev4 (default sev3)", false), ("host", "string", "Optional saved host", false), ("summary", "string", "First timeline entry (optional)", false) },
            async (a, ctx, _) =>
            {
                var title = S(a, "title").Trim();
                if (title.Length == 0) return "ERROR: title is required";
                var sev = S(a, "severity").ToLowerInvariant() switch { "sev1" or "1" => Incidents.IncidentSeverity.Sev1, "sev2" or "2" => Incidents.IncidentSeverity.Sev2, "sev4" or "4" => Incidents.IncidentSeverity.Sev4, _ => Incidents.IncidentSeverity.Sev3 };
                var h = S(a, "host") is { Length: > 0 } hn ? AppData.Current.FindHostByName(hn) : null;
                var summary = S(a, "summary").Trim();
                if (!await ctx.Approve("AI wants to open an incident", $"Title: {title}\nSeverity: {Incidents.IncidentStore.SeverityText(sev)}{(h != null ? $"\nHost: {h.Display}" : "")}" + (summary.Length > 0 ? $"\n\nFirst entry:\n{Short(summary, 3000)}" : ""), h)) return "User declined.";
                var i = Incidents.IncidentStore.Create(title, h != null ? new[] { h.Id } : null, sev, h != null ? Envs.For(h) : "");
                if (summary.Length > 0) Incidents.IncidentStore.Add(i, new Incidents.IncidentItem { Kind = Incidents.ItemKind.AiAnalysis, Title = "AI summary", Text = summary, HostId = h?.Id });
                AuditLog.Write(ctx.Actor, "incidents", "incident opened by the AI (approved)", title);
                return $"Opened [incident {i.Id}] “{title}”.";
            }));

        Capabilities.Register(new Capability("add_to_incident", "Add an entry (findings, a log excerpt, a timeline event) to an incident's timeline; incident_id 'active' = the incident the user has active. Approved first.", CapabilityRisk.Change,
            new[] { ("incident_id", "string", "Incident id, or 'active'", true), ("title", "string", "Entry title", true), ("text", "string", "Entry text", true) },
            async (a, ctx, _) =>
            {
                var id = S(a, "incident_id").Trim();
                var i = id.Equals("active", StringComparison.OrdinalIgnoreCase) ? Incidents.IncidentStore.Active : Incidents.IncidentStore.Find(id);
                if (i == null || i.Deleted != null) return "No such incident (no active incident, or a wrong id: list_incidents).";
                var title = S(a, "title").Trim(); var text = S(a, "text").Trim();
                if (title.Length == 0 || text.Length == 0) return "ERROR: title and text are required";
                if (!await ctx.Approve("AI wants to add to an incident", $"Incident: “{i.Title}” ({i.Status})\nEntry: {title}\n\n{Short(text, 4000)}", null)) return "User declined.";
                Incidents.IncidentStore.Add(i, new Incidents.IncidentItem { Kind = Incidents.ItemKind.AiAnalysis, Title = "AI: " + title, Text = text });
                AuditLog.Write(ctx.Actor, "incidents", "entry added to an incident by the AI (approved)", i.Title);
                return $"Added to [incident {i.Id}] “{i.Title}”.";
            }));

        // File transfer: the queue and history (copies themselves: transfer_files, which asks).
        Capabilities.Register(new Capability("transfer_status", "The file transfer queue (running, paused, failed) and the most recent finished transfers.", CapabilityRisk.Read,
            Array.Empty<(string, string, string, bool)>(), (_, _, _) =>
            {
                var jobs = Files.TransferQueue.Jobs.ToList();
                var sb = new StringBuilder(jobs.Count == 0 ? "Queue: empty\n" : "Queue:\n");
                foreach (var j in jobs.Take(30)) sb.AppendLine($"- {j.Title} · {j.Route} · {(j.Finished ? j.Summary : $"{j.Status} {j.Percent:0}%")}");
                var hist = Files.TransferHistory.All.Take(10).ToList();
                sb.AppendLine(hist.Count == 0 ? "History: none" : "Recent:");
                foreach (var e in hist) sb.AppendLine($"- {e.Time:yyyy-MM-dd HH:mm} {(e.Result == "ok" ? "✓" : "✕ " + e.Result)} {e.Title} · {e.Route} · {e.Files} file(s), {Fmt.Bytes(e.Bytes)}");
                return Task.FromResult(Untrusted("transfers", sb.ToString(), 6000));
            }));

        // Portal browser (ADR-032, built in since 0.1.17): bookmarks and opening one — never cookies, pages or saved sign-ins.
        Capabilities.Register(new Capability("portal_list", "List the web-UI bookmarks of the portal browser (title, address, folder, environment, linked host). Never page content or sign-ins.", CapabilityRisk.Read,
            Array.Empty<(string, string, string, bool)>(), (_, _, _) => Task.FromResult(!Browser.PortalBrowser.Enabled ? Browser.PortalBrowser.OffText :
                Untrusted("portal bookmarks", string.Join("\n", Browser.Portals.All.Take(MaxRows).Select(b => PortalUi.ClosedTarget(b) != null ? $"[{b.Id}] {b.Title} · 🔒 locked" :
                    $"[{b.Id}] {b.Title} · {Browser.Portals.Display(b.Url)} · {(b.Folder.Length > 0 ? b.Folder : "-")} · {Browser.Portals.EnvironmentOf(b)}{(b.HostId != null && AppData.Current.FindHost(b.HostId) is { } h ? " · host " + h.Display : "")}")), 6000))));

        Capabilities.Register(new Capability("portal_open", "Open a portal bookmark (by id from portal_list) in a browser tab for the user. Asks first. The AI can't see or use the page.", CapabilityRisk.Change,
            new[] { ("bookmark_id", "string", "Bookmark id", true) },
            async (a, ctx, _) =>
            {
                if (!Browser.PortalBrowser.Enabled) return Browser.PortalBrowser.OffText;
                var b = Browser.Portals.Find(S(a, "bookmark_id"));
                if (b == null) return "No such bookmark (portal_list).";
                if (PortalUi.ClosedTarget(b) is { } locked) Locks.Demand(locked, b.Title);
                if (!await ctx.Approve("AI wants to open a web UI", $"{b.Title}\n{Browser.Portals.Display(b.Url)} · {Browser.Portals.EnvironmentOf(b)}\n\nIt opens in a browser tab for you; the AI doesn't see the page.", b.HostId != null ? AppData.Current.FindHost(b.HostId) : null)) return "User declined.";
                PortalUi.Open(b);
                AuditLog.Write(ctx.Actor, "portals", "web UI opened at the AI's request (approved)", b.Title);
                return $"Opened “{b.Title}” for the user.";
            }));

        // Shared library add-on (ADR-033): which libraries and shared hosts exist — never credentials or keys.
        Capabilities.Register(new Capability("library_list", "List the shared libraries (team hosts and credentials on a file share): name, last sync, and their shared hosts (name, address, group). Credentials are never listed.", CapabilityRisk.Read,
            Array.Empty<(string, string, string, bool)>(), (_, _, _) =>
            {
                var clients = Library.SharedLibraries.Clients;
                if (clients.Count == 0) return Task.FromResult("No shared library is set up.");
                var sb = new StringBuilder();
                foreach (var c in clients)
                {
                    sb.AppendLine($"Library “{c.State.Name}” · last sync {(c.State.LastSync is { } ls ? ls.ToString("yyyy-MM-dd HH:mm") : "never")}");
                    var prefix = Library.SharedLibraries.ItemId(c.State.LibraryId, "");
                    foreach (var h in Library.SharedLibraries.Hosts.Where(h => h.Id.StartsWith(prefix)).Take(MaxRows)) sb.AppendLine($"  ⇄ {h.Display} · {h.Endpoint} · {h.Group}");
                }
                return Task.FromResult(Untrusted("shared libraries", sb.ToString(), 6000));
            }));
    }
}
