using System.Text;
using DcxManager.Services;

namespace DcxManager.Notify;

/// <summary>
/// One RFC 5424 message: <c>&lt;PRI&gt;1 TIMESTAMP HOSTNAME APP-NAME PROCID MSGID [SD] BOM MSG</c>, framed for RFC 5425 as
/// <c>LENGTH SP MESSAGE</c> (octet counting). Structured data uses the documentation enterprise number 32473 (RFC 5612):
/// <c>[dcx@32473 actor="…" target="…" result="…"]</c>. Every text is masked (<see cref="Redaction.Mask"/>) and stripped of
/// control characters, so a secret or a forged line can't reach the collector.
/// </summary>
public static class SyslogMessage
{
    public const string AppName = "DarkCipherServerManagement";
    public const int Local0 = 16;
    public enum Severity { Emergency, Alert, Critical, Error, Warning, Notice, Info, Debug }

    /// <summary>The message bytes (not yet framed).</summary>
    public static byte[] Build(DateTimeOffset time, Severity severity, string msgId, IReadOnlyList<(string Name, string Value)> data, string text, string? host = null)
    {
        var sb = new StringBuilder();
        sb.Append('<').Append(Local0 * 8 + (int)severity).Append(">1 ");
        sb.Append(time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
        sb.Append(Token(host ?? Environment.MachineName, 255)).Append(' ');
        sb.Append(AppName).Append(' ');
        sb.Append(Environment.ProcessId).Append(' ');
        sb.Append(Token(msgId, 32)).Append(' ');
        if (data.Count == 0) sb.Append('-');
        else
        {
            sb.Append("[dcx@32473");
            foreach (var (n, v) in data) sb.Append(' ').Append(Token(n, 32).Replace("=", "").Replace("]", "").Replace("\"", "")).Append("=\"").Append(Escape(Clean(v, 512))).Append('"');
            sb.Append(']');
        }
        var head = Encoding.ASCII.GetBytes(sb.Append(' ').ToString());
        var msg = Encoding.UTF8.GetBytes(Clean(text, 4000));
        return [.. head, 0xEF, 0xBB, 0xBF, .. msg];   // the BOM says the MSG is UTF-8 (RFC 5424 §6.4)
    }

    /// <summary>RFC 5425 framing: the length in octets, a space, the message.</summary>
    public static byte[] Frame(byte[] message) => [.. Encoding.ASCII.GetBytes(message.Length + " "), .. message];

    /// <summary>An audit entry as a message.</summary>
    public static byte[] Audit(AuditEntry e) =>
        Build(e.Time, Severity.Notice, "audit", [("actor", e.Actor), ("target", e.Target), ("result", e.Result)], e.Action);

    /// <summary>An application log (error) entry as a message.</summary>
    public static byte[] AppLog(DateTimeOffset time, string where, string entry) =>
        Build(time, Severity.Error, "error", [("where", where)], entry);

    /// <summary>One line of a logged terminal session (0.4.0): msgid "session", the session's title as structured data.</summary>
    public static byte[] Session(DateTimeOffset time, string session, string line) =>
        Build(time, Severity.Info, "session", [("session", session)], line);

    /// <summary>PRINTUSASCII without spaces (header fields), or "-".</summary>
    private static string Token(string s, int max)
    {
        var t = new string(s.Where(c => c >= '!' && c <= '~').Take(max).ToArray());
        return t.Length == 0 ? "-" : t;
    }

    private static string Clean(string s, int max)
    {
        var masked = Redaction.Mask(s);
        var t = new string(masked.Select(c => c is '\r' or '\n' or '\t' ? ' ' : c).Where(c => !char.IsControl(c)).ToArray());
        return t.Length > max ? t[..max] + "…" : t;
    }

    /// <summary>SD-PARAM value escaping: '"', '\' and ']' (RFC 5424 §6.3.3).</summary>
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("]", "\\]");
}
