using System.Text.Json;
using System.Text.Json.Nodes;
using DcxManager.Infra;
using DcxManager.Services;

namespace DcxManager.DcxCtl;

/// <summary>
/// A DarkCipher portal (darkciphersystems.com, the JAT portal, any portal on the same code) in Infrastructure (0.3.0 §9),
/// over dcx-ctl/1. Config: <see cref="ProviderConfig.HostId"/> = the saved host for the portal server (user dcx-ctl, its
/// Ed25519 key, the host key pinned in Known hosts), <see cref="ProviderConfig.CredentialId"/> = a vault password credential
/// whose secret is the enrolment string. Shows the portal (brand, version, health), statistics, security, mail, access
/// overview and users — read-only by default; with "Allow changes" and an admin-scope key, sensitive actions ask for the
/// account's authenticator code first (the portal's own step-up). Never possible here, by the portal's design: sign-in,
/// passwords, MFA, API keys, site deploys.
/// </summary>
public sealed class DcxPortalProvider(ProviderConfig c) : IInfraProvider
{
    public ProviderConfig Config => c;
    public string Where => AppData.Current.FindHost(c.HostId) is { } h ? $"ssh:{h.Address} (dcx-ctl/1)" : "no host set";
    public NetClass? NetworkClass => AppData.Current.FindHost(c.HostId) is { } h ? NetworkPolicy.ClassifyHost(h.Address) : null;

    /// <summary>Self-test seam: opens a session over another transport (the loopback fake).</summary>
    internal static Func<ProviderConfig, CancellationToken, Task<DcxCtlSession>>? Connector;

    private static readonly (string Path, string Kind, string Name)[] Sections =
    {
        ("/admin/api/system", "System", "System & health"), ("/admin/api/stats", "Stats", "Statistics"), ("/admin/api/security", "Security", "Security"),
        ("/admin/api/mail", "Mail", "Mail"), ("/admin/api/access/overview", "Access", "Access overview"),
    };

    private async Task<DcxCtlSession> OpenAsync(CancellationToken ct)
    {
        if (Connector != null) return await Connector(c, ct);
        var host = AppData.Current.FindHost(c.HostId) ?? throw new InvalidOperationException("Pick the portal's saved host (user dcx-ctl, its key).");
        var enrol = (await ExternalCredentials.UseAsync(c.CredentialId, "DCX portal " + c.Name, ct))?.Secret ?? throw new InvalidOperationException("Pick the vault credential holding the enrolment string.");
        return await DcxCtlSession.ConnectAsync(host, enrol, ct);
    }

    public async Task<ProviderCaps> DetectAsync(CancellationToken ct)
    {
        try
        {
            await using var s = await OpenAsync(ct);
            var sys = await s.RequestAsync("GET", "/admin/api/system", ct: ct);
            var version = Str(sys.Json, "version") ?? Str(sys.Json, "app_version") ?? "";
            var o = s.Opened;
            return new ProviderCaps(true, true, s.Scope == "admin", version,
                $"{Str(o, "brand") ?? Str(o, "portal")} · account {Str(o, "account")} · scope {s.Scope}", new[] { "dcx-ctl/1: SSH + PSK/X25519 handshake, end-to-end MACs" });
        }
        catch (Exception ex) { return ProviderCaps.Unreachable(ex.Message); }
    }

    public async Task<IReadOnlyList<InfraObject>> InventoryAsync(CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var list = new List<InfraObject>();
        var o = s.Opened;
        list.Add(new InfraObject("portal", "Portal", Str(o, "brand") ?? Str(o, "portal") ?? c.Name, "online", "", $"account {Str(o, "account")} · scope {s.Scope}",
            new Dictionary<string, string> { ["portal"] = Str(o, "portal") ?? "", ["account"] = Str(o, "account") ?? "", ["scope"] = s.Scope }));
        foreach (var (path, kind, name) in Sections)
        {
            try
            {
                var r = await s.RequestAsync("GET", path, ct: ct);
                if (r.Status == 403) { list.Add(new InfraObject(kind, kind, name, "denied", "portal", "this key's account may not see it", new Dictionary<string, string>())); continue; }
                var props = Flatten(r.Json);
                var status = props.GetValueOrDefault("ok") is "False" or "false" ? "warning" : r.Status == 200 ? "available" : "error " + r.Status;
                list.Add(new InfraObject(kind, kind, name, status, "portal", string.Join(" · ", props.Take(6).Select(p => $"{p.Key} {p.Value}")), props));
            }
            catch (DcxCtlProtocol.ProtocolException) { throw; }
            catch (Exception ex) { list.Add(new InfraObject(kind, kind, name, "error", "portal", ex.Message, new Dictionary<string, string>())); }
        }
        try
        {
            var u = await s.RequestAsync("GET", "/admin/api/access/users", ct: ct);
            if (u.Status == 200 && (u.Json?["users"] as JsonArray ?? u.Json as JsonArray) is { } users)
                foreach (var x in users.OfType<JsonObject>().Take(500))
                    list.Add(new InfraObject("user:" + (Str(x, "username") ?? Str(x, "id")), "User", Str(x, "username") ?? "?", Str(x, "status") ?? (x["active"]?.ToString() == "false" ? "disabled" : "active"),
                        "portal", $"{Str(x, "role")} · last seen {Str(x, "last_login") ?? Str(x, "last_seen") ?? "never"}", Flatten(x)));
        }
        catch (DcxCtlProtocol.ProtocolException) { throw; }
        catch { }
        return list;
    }

    private static readonly InfraAction MailTest = new("mail-test", "Send a test e-mail from the portal", true, Consequence.None, null,
        "The portal sends one test message with its own mail settings to the address you give. Needs your authenticator code (the portal's step-up).")
    { Inputs = new[] { new InfraInput("to", "Send to"), new InfraInput("code", "Authenticator code (6 digits)", Number: true) } };

    public IReadOnlyList<InfraAction> ActionsFor(InfraObject o) => o.Kind == "Mail" ? new[] { MailTest } : Array.Empty<InfraAction>();

    public string? Forbidden(InfraAction a, InfraObject o, ProviderCaps caps) => caps.CanControl ? null : "this key has read scope only";

    public Task<ActionResult> RunAsync(InfraAction a, InfraObject o, CancellationToken ct) => Task.FromResult(new ActionResult(false, false, "This action needs values (address, code)."));

    public async Task<ActionResult> RunAsync(InfraAction a, InfraObject o, IReadOnlyDictionary<string, string> input, CancellationToken ct)
    {
        if (a.Id != MailTest.Id) return new ActionResult(false, false, "unknown action");
        await using var s = await OpenAsync(ct);
        var re = await s.RequestAsync("POST", "/admin/api/auth/reauth", new JsonObject { ["code"] = input.GetValueOrDefault("code", "") }, ct);
        if (re.Status != 200) return new ActionResult(false, false, "The authenticator code was refused.");
        var r = await s.RequestAsync("POST", "/admin/api/mail/test", new JsonObject { ["to"] = input.GetValueOrDefault("to", "") }, ct);
        AuditLog.Write("you", "portal " + c.Name, "test e-mail requested", r.Status.ToString());
        return new ActionResult(r.Status == 200, r.Status == 200, r.Status == 200 ? "The portal accepted the test message." : $"The portal answered {r.Status}: {Str(r.Json, "error")}");
    }

    private static string? Str(JsonNode? n, string key) => n is JsonObject o && o[key] is JsonValue v ? v.ToString() : null;

    /// <summary>Top-level (and one level down) scalar values as name → text, masked; arrays are counted.</summary>
    internal static Dictionary<string, string> Flatten(JsonNode? n)
    {
        var d = new Dictionary<string, string>();
        void Add(string prefix, JsonObject o, int depth)
        {
            foreach (var (k, v) in o)
            {
                if (d.Count >= 60) return;
                var key = prefix + k;
                switch (v)
                {
                    case JsonValue val: d[key] = Redaction.Mask(val.ToString()); break;
                    case JsonArray arr: d[key] = $"{arr.Count} item(s)"; break;
                    case JsonObject child when depth < 1: Add(key + ".", child, depth + 1); break;
                }
            }
        }
        if (n is JsonObject root) Add("", root, 0);
        return d;
    }
}
