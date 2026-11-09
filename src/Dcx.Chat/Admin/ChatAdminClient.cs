using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CipherChat.Api;
using DcxManager.Services;

namespace DcxManager.Chat.Admin;

/// <summary>
/// CipherChat server administration from ServerManagement (ADR-117), the same as CipherChat for Windows' Admin window:
/// people, invites, roles and permissions, access requests, the audit log, server settings, email and notifications.
/// The protocol is CipherChat's own <see cref="AdminApi"/> (vendored); this class adds only what makes it part of the app:
/// typed records (Admin/AdminModels.cs), shape checks before a round trip, the policy gate and the app's audit lines.
/// <para>
/// <b>Its own sign-in.</b> A paired device is chat-only (the server refuses it on the admin API), so administration signs
/// in separately: password + authenticator code (scope <c>admin</c>). That sign-in works on the admin API only, never the
/// chat. It lives in memory only (its cookie, in its own <see cref="ChatApi"/>): nothing is stored, it is never resumed
/// with the device token, and when it idles out, the pane closes or the app locks, the pane asks again.
/// </para>
/// <para>
/// <b>The server decides.</b> Every change is checked by the server (the permission, "fewer permissions than you have",
/// the owner rule) and needs a fresh authenticator code; <see cref="Ask"/> asks for it when the server wants it. One-time
/// links are returned to the pane to show once, and only when they point at the server's own sign-in page; they are
/// never logged or stored. ServerManagement's audit log gets what was done to whom on which server, never a link,
/// password, email address or message.
/// </para>
/// </summary>
public sealed class ChatAdminClient
{
    private static readonly Regex UserRe = new("^[a-z0-9._-]{3,32}$", RegexOptions.CultureInvariant);
    private static readonly Regex RoleRe = new("^[a-z][a-z0-9-]{1,23}$", RegexOptions.CultureInvariant);
    private static readonly Regex PermRe = new("^[a-z]+(\\.[a-z]+){1,3}$", RegexOptions.CultureInvariant);

    private AdminApi? _admin;

    /// <summary>Asks for the authenticator code when the server wants it again (set by the pane).</summary>
    public AskCode Ask { get; set; } = _ => Task.FromResult<string?>(null);

    public string Origin { get; private set; } = "";
    public string User { get; private set; } = "";
    public bool SignedIn => _admin is not null;
    public string Display => User + "@" + (Uri.TryCreate(Origin, UriKind.Absolute, out var u) ? u.Host : Origin);

    /// <summary>The admin sign-in ended (signed out, idled out on the server, the app locked).</summary>
    public event Action? SignedOut;

    /// <summary>Why administration can't be used on this PC (policy <c>ChatAdmin</c>), or null.</summary>
    public static string? Blocked() => AppSettings.Current.ChatAdmin ? null : "CipherChat administration is turned off by your organisation's policy.";

    // ------------------------------------------------------------------------------------------- sign-in

    /// <summary>Password + authenticator code, administration-only. The password is a parameter only, never kept.</summary>
    public async Task SignInAsync(string server, string user, string password, string code, CancellationToken ct = default)
    {
        if (Blocked() is { } off) throw new InvalidOperationException(off);
        if (ChatServers.Check(server) is { } why) throw new ArgumentException(why);
        var origin = ChatServers.Origin(server)!;
        var api = new ChatApi(ChatServers.TransportFor(new Uri(origin)), ChatSession.UserAgent);
        await AdminApi.SignInAsync(api, user.Trim(), password, code.Trim(), ct).ConfigureAwait(false);
        var admin = new AdminApi(api);
        var status = await admin.StatusAsync(ct).ConfigureAwait(false);
        // A 0.2.0 server ignores the scope (admin then works from that sign-in); a newer one must say "admin".
        var scope = Read.Str(status["scope"]);
        if (!Read.Bool(status["admin"]) || scope is not ("admin" or ""))
        {
            try { await api.LogoutAsync(ct).ConfigureAwait(false); } catch (ApiException) { }
            throw new InvalidOperationException("That account doesn't manage this server.");
        }
        api.Ask = c => Ask(c);
        SignOutQuietly();
        _admin = admin;
        Origin = origin;
        User = Read.Str(status["user"]);
        AuditLog.Write("you", "chat-admin", "signed in for administration", Display);
    }

    /// <summary>Ends the admin sign-in on the server and forgets it here.</summary>
    public async Task SignOutAsync()
    {
        if (_admin is not { } a) return;
        try { await a.Client.LogoutAsync().ConfigureAwait(false); } catch (ApiException) { }
        AuditLog.Write("you", "chat-admin", "signed out of administration", Display);
        SignOutQuietly();
    }

    /// <summary>Forgets the sign-in without asking the server (app lock, quitting: the server idles it out).</summary>
    public void SignOutQuietly()
    {
        if (_admin is null) return;
        _admin = null;
        SignedOut?.Invoke();
    }

    private async Task<JsonObject> Run(Func<AdminApi, Task<JsonObject>> call)
    {
        var a = _admin ?? throw new InvalidOperationException("Sign in for administration first.");
        try { return await call(a).ConfigureAwait(false); }
        catch (ApiException e) when (e.Status == 403 && !e.Denied)
        {
            // "Sign in first.": the admin session idled out. It is never resumed silently; the pane asks again.
            SignOutQuietly();
            throw new InvalidOperationException("Your administration sign-in ended. Sign in again to carry on.", e);
        }
    }

    private void Audit(string what, string target) => AuditLog.Write("you", "chat-admin", what, target + " on " + Display);

    // ------------------------------------------------------------------------------------------- people

    public async Task<AdminPeople> PeopleAsync()
    {
        var o = await Run(a => a.UsersAsync()).ConfigureAwait(false);
        var users = Read.Objects(o["users"]).Select(u => new AdminUser(
            Read.Str(u["username"]), Read.Str(u["display_name"]), Read.Str(u["email"]), Read.Str(u["role"]), Read.Str(u["status"]),
            Read.Bool(u["locked"]), Read.Str(u["last_login"]), Read.Str(u["created"]), Read.Bool(u["chat_identity"]), Read.Bool(u["certified"]),
            Read.Int(u["sessions"]), Read.Strings(u["grants"]), Read.Strings(u["denies"]), Read.Strings(u["permissions"]), Read.Bool(u["manageable"])))
            .Where(u => u.Username.Length > 0).ToList();
        return new AdminPeople(users, Read.Str(o["me"]), Read.Str(o["owner"]), Read.Strings(o["roles"]), Read.Strings(o["mine"]));
    }

    /// <summary>Invites someone: a pending account and a one-time link (emailed to them too when <paramref name="sendEmail"/>).</summary>
    public async Task<AdminResult> InviteAsync(string username, string displayName, string email, string role, bool sendEmail)
    {
        username = CheckUser(username.Trim().ToLowerInvariant());
        if (username.StartsWith("addon.", StringComparison.Ordinal)) throw new ArgumentException("Names starting with 'addon.' are kept for add-ons.");
        CheckRole(role);
        var r = Result(await Run(a => a.InviteAsync(username, Clip(displayName, 80), Clip(email, 120), role, sendEmail)).ConfigureAwait(false), username);
        Audit("invited (role " + role + ")", username);
        return r;
    }

    public Task<AdminResult> SetRoleAsync(string user, string role)
    {
        CheckRole(role);
        return Act(user, a => a.SetRoleAsync(user, role), "role changed to " + role);
    }

    /// <summary>Single permissions on top of the role: always (<paramref name="grants"/>) or never (<paramref name="denies"/>).</summary>
    public Task<AdminResult> SetPermissionsAsync(string user, IEnumerable<string> grants, IEnumerable<string> denies)
    {
        var g = grants.Select(CheckPerm).Distinct().ToList();
        var d = denies.Select(CheckPerm).Distinct().Except(g).ToList();
        return Act(user, a => a.SetPermissionsAsync(user, g, d), "permissions changed (" + g.Count + " always, " + d.Count + " never)");
    }

    public Task<AdminResult> SuspendAsync(string user) => Action(user, "suspend", false, "suspended");
    public Task<AdminResult> ActivateAsync(string user) => Action(user, "activate", false, "reactivated");
    public Task<AdminResult> SignOutUserAsync(string user) => Action(user, "sign_out", false, "signed out everywhere");
    public Task<AdminResult> ResetPasswordAsync(string user, bool sendEmail) => Action(user, "reset_password", sendEmail, "password reset link made");
    public Task<AdminResult> ResetMfaAsync(string user, bool sendEmail) => Action(user, "reset_mfa", sendEmail, "authenticator reset link made");
    public Task<AdminResult> NewInviteAsync(string user, bool sendEmail) => Action(user, "new_invite", sendEmail, "new invite link made");
    /// <summary>Deletes the account: its identity, devices, room memberships and sessions go; the name stays reserved.</summary>
    public Task<AdminResult> DeleteAsync(string user) => Action(user, "delete", false, "account deleted");

    private Task<AdminResult> Action(string user, string action, bool sendEmail, string what) =>
        Act(user, a => a.UserActionAsync(user, action, null, sendEmail), what);

    private async Task<AdminResult> Act(string user, Func<AdminApi, Task<JsonObject>> call, string what)
    {
        CheckUser(user);
        var r = Result(await Run(call).ConfigureAwait(false), user);
        Audit(what, user);
        return r;
    }

    private AdminResult Result(JsonObject o, string user)
    {
        // A link is shown only when it points at this server's own sign-in page (a reply can't plant another address).
        OneTimeLink? link = o["link"] is JsonObject l && Read.Str(l["url"]) is { Length: > 0 } url && IsOwnLink(url)
            ? new OneTimeLink(url, Read.Int(l["expires_hours"])) : null;
        var emailed = o["email"] switch
        {
            null => null,
            JsonObject e => Read.Bool(e["ok"]) ? "sent" : Read.Str(e["error"]) is { Length: > 0 } err ? err : "not sent",
            var e => Read.Bool(e) ? "sent" : Read.Str(e),
        };
        return new AdminResult(Read.Str(o["username"]) is { Length: > 0 } n ? n : user, link, emailed, o["ended"] is null ? null : Read.Int(o["ended"]));
    }

    internal bool IsOwnLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.UserInfo.Length == 0
        && string.Equals(u.GetLeftPart(UriPartial.Authority), Origin, StringComparison.OrdinalIgnoreCase)
        && u.AbsolutePath.StartsWith("/chat/login/", StringComparison.Ordinal);

    // ------------------------------------------------------------------------------------------- roles

    public async Task<AdminRoles> RolesAsync()
    {
        var o = await Run(a => a.RolesAsync()).ConfigureAwait(false);
        var roles = Read.Objects(o["roles"]).Select(r => new AdminRole(Read.Str(r["name"]), Read.Str(r["label"]), Read.Strings(r["perms"]),
            Read.Bool(r["builtin"]), Read.Int(r["people"]), Read.Str(r["updated"]), Read.Str(r["updated_by"]))).Where(r => r.Name.Length > 0).ToList();
        var perms = Read.Objects(o["permissions"]).Select(p => new AdminPermission(Read.Str(p["id"]), Read.Str(p["group"]), Read.Str(p["label"]))).Where(p => p.Id.Length > 0).ToList();
        return new AdminRoles(roles, perms, Read.Strings(o["mine"]), Read.Bool(o["can_manage"]));
    }

    public async Task CreateRoleAsync(string name, string label, IEnumerable<string> perms)
    {
        CheckRole(name);
        var p = perms.Select(CheckPerm).Distinct().ToList();
        await Run(a => a.CreateRoleAsync(name, Clip(label, 40), p)).ConfigureAwait(false);
        Audit("role created", name);
    }

    public async Task UpdateRoleAsync(string name, string label, IEnumerable<string> perms)
    {
        CheckRole(name);
        var p = perms.Select(CheckPerm).Distinct().ToList();
        await Run(a => a.UpdateRoleAsync(name, Clip(label, 40), p)).ConfigureAwait(false);
        Audit("role changed", name);
    }

    public async Task DeleteRoleAsync(string name)
    {
        CheckRole(name);
        await Run(a => a.DeleteRoleAsync(name)).ConfigureAwait(false);
        Audit("role deleted", name);
    }

    // ------------------------------------------------------------------------------------------- access requests

    /// <summary>Requests with this status: new, approved, declined or all.</summary>
    public async Task<AccessRequests> RequestsAsync(string status = "new")
    {
        if (status is not ("new" or "approved" or "declined" or "all")) throw new ArgumentException("Status: new, approved, declined or all.");
        var o = await Run(a => a.RequestsAsync(status)).ConfigureAwait(false);
        var list = Read.Objects(o["requests"]).Select(r => new AccessRequest(Read.Long(r["id"]), Read.Str(r["created"]), Read.Str(r["name"]),
            Read.Str(r["email"]), Read.Str(r["message"]), Read.Str(r["status"]), Read.Str(r["decided_by"]))).Where(r => r.Id > 0).ToList();
        var counts = (o["counts"] as JsonObject ?? []).ToDictionary(kv => kv.Key, kv => Read.Int(kv.Value));
        return new AccessRequests(list, counts);
    }

    /// <summary>Approves a request: an invite for <paramref name="username"/>, its link emailed to the requester when asked.</summary>
    public async Task<AdminResult> ApproveRequestAsync(long id, string username, bool sendEmail)
    {
        CheckRequest(id);
        username = CheckUser(username.Trim().ToLowerInvariant());
        var r = Result(await Run(a => a.ApproveRequestAsync(id, username, sendEmail)).ConfigureAwait(false), username);
        Audit("access request approved", "request " + id + " (" + username + ")");
        return r;
    }

    /// <summary>decline or delete.</summary>
    public async Task DecideRequestAsync(long id, string decision)
    {
        CheckRequest(id);
        if (decision is not ("decline" or "delete")) throw new ArgumentException("Decline or delete.");
        await Run(a => a.DecideRequestAsync(id, decision)).ConfigureAwait(false);
        Audit("access request " + decision + "d", "request " + id);
    }

    // ------------------------------------------------------------------------------------------- audit log

    public async Task<IReadOnlyList<AdminAuditEvent>> AuditAsync()
    {
        var o = await Run(a => a.AuditAsync()).ConfigureAwait(false);
        return Read.Objects(o["events"]).Select(e => new AdminAuditEvent(Read.Long(e["seq"]), Read.Str(e["at"]), Read.Str(e["actor"]), Read.Str(e["action"]),
            Read.Str(e["target_type"]), Read.Str(e["target_id"]), Read.Str(e["outcome"]), Read.Str(e["ip"]))).ToList();
    }

    // ------------------------------------------------------------------------------------------- settings and mail

    public async Task<AdminServerSettings> SettingsAsync()
    {
        var o = await Run(a => a.SettingsAsync()).ConfigureAwait(false);
        var schema = (o["schema"] as JsonObject ?? []).Where(kv => kv.Value is JsonObject).Select(kv => new AdminSettingSpec(kv.Key,
            Read.Str(kv.Value!["kind"]), Read.Long(kv.Value["min"]), Read.Long(kv.Value["max"]), kv.Value["default"]?.DeepClone())).ToList();
        var events = (o["events"] as JsonObject ?? []).ToDictionary(kv => kv.Key, kv => Read.Str(kv.Value));
        return new AdminServerSettings(Read.Bool(o["can_settings"]), Read.Bool(o["can_mail"]), Read.Obj(o["settings"]), schema, Read.Strings(o["roles"]),
            Read.Obj(o["mail"]), Read.Obj(o["notify"]), events, Read.Int(o["queue"]), o["log"] is JsonArray l ? (JsonArray)l.DeepClone() : []);
    }

    /// <summary>Changes server settings. Each value is checked against the server's own schema first (kind and limits).</summary>
    public async Task SaveSettingsAsync(IReadOnlyList<AdminSettingSpec> schema, IReadOnlyDictionary<string, JsonNode> changes)
    {
        var body = CheckSettings(schema, changes);
        if (body.Count == 0) return;
        await Run(a => a.SaveSettingsAsync(body)).ConfigureAwait(false);
        Audit("server settings changed (" + string.Join(", ", body.Select(kv => kv.Key)) + ")", "settings");
    }

    /// <summary>The changes as the server's settings body, each checked against its schema (the self-test checks the rules).</summary>
    internal static JsonObject CheckSettings(IReadOnlyList<AdminSettingSpec> schema, IReadOnlyDictionary<string, JsonNode> changes)
    {
        var body = new JsonObject();
        foreach (var (key, value) in changes)
        {
            var spec = schema.FirstOrDefault(s => s.Key == key) ?? throw new ArgumentException("Unknown setting: " + key);
            body[key] = spec.Kind switch
            {
                "int" when value is JsonValue jv && jv.GetValueKind() == System.Text.Json.JsonValueKind.Number && Read.Long(value) is var n && n >= spec.Min && n <= spec.Max => n,
                "int" => throw new ArgumentException($"{key}: {spec.Min}-{spec.Max}."),
                "bool" when value is JsonValue v && v.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => v.GetValue<bool>(),
                "bool" => throw new ArgumentException(key + ": on or off."),
                "str" when value is JsonValue sv && sv.GetValueKind() == System.Text.Json.JsonValueKind.String && Read.Str(value) is var s && s.Length <= spec.Max => s,
                "str" => throw new ArgumentException($"{key}: at most {spec.Max} characters."),
                _ => throw new ArgumentException("Unknown setting kind for " + key),
            };
        }
        return body;
    }

    /// <summary>The SMTP set-up. The password goes only when it is being changed (<paramref name="newPassword"/>); the server
    /// seals it and never shows it again. Plain SMTP is refused by the server; certificates are always verified.</summary>
    public async Task SaveMailAsync(string host, int port, string security, string user, string? newPassword, bool clearPassword,
                                    string from, string fromName, string replyTo)
    {
        if (security is not ("starttls" or "tls")) throw new ArgumentException("Security: STARTTLS or TLS.");
        if (port is < 1 or > 65535) throw new ArgumentException("Port: 1-65535.");
        var body = new JsonObject
        {
            ["host"] = Clip(host, 253), ["port"] = port, ["security"] = security, ["user"] = Clip(user, 200),
            ["from"] = Clip(from, 200), ["from_name"] = Clip(fromName, 80), ["reply_to"] = Clip(replyTo, 200),
        };
        if (!string.IsNullOrEmpty(newPassword)) body["password"] = newPassword;
        if (clearPassword) body["clear_password"] = true;
        await Run(a => a.SaveMailAsync(body)).ConfigureAwait(false);
        Audit("email set-up changed", "mail");
    }

    /// <summary>Queues a test email (null = the account's own address). Returns where it went.</summary>
    public async Task<string> TestMailAsync(string? to)
    {
        var o = await Run(a => a.TestMailAsync(string.IsNullOrWhiteSpace(to) ? null : Clip(to, 200))).ConfigureAwait(false);
        Audit("test email queued", "mail");
        return Read.Str(o["to"]) is { Length: > 0 } t ? "Queued for " + t + "." : "Queued.";
    }

    /// <summary>Notifications: per event, on/off, to the admins it concerns, to the person, and up to 5 extra addresses.</summary>
    public async Task SaveNotifyAsync(JsonObject perEvent)
    {
        CheckNotify(perEvent);
        var body = (JsonObject)perEvent.DeepClone();
        await Run(a => a.SaveNotifyAsync(body)).ConfigureAwait(false);
        Audit("notifications changed", "mail");
    }

    internal static void CheckNotify(JsonObject perEvent)
    {
        foreach (var (key, v) in perEvent)
            if (v is not JsonObject o || o.Any(kv => kv.Key is not ("enabled" or "admins" or "user" or "extra")) || (o["extra"] is JsonArray x && x.Count > 5))
                throw new ArgumentException("Notification " + key + ": enabled, admins, user and up to 5 extra addresses.");
    }

    // ------------------------------------------------------------------------------------------- checks

    internal static string CheckUser(string u) => UserRe.IsMatch(u) ? u : throw new ArgumentException("Username: 3-32 characters, lowercase letters, digits, . _ -");
    internal static string CheckRole(string r) => RoleRe.IsMatch(r) ? r : throw new ArgumentException("Role names: 2-24 lowercase letters, digits and -, starting with a letter.");
    internal static string CheckPerm(string p) => PermRe.IsMatch(p) ? p : throw new ArgumentException("That isn't a permission id: " + p);
    private static void CheckRequest(long id) { if (id is <= 0 or > 999_999_999) throw new ArgumentException("That isn't a request."); }
    private static string Clip(string? s, int n) { s = (s ?? "").Trim(); return s.Length <= n ? s : s[..n]; }

    /// <summary>For the self-test: the origin a signed-in client would have (the link check needs it).</summary>
    internal void SetOriginForTest(string origin) => Origin = origin;
}
