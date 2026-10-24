using System.IO;
using System.Text.Json;

namespace DcxManager.Services;

public sealed record AuditEntry(DateTime Time, string Actor, string Target, string Action, string Result)
{
    public string TimeText => Time.ToString("yyyy-MM-dd HH:mm:ss");
}

/// <summary>
/// Every change the app makes (by you or the AI assistant) is recorded. One file per month; each line is a separately
/// encrypted entry, so appending never needs to rewrite or decrypt the rest of the file.
/// </summary>
public static class AuditLog
{
    private static readonly object Gate = new();
    public static event Action<AuditEntry>? Added;

    private static string PathFor(DateTime d) => Path.Combine(SecureStore.DataDir, $"audit-{d:yyyy-MM}.dcxlog");

    public static void Write(string actor, string target, string action, string result = "ok")
    {
        var e = new AuditEntry(DateTime.Now, actor, target, action, result);
        try
        {
            var line = SecureStore.EncryptString(JsonSerializer.Serialize(e), "audit");
            lock (Gate) File.AppendAllText(PathFor(e.Time), line + "\n");
        }
        catch { }
        Added?.Invoke(e);
    }

    public static IEnumerable<string> Months() =>
        Directory.Exists(SecureStore.DataDir)
            ? Directory.GetFiles(SecureStore.DataDir, "audit-*.dcxlog").Select(f => Path.GetFileNameWithoutExtension(f)[6..]).OrderDescending()
            : Enumerable.Empty<string>();

    public static List<AuditEntry> Read(string month)
    {
        var list = new List<AuditEntry>();
        var path = Path.Combine(SecureStore.DataDir, $"audit-{month}.dcxlog");
        if (!File.Exists(path)) return list;
        // Under the writers' lock, and sharing write access: a read must never fail because an entry is being appended
        // (the Audit log screen re-reads on every new entry; s26 caught this as an error box).
        string[] lines;
        lock (Gate)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            lines = sr.ReadToEnd().Split('\n');
        }
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { if (JsonSerializer.Deserialize<AuditEntry>(SecureStore.DecryptString(line.Trim(), "audit")) is { } e) list.Add(e); }
            catch { list.Add(new AuditEntry(DateTime.MinValue, "?", "?", "unreadable entry (tampered or from another key)", "error")); }
        }
        list.Reverse();
        return list;
    }
}
