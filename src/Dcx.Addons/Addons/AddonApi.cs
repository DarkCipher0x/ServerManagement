using System.Text.Json.Nodes;
using DcxManager.Services;

namespace DcxManager.Addons;

/// <summary>One add-on bridge method beyond the fixed ones in <see cref="AddonHost"/>: its permission, a description for
/// app.capabilities, and the handler (UI thread). Features add theirs with <see cref="AddonApi.Register"/>.</summary>
public sealed record AddonMethod(string Name, string Permission, string Description, Func<InstalledAddon, JsonObject, AddonContext, CancellationToken, Task<JsonNode?>> Run);

/// <summary>
/// The full-control add-on API (ADR-036, s40). DarkCipher0x: "I never want to need an urgent add-on and find the functionality
/// isn't there." Everything goes over the encrypted API 2 bridge (ADR-035); nothing here is reachable any other way.
/// <list type="bullet">
/// <item><b>app.capabilities</b> lists every bridge method and every app capability, with its risk, the permission it
/// needs and whether the add-on has it.</item>
/// <item><b>app.call</b> runs any capability of the app's shared registry: the AI operator's built-in tools (SSH commands,
/// services, logs, files and transfers, cloud instances) and every <see cref="Capabilities"/> tool (infrastructure, cloud
/// screens, terminal target, notes, incidents, transfers, portals, libraries). A feature that registers a tool is
/// reachable by add-ons at once, with no change here.</item>
/// <item><b>Registered methods</b> (<see cref="Register"/>): add-on-only abilities such as host management and UI control.</item>
/// </list>
/// Safeguards are the app's own, never weaker than for the AI: reads need app.read (private ones app.private and ask each
/// time), every change needs app.control AND the user's approval of the exact action (PRODUCTION asks for the name), and
/// every call is audited with the add-on's id.
/// </summary>
public static class AddonApi
{
    /// <summary>Every capability carries its risk (0.2.0 phase 2: the operator's built-ins are registry entries too).</summary>
    public static CapabilityRisk? RiskOf(string tool) => Capabilities.Find(tool)?.Risk;

    public static string PermissionFor(CapabilityRisk r) => r switch { CapabilityRisk.Read => "app.read", CapabilityRisk.Private => "app.private", _ => "app.control" };

    /// <summary>The approval of one action for an add-on (tests replace it): add-on, title, exact details, host → allowed?</summary>
    internal static Func<InstalledAddon, string, string, HostEntry?, Task<bool>> Approver = (a, title, detail, host) =>
        Task.FromResult(Envs.Confirm(host, $"{a.Manifest.Name}: {title}",
            $"{detail}\n\nThe add-on “{a.Manifest.Name}” ({a.Manifest.Publisher}, {a.Manifest.Version}) asks to do this. Allow it?", "Allow", danger: true));

    private static readonly List<AddonMethod> Methods = new();
    private static bool _builtIns;

    /// <summary>Adds an add-on bridge method (a name can only be registered once; the fixed methods can't be replaced).</summary>
    public static void Register(AddonMethod m)
    {
        lock (Methods)
        {
            if (!_builtIns) { _builtIns = true; RegisterBuiltIns(); }
            if (Methods.All(x => x.Name != m.Name) && !Fixed.Any(f => f.Name == m.Name)) Methods.Add(m);
        }
    }

    public static AddonMethod? Find(string name) { lock (Methods) { if (!_builtIns) { _builtIns = true; RegisterBuiltIns(); } return Methods.FirstOrDefault(m => m.Name == name); } }

    private static IReadOnlyList<AddonMethod> AllMethods { get { lock (Methods) { if (!_builtIns) { _builtIns = true; RegisterBuiltIns(); } return Methods.ToList(); } } }

    /// <summary>The fixed methods of <see cref="AddonHost"/> (for app.capabilities).</summary>
    public static readonly (string Name, string Permission, string Description)[] Fixed =
    {
        ("app.info", "", "App version, this add-on's id and its granted permissions"),
        ("app.capabilities", "", "Everything an add-on can call: bridge methods and app capabilities, with risk, permission and whether it is granted"),
        ("app.call", "app.read / app.private / app.control", "Run an app capability: {\"tool\":\"<name>\",\"args\":{…}} — see app.capabilities"),
        ("ui.notify", "", "Show a line in the status bar: {\"text\"}"),
        ("hosts.list", "hosts.read", "Saved hosts (never passwords or keys); shared library hosts too with library.read"),
        ("incidents.active", "incidents.read", "The active incident"),
        ("incidents.list", "incidents.read", "All incidents"),
        ("incidents.addEvent", "incidents.write", "Add an event: {\"title\",\"text\",\"kind\":\"event|log\",\"incident\"}"),
        ("notes.search", "notes.read", "Search notes: {\"query\"}"),
        ("notes.create", "notes.write", "Create a note: {\"title\",\"body\",\"hostId\"}"),
        ("terminal.screen", "terminal.read", "The screen of the terminal the command was run from (secrets masked)"),
        ("files.pick", "filesystem.read", "Ask the user to pick a file; returns its text (≤ 2 MB)"),
        ("credentials.request", "credentials.request", "Ask the user for a credential: {\"reason\"} (a dialog every time)"),
    };

    // ---- app.capabilities / app.call ----------------------------------------------------------------------------------

    /// <summary>The app.capabilities answer: bridge methods and every app capability with risk, permission and grant.</summary>
    public static JsonNode CapabilityList(InstalledAddon a)
    {
        var methods = new JsonArray();
        foreach (var f in Fixed) methods.Add(new JsonObject { ["name"] = f.Name, ["permission"] = f.Permission, ["description"] = f.Description, ["granted"] = f.Permission.Length == 0 || f.Permission.Contains('/') || a.Granted.Contains(f.Permission) });
        foreach (var m in AllMethods) methods.Add(new JsonObject { ["name"] = m.Name, ["permission"] = m.Permission, ["description"] = m.Description, ["granted"] = a.Granted.Contains(m.Permission) });
        var tools = new JsonArray();
        foreach (var def in Capabilities.Schema().OfType<JsonObject>())
        {
            var fn = def["function"] as JsonObject;
            var name = fn?["name"]?.GetValue<string>() ?? "";
            if (RiskOf(name) is not { } risk) continue;
            var perm = PermissionFor(risk);
            tools.Add(new JsonObject
            {
                ["name"] = name, ["description"] = fn!["description"]?.DeepClone(), ["parameters"] = fn["parameters"]?.DeepClone(),
                ["risk"] = risk switch { CapabilityRisk.Read => "read", CapabilityRisk.Private => "private (asks each time)", _ => "change (you approve each one)" },
                ["permission"] = perm, ["granted"] = a.Granted.Contains(perm),
            });
        }
        return new JsonObject { ["api"] = 2, ["encrypted"] = true, ["methods"] = methods, ["tools"] = tools };
    }

    /// <summary>Runs one app capability for an add-on through the same registry and code path as the AI (same checks, same
    /// approvals), with the add-on named in every approval and as the actor in the audit log.</summary>
    public static async Task<JsonNode?> CallAsync(InstalledAddon a, JsonObject p, List<string> log, CancellationToken ct)
    {
        var tool = p["tool"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t.Trim() : "";
        if (tool.Length == 0 || tool.Length > 80) throw new AddonHost.AddonError(-32602, "\"tool\" is required (see app.capabilities)");
        if (RiskOf(tool) is not { } risk)
            throw new AddonHost.AddonError(-32601, Capabilities.UnavailableBecause(tool) is { } m
                ? $"capability unavailable ({m.ToUpperInvariant()} off): {tool} (see app.capabilities)"
                : "unknown capability " + tool + " (see app.capabilities)");
        var perm = PermissionFor(risk);
        if (!a.Granted.Contains(perm)) throw new AddonHost.AddonError(-32001, $"permission denied: {perm}" + (a.Manifest.Permissions.Contains(perm) ? " (not granted by the user)" : " (not requested in addon.json)"));
        var args = p["args"] switch { null => new JsonObject(), JsonObject o => (JsonObject)o.DeepClone(), _ => throw new AddonHost.AddonError(-32602, "\"args\" must be an object") };
        if (args.ToJsonString().Length > 200_000) throw new AddonHost.AddonError(-32602, "\"args\" is larger than 200 KB");
        AuditLog.Write("addon:" + a.Id, "addons", $"add-on called {tool} ({risk})", Redaction.Mask(Capabilities.BriefInline(args)));
        var ctx = new CapabilityContext((title, detail, host) => Approver(a, System.Text.RegularExpressions.Regex.Replace(title, @"^AI\b:?\s*", ""), detail, host), "addon:" + a.Id);
        string result;
        try { result = await Capabilities.RunAsync(tool, args, ctx, ct); }
        catch (OperationCanceledException) { throw new AddonHost.AddonError(-32000, "cancelled"); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException or System.Net.Http.HttpRequestException)
        { throw new AddonHost.AddonError(-32000, ex.Message); }
        catch (UnauthorizedAccessException ex) { throw new AddonHost.AddonError(-32001, ex.Message); }   // a padlock or a policy restriction
        bool declined = result.StartsWith("User declined", StringComparison.Ordinal);
        log.Add($"{(declined ? "⛔" : "→")} {tool}{(declined ? ": declined" : "")}");
        if (declined) throw new AddonHost.AddonError(-32003, "the user declined");
        return new JsonObject { ["tool"] = tool, ["result"] = result };
    }

    // ---- registered methods: hosts and UI --------------------------------------------------------------------------

    private static string Str(JsonObject p, string name, int max, bool required = false)
    {
        var v = p[name];
        if (v == null) { if (required) throw new AddonHost.AddonError(-32602, $"\"{name}\" is required"); return ""; }
        if (v is not JsonValue jv || !jv.TryGetValue<string>(out var s)) throw new AddonHost.AddonError(-32602, $"\"{name}\" must be a string");
        if (s.Length > max) throw new AddonHost.AddonError(-32602, $"\"{name}\" is longer than {max:N0} characters");
        if (s.Any(c => char.IsControl(c) && c is not ('\n' or '\t'))) throw new AddonHost.AddonError(-32602, $"\"{name}\" contains control characters");
        return s.Trim();
    }

    private static HostEntry LocalHost(JsonObject p)
    {
        var id = Str(p, "hostId", 64, required: true);
        return AppData.Current.Hosts.FirstOrDefault(h => h.Id == id && !h.IsAdHoc) ?? throw new AddonHost.AddonError(-32602, "no such saved host (shared library hosts are managed in their library)");
    }

    /// <summary>A policy restriction or a padlock refuses the call (code -32001, the reason in the message).</summary>
    private static void Gate(InstalledAddon a, string action, HostEntry? h = null)
    {
        try
        {
            Restrictions.Demand(action, "addon:" + a.Id);
            if (h != null) Locks.Refuse("host:" + h.Id, "addon:" + a.Id);
        }
        catch (UnauthorizedAccessException ex) { throw new AddonHost.AddonError(-32001, ex.Message); }
    }

    /// <summary>Applies the editable fields present in <paramref name="p"/> to <paramref name="h"/>; returns "field: old → new" lines.</summary>
    private static List<string> Apply(HostEntry h, JsonObject p, bool commit = true)
    {
        var changes = new List<string>();
        void Set(string field, string old, string now, Action apply) { if (old != now) { changes.Add($"{field}: {(old.Length == 0 ? "(empty)" : old)} → {(now.Length == 0 ? "(empty)" : now)}"); if (commit) apply(); } }
        if (p.ContainsKey("name")) { var v = Str(p, "name", 80); if (v.Length == 0) throw new AddonHost.AddonError(-32602, "\"name\" can't be empty"); Set("name", h.Name, v, () => h.Name = v); }
        if (p.ContainsKey("address"))
        {
            var v = Str(p, "address", 253);
            if (v.Length == 0 || v.Any(char.IsWhiteSpace) || v.Contains('/') || v.Contains('@')) throw new AddonHost.AddonError(-32602, "\"address\" must be a host name or IP address");
            Set("address", h.Address, v, () => h.Address = v);
        }
        if (p["port"] is { } pn)
        {
            if (pn is not JsonValue pv || !pv.TryGetValue<int>(out var port) || port is < 1 or > 65535) throw new AddonHost.AddonError(-32602, "\"port\" must be 1–65535");
            Set("port", h.Port.ToString(), port.ToString(), () => h.Port = port);
        }
        if (p.ContainsKey("user")) { var v = Str(p, "user", 64); Set("user", h.Username, v, () => h.Username = v); }
        if (p.ContainsKey("folder")) { var v = AppData.NormalizeFolder(Str(p, "folder", 200)); if (v.Length == 0) v = "General"; Set("folder", h.Group, v, () => h.Group = v); }
        if (p.ContainsKey("protocol"))
        {
            var v = Str(p, "protocol", 10).ToLowerInvariant();
            if (v is not ("ssh" or "rdp")) throw new AddonHost.AddonError(-32602, "\"protocol\" must be ssh or rdp");
            Set("protocol", h.Protocol, v, () => h.Protocol = v);
        }
        if (p.ContainsKey("environment")) { var v = Str(p, "environment", 40); Set("environment", h.Environment, v, () => h.Environment = v); }
        if (p.ContainsKey("tags")) { var v = Str(p, "tags", 400); Set("tags", h.Tags, v, () => h.Tags = v); }
        if (p.ContainsKey("notes")) { var v = Str(p, "notes", 4000); Set("notes", h.Notes, v, () => h.Notes = v); }
        if (p.ContainsKey("credentialId"))
        {
            var v = Str(p, "credentialId", 64);
            if (v.Length > 0 && AppData.Current.Credentials.All(c => c.Id != v)) throw new AddonHost.AddonError(-32602, "no such credential in the vault");
            var oldName = AppData.Current.Credentials.FirstOrDefault(c => c.Id == h.CredentialId)?.Name ?? "";
            var newName = AppData.Current.Credentials.FirstOrDefault(c => c.Id == v)?.Name ?? "";
            if ((h.CredentialId ?? "") != v) { changes.Add($"sign-in credential: {(oldName.Length == 0 ? "(none)" : oldName)} → {(newName.Length == 0 ? "(none)" : newName)}"); if (commit) h.CredentialId = v.Length == 0 ? null : v; }
        }
        return changes;
    }

    /// <summary>Opens a screen by its key (from <see cref="Screens"/>), or false when no main window is open. Registered
    /// by the app (0.2.0 phase 6: the add-ons don't reference the window above them).</summary>
    public static Func<string, bool>? OpenScreen { get; set; }
    /// <summary>Opens a host in a view (server, terminal, files, commander, notes), or false when no main window is open.</summary>
    public static Func<HostEntry, string, bool>? OpenHostView { get; set; }

    private static readonly HashSet<string> Screens = new(StringComparer.Ordinal)
    {
        "dashboard", "hosts", "commander", "notes", "sessionlogs", "incidents", "infra", "cloud", "portals", "snippets", "tunnels", "serial", "lan", "toolbox", "ansible",
        "credentials", "knownhosts", "library", "apiexplorer", "aipanel", "assistant", "reports", "observe", "help", "privacy", "addons", "audit", "settings",
    };

    private static void RegisterBuiltIns()
    {
        Methods.Add(new("hosts.add", "hosts.write",
            "Save a new host (you approve it): {\"name\",\"address\",\"port\",\"user\",\"folder\",\"protocol\":\"ssh|rdp\",\"environment\",\"tags\",\"notes\",\"credentialId\"}",
            async (a, p, _, _) =>
            {
                Gate(a, "host.add");
                var h = new HostEntry();
                if (!p.ContainsKey("name") || !p.ContainsKey("address")) throw new AddonHost.AddonError(-32602, "\"name\" and \"address\" are required");
                var lines = Apply(h, p);
                if (!await Approver(a, "save a new host", "New host:\n  " + string.Join("\n  ", lines), h)) throw new AddonHost.AddonError(-32003, "the user declined");
                AppData.Current.Hosts.Add(h);
                AppData.Save();
                AuditLog.Write("addon:" + a.Id, h.Display, "host added by an add-on (approved)", string.Join("; ", lines));
                return new JsonObject { ["id"] = h.Id };
            }));
        Methods.Add(new("hosts.update", "hosts.write",
            "Change a saved host (you approve the exact changes): {\"hostId\", and any of the fields of hosts.add}",
            async (a, p, _, _) =>
            {
                var h = LocalHost(p);
                Gate(a, "host.edit", h);
                if (p.ContainsKey("folder")) Gate(a, "folder.edit");
                var lines = Apply(h, p, commit: false);   // validate and describe first; nothing changes before the yes
                if (lines.Count == 0) return new JsonObject { ["changed"] = false };
                if (!await Approver(a, $"change host {h.Display}", $"Changes to {h.Display}:\n  " + string.Join("\n  ", lines), h)) throw new AddonHost.AddonError(-32003, "the user declined");
                Apply(h, p);
                AppData.Save();
                AuditLog.Write("addon:" + a.Id, h.Display, "host changed by an add-on (approved)", string.Join("; ", lines));
                return new JsonObject { ["changed"] = true };
            }));
        Methods.Add(new("hosts.remove", "hosts.write",
            "Remove a saved host (you approve it; its credentials stay in the vault): {\"hostId\"}",
            async (a, p, _, _) =>
            {
                var h = LocalHost(p);
                Gate(a, "host.delete", h);
                if (!await Approver(a, $"remove host {h.Display}", $"Remove the saved host {h.Display} ({h.Endpoint})? Its credentials stay in the vault.", h)) throw new AddonHost.AddonError(-32003, "the user declined");
                AppData.Current.Hosts.Remove(h);
                Locks.Forget("host:" + h.Id);
                AppData.Save();
                AuditLog.Write("addon:" + a.Id, h.Display, "host removed by an add-on (approved)", h.Endpoint);
                return new JsonObject { ["removed"] = true };
            }));
        Methods.Add(new("ui.open", "ui.control",
            "Open a screen: {\"screen\": dashboard|hosts|commander|notes|sessionlogs|incidents|infra|cloud|portals|snippets|tunnels|serial|lan|credentials|knownhosts|library|apiexplorer|aipanel|assistant|reports|help|privacy|addons|audit|settings}",
            (a, p, _, _) =>
            {
                var screen = Str(p, "screen", 40, required: true);
                if (!Screens.Contains(screen)) throw new AddonHost.AddonError(-32602, "unknown screen " + screen);
                if (Locks.CategoryOfScreen(screen) is { } cat && Locks.IsCategoryLocked(cat)) throw new AddonHost.AddonError(-32001, $"the {screen} screen is locked; add-ons can't open locked screens");
                if (OpenScreen?.Invoke(screen) != true) throw new AddonHost.AddonError(-32000, "the main window is not open");
                AuditLog.Write("addon:" + a.Id, "addons", "add-on opened a screen", screen);
                return Task.FromResult<JsonNode?>(new JsonObject { ["opened"] = screen });
            }));
        Methods.Add(new("ui.openHost", "ui.control",
            "Open a saved host: {\"hostId\",\"view\": server|terminal|files|commander|notes} (a terminal connects with the host's own sign-in)",
            (a, p, _, _) =>
            {
                var id = Str(p, "hostId", 64, required: true);
                var h = AppData.Current.FindHost(id) ?? throw new AddonHost.AddonError(-32602, "no such host");
                if (Locks.IsLocked("host:" + h.Id)) throw new AddonHost.AddonError(-32001, $"“{h.Display}” is locked; add-ons can't use locked items");
                var view = Str(p, "view", 20);
                if ((view.Length == 0 ? "server" : view) is not ("server" or "terminal" or "files" or "commander" or "notes"))
                    throw new AddonHost.AddonError(-32602, "\"view\" must be server, terminal, files, commander or notes");
                if (OpenHostView?.Invoke(h, view.Length == 0 ? "server" : view) != true) throw new AddonHost.AddonError(-32000, "the main window is not open");
                AuditLog.Write("addon:" + a.Id, h.Display, "add-on opened a host view", view.Length == 0 ? "server" : view);
                return Task.FromResult<JsonNode?>(new JsonObject { ["opened"] = view.Length == 0 ? "server" : view });
            }));
    }
}
