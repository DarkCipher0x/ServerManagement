using System.Text.Json;
using System.Text.Json.Nodes;
using DcxManager.Services;

namespace DcxManager.Notify;

/// <summary>
/// One event the app ships to a log sink (0.4.0, Observability): an audit line, an application-log error, a logged terminal
/// session line, or a test. Every text is masked (<see cref="Redaction.Mask"/>) and stripped of control characters when the
/// event is made, so a secret or a forged line can't reach any sink, and the masked text is what waits in the spool.
/// </summary>
public sealed record LogEvent(DateTimeOffset Time, string Kind, string Severity, string Message, string Actor = "", string Target = "", string Result = "", string Session = "")
{
    public const string App = "DarkCipherServerManagement";

    public static LogEvent Audit(AuditEntry e) => Make(e.Time, "audit", "notice", e.Action, e.Actor, e.Target, e.Result);
    public static LogEvent AppLog(DateTimeOffset time, string where, string entry) => Make(time, "error", "error", entry, target: where);
    public static LogEvent SessionLine(DateTimeOffset time, string session, string line) => Make(time, "session", "info", line, session: session);
    public static LogEvent Test(string sink) => Make(DateTimeOffset.Now, "test", "notice", $"Test event from {App} for the sink “{sink}”", Environment.UserName, sink);

    /// <summary>A masked, cleaned event.</summary>
    public static LogEvent Make(DateTimeOffset time, string kind, string severity, string message, string actor = "", string target = "", string result = "", string session = "") =>
        new(time, Clean(kind, 32), Clean(severity, 16), Clean(message, 4000), Clean(actor, 256), Clean(target, 512), Clean(result, 512), Clean(session, 256));

    /// <summary>The event as a flat JSON document (ECS-like field names; the same for every sink but Azure Monitor).</summary>
    public JsonObject ToJson() => new()
    {
        ["@timestamp"] = Time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
        ["app"] = App, ["host"] = Environment.MachineName, ["user"] = Environment.UserName,
        ["kind"] = Kind, ["severity"] = Severity, ["actor"] = Actor, ["target"] = Target, ["result"] = Result, ["session"] = Session, ["message"] = Message,
    };

    public string ToJsonString() => ToJson().ToJsonString(Compact);

    /// <summary>Azure Monitor custom tables want PascalCase columns and TimeGenerated.</summary>
    public JsonObject ToAzureJson() => new()
    {
        ["TimeGenerated"] = Time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
        ["App"] = App, ["Host"] = Environment.MachineName, ["User"] = Environment.UserName,
        ["Kind"] = Kind, ["Severity"] = Severity, ["Actor"] = Actor, ["Target"] = Target, ["Result"] = Result, ["Session"] = Session, ["Message"] = Message,
    };

    /// <summary>Syslog-style numeric level for GELF (0 emergency … 7 debug).</summary>
    public int Level => Severity switch { "emergency" => 0, "alert" => 1, "critical" => 2, "error" => 3, "warning" => 4, "notice" => 5, "debug" => 7, _ => 6 };

    /// <summary>A spooled event back from JSON (the spool keeps the masked documents; a damaged line is skipped).</summary>
    public static LogEvent? FromJson(string json)
    {
        try
        {
            var o = JsonNode.Parse(json) as JsonObject;
            if (o == null) return null;
            string S(string k) => o[k]?.GetValue<string>() ?? "";
            var time = DateTimeOffset.TryParse(S("@timestamp"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.Now;
            return new LogEvent(time, S("kind"), S("severity"), S("message"), S("actor"), S("target"), S("result"), S("session"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    internal static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Clean(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var masked = Redaction.Mask(s);
        var t = new string(masked.Select(c => c is '\r' or '\n' or '\t' ? ' ' : c).Where(c => !char.IsControl(c)).ToArray());
        return t.Length > max ? t[..max] + "…" : t;
    }
}
