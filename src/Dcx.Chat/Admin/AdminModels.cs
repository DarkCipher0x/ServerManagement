using System.Text.Json;
using System.Text.Json.Nodes;

namespace DcxManager.Chat.Admin;

/// <summary>
/// What CipherChat's admin API returns (server: access_api.py, settings_api.py, chat_requests.py; docs/ADMIN.md), as
/// plain records (ADR-117). The server is the authority: these only carry what it said, and every change is checked
/// there again (permissions, "fewer permissions than you", the owner rule, a fresh authenticator code). Unknown fields
/// are ignored and fields of the wrong type read as empty, so a newer server never breaks the pane; permissions and
/// settings come with their own catalogue and schema from the server, so new ones appear without a code change.
/// </summary>
public sealed record AdminUser(
    string Username, string DisplayName, string Email, string Role, string Status, bool Locked,
    string LastLogin, string Created, bool ChatIdentity, bool Certified, int Sessions,
    IReadOnlyList<string> Grants, IReadOnlyList<string> Denies, IReadOnlyList<string> Permissions, bool Manageable)
{
    public bool Pending => Status == "pending";
    public bool Suspended => Status == "suspended";
}

/// <summary>The People list, plus who's asking and what they hold (the pane offers only what they could do).</summary>
public sealed record AdminPeople(IReadOnlyList<AdminUser> Users, string Me, string Owner, IReadOnlyList<string> Roles, IReadOnlyList<string> Mine)
{
    public bool IAmOwner => Me == Owner;
    public bool Can(string permission) => IAmOwner || Mine.Contains(permission);
}

/// <summary>One permission in the server's catalogue (id, group, what it lets someone do).</summary>
public sealed record AdminPermission(string Id, string Group, string Label);

public sealed record AdminRole(string Name, string Label, IReadOnlyList<string> Perms, bool Builtin, int People, string Updated, string UpdatedBy);

public sealed record AdminRoles(IReadOnlyList<AdminRole> Roles, IReadOnlyList<AdminPermission> Permissions, IReadOnlyList<string> Mine, bool CanManage);

/// <summary>A request for access from the public form. The requester's address and message stay on the server's admin
/// pages; this PC shows them while the pane is open and never stores them.</summary>
public sealed record AccessRequest(long Id, string Created, string Name, string Email, string Message, string Status, string DecidedBy);

public sealed record AccessRequests(IReadOnlyList<AccessRequest> Requests, IReadOnlyDictionary<string, int> Counts);

/// <summary>A line of the server's tamper-evident audit log (newest first, at most 300).</summary>
public sealed record AdminAuditEvent(long Seq, string At, string Actor, string Action, string TargetType, string TargetId, string Outcome, string Ip);

/// <summary>A one-time invite or reset link. Shown once, copied by the admin, never written to disk or to any log.</summary>
public sealed record OneTimeLink(string Url, int ExpiresHours);

/// <summary>What a change returned: the link it made (if any), whether it was emailed, how many sign-ins it ended.</summary>
public sealed record AdminResult(string Username, OneTimeLink? Link, string? Emailed, int? SessionsEnded);

/// <summary>One server setting and its limits, from the server's own schema.</summary>
public sealed record AdminSettingSpec(string Key, string Kind, long Min, long Max, JsonNode? Default);

/// <summary>The server's settings (when the admin may manage them) and its mail set-up (when they may manage mail).</summary>
public sealed record AdminServerSettings(
    bool CanSettings, bool CanMail, JsonObject Settings, IReadOnlyList<AdminSettingSpec> Schema, IReadOnlyList<string> Roles,
    JsonObject Mail, JsonObject Notify, IReadOnlyDictionary<string, string> Events, int MailQueue, JsonArray MailLog);

/// <summary>Reads the server's JSON leniently (a field of the wrong type is empty, never an exception).</summary>
internal static class Read
{
    public static string Str(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>()
        : n is JsonValue x && x.GetValueKind() == JsonValueKind.Number ? x.ToJsonString() : "";
    public static bool Bool(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.True;
    public static long Long(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && long.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var l) ? l : 0;
    public static int Int(JsonNode? n) => (int)Math.Clamp(Long(n), int.MinValue, int.MaxValue);
    public static IReadOnlyList<string> Strings(JsonNode? n) => n is JsonArray a ? a.Select(Str).Where(s => s.Length > 0).ToList() : [];
    public static IEnumerable<JsonObject> Objects(JsonNode? n) => n is JsonArray a ? a.OfType<JsonObject>() : [];
    public static JsonObject Obj(JsonNode? n) => n is JsonObject o ? (JsonObject)o.DeepClone() : [];
}
