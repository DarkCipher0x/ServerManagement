using System.Text.Json.Nodes;

namespace CipherChat.Api;

/// <summary>
/// The server's administration API (/chat/api/admin/*, docs/ADMIN.md), UI-free, so any app can build its own admin
/// screens on it. The server checks every call (permissions, owner protection, a fresh code or passkey for changes:
/// <see cref="ChatApi.CallAsync"/> asks through <see cref="ChatApi.Ask"/> / <see cref="ChatApi.Passkey"/>).
/// <para>A paired device's session is chat-only and gets 403 {denied, admin_signin} here. Administration then needs its
/// own sign-in on a separate <see cref="ChatApi"/> (its own cookie): <see cref="SignInAsync"/> or
/// <see cref="PasskeySignInAsync"/> (scope "admin", which never reaches the chat). Sign it out when done.</para>
/// Answers are the server's JSON as is (shapes in docs/ADMIN.md and the web admin page, web/admin/admin.js).
/// </summary>
public sealed class AdminApi(ChatApi api)
{
    public ChatApi Client { get; } = api;

    /// <summary>The scope of an administration-only sign-in.</summary>
    public const string Scope = "admin";

    // ---------------------------------------------------------------- the admin sign-in
    /// <summary>Password + authenticator code, scope "admin". Returns the user.</summary>
    public static Task<string> SignInAsync(ChatApi fresh, string user, string password, string code, CancellationToken ct = default) =>
        fresh.LoginAsync(user, password, code, Scope, ct);

    /// <summary>A passkey, scope "admin". Returns the user it belongs to (check it's the one you expect).</summary>
    public static Task<string> PasskeySignInAsync(ChatApi fresh, SignPasskey sign, CancellationToken ct = default) =>
        fresh.PasskeyLoginAsync(sign, Scope, ct);

    /// <summary>auth/status: user, owner, permissions, admin, scope, device, reauth_valid_for.</summary>
    public Task<JsonObject> StatusAsync(CancellationToken ct = default) => Client.StatusAsync(ct);

    /// <summary>True when this session can't administer because it's a paired device (sign in for admin instead).</summary>
    public static bool NeedsAdminSignIn(ApiException e) => e.Status == 403 && e.Flag("admin_signin");

    // ---------------------------------------------------------------- access requests (admin.requests.review)
    /// <summary>status: new | approved | declined | all. {requests: [...], counts: {status: n}}.</summary>
    public Task<JsonObject> RequestsAsync(string status = "new", CancellationToken ct = default) =>
        Client.CallAsync("admin/requests?status=" + Uri.EscapeDataString(status), null, ct: ct);

    /// <summary>Approve (creates an invite for username; returns {username, link, email?}).</summary>
    public Task<JsonObject> ApproveRequestAsync(long id, string username, bool sendEmail, CancellationToken ct = default) =>
        Client.CallAsync("admin/requests/" + id, new JsonObject { ["action"] = "approve", ["username"] = username, ["send_email"] = sendEmail }, ct: ct);

    /// <summary>action: decline | delete.</summary>
    public Task<JsonObject> DecideRequestAsync(long id, string action, CancellationToken ct = default) =>
        Client.CallAsync("admin/requests/" + id, new JsonObject { ["action"] = action }, ct: ct);

    // ---------------------------------------------------------------- people (admin.users.*)
    /// <summary>{owner, me, mine, roles, users: [{username, display_name, role, status, locked, grants, denies, certified,
    /// chat_identity, last_login, manageable, sessions}]}.</summary>
    public Task<JsonObject> UsersAsync(CancellationToken ct = default) => Client.CallAsync("admin/users", null, ct: ct);

    /// <summary>A new account's invite link: {username, link: {url, expires_hours}, email?}.</summary>
    public Task<JsonObject> InviteAsync(string username, string displayName, string email, string role, bool sendEmail, CancellationToken ct = default) =>
        Client.CallAsync("admin/users/invite", new JsonObject
        {
            ["username"] = username, ["display_name"] = displayName, ["email"] = email, ["role"] = role, ["send_email"] = sendEmail,
        }, ct: ct);

    /// <summary>The account actions the server offers (<see cref="UserActionAsync"/>).</summary>
    public static readonly IReadOnlyList<string> UserActions =
        ["role", "permissions", "new_invite", "suspend", "activate", "reset_password", "reset_mfa", "sign_out", "delete"];

    /// <summary>One account action. body: {role} for "role", {grants, denies} for "permissions", else empty. Link-making
    /// actions return {link, email?}.</summary>
    public Task<JsonObject> UserActionAsync(string username, string action, JsonObject? body = null, bool sendEmail = true, CancellationToken ct = default)
    {
        if (!UserActions.Contains(action)) throw new ArgumentException("Unknown account action: " + action);
        var b = body?.DeepClone().AsObject() ?? [];
        b["send_email"] = sendEmail;
        return Client.CallAsync("admin/users/" + Uri.EscapeDataString(username) + "/" + action, b, ct: ct);
    }

    public Task<JsonObject> SetRoleAsync(string username, string role, CancellationToken ct = default) =>
        UserActionAsync(username, "role", new JsonObject { ["role"] = role }, ct: ct);

    public Task<JsonObject> SetPermissionsAsync(string username, IEnumerable<string> grants, IEnumerable<string> denies, CancellationToken ct = default) =>
        UserActionAsync(username, "permissions", new JsonObject { ["grants"] = Array(grants), ["denies"] = Array(denies) }, ct: ct);

    // ---------------------------------------------------------------- roles (admin.roles.manage)
    /// <summary>{permissions: [{id, group, label}], roles: [{name, label, builtin, perms, people, updated, updated_by}], can_manage, mine}.</summary>
    public Task<JsonObject> RolesAsync(CancellationToken ct = default) => Client.CallAsync("admin/roles", null, ct: ct);

    public Task<JsonObject> CreateRoleAsync(string name, string label, IEnumerable<string> perms, CancellationToken ct = default) =>
        Client.CallAsync("admin/roles", new JsonObject { ["name"] = name, ["label"] = label, ["perms"] = Array(perms) }, ct: ct);

    public Task<JsonObject> UpdateRoleAsync(string name, string label, IEnumerable<string> perms, CancellationToken ct = default) =>
        Client.CallAsync("admin/roles/" + Uri.EscapeDataString(name), new JsonObject { ["label"] = label, ["perms"] = Array(perms) }, ct: ct);

    public Task<JsonObject> DeleteRoleAsync(string name, CancellationToken ct = default) =>
        Client.CallAsync("admin/roles/" + Uri.EscapeDataString(name) + "/delete", new JsonObject(), ct: ct);

    // ---------------------------------------------------------------- settings, email, notifications
    /// <summary>{schema, settings, roles, env_name} (admin.settings.manage) and {mail, allow_private, events, notify, log,
    /// queue} (admin.mail.manage): each caller sees its own part.</summary>
    public Task<JsonObject> SettingsAsync(CancellationToken ct = default) => Client.CallAsync("admin/settings", null, ct: ct);

    /// <summary>Only the settings that changed: {key: value}.</summary>
    public Task<JsonObject> SaveSettingsAsync(JsonObject changes, CancellationToken ct = default) => Client.CallAsync("admin/settings", changes, ct: ct);

    /// <summary>{host, port, security: starttls|tls, user, password?, clear_password?, from, from_name, reply_to}.</summary>
    public Task<JsonObject> SaveMailAsync(JsonObject mail, CancellationToken ct = default) => Client.CallAsync("admin/mail", mail, ct: ct);

    /// <summary>Queues a test email (to = null: the account's own address). Returns {to}.</summary>
    public Task<JsonObject> TestMailAsync(string? to = null, CancellationToken ct = default) =>
        Client.CallAsync("admin/mail/test", string.IsNullOrWhiteSpace(to) ? new JsonObject() : new JsonObject { ["to"] = to.Trim() }, ct: ct);

    /// <summary>{event: {enabled, admins, user, extra: [addresses]}}.</summary>
    public Task<JsonObject> SaveNotifyAsync(JsonObject notify, CancellationToken ct = default) => Client.CallAsync("admin/notify", notify, ct: ct);

    // ---------------------------------------------------------------- audit (admin.audit.view)
    /// <summary>The newest 300: {events: [{seq, at, actor, action, target_type, target_id, outcome, ip}]} (ip is a daily tag).</summary>
    public Task<JsonObject> AuditAsync(CancellationToken ct = default) => Client.CallAsync("admin/audit", null, ct: ct);

    private static JsonArray Array(IEnumerable<string> items)
    {
        var a = new JsonArray();
        foreach (var x in items) a.Add(x);
        return a;
    }
}
