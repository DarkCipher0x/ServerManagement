using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Sessions;

/// <summary>
/// Automatic session logging (ADR-019). Output is made readable (escape sequences stripped, carriage-return overwrites
/// resolved), optionally timestamped per line, and written encrypted like the audit log: one AES-GCM chunk per line of
/// the file, with the file name as context. The file name carries no host name; the title is in an encrypted header.
/// Chunks are written about once a second and on close; nothing is written while the store is locked.
/// </summary>
public sealed class SessionLogger : IDisposable
{
    public const long MaxFileBytes = 64L * 1024 * 1024;
    private const int MaxPendingChars = 1_000_000;

    private readonly object _gate = new();
    private readonly StringBuilder _pending = new();
    private readonly bool _timestamps;
    private readonly string _title;
    private readonly Timer _timer;
    private string _partial = "", _carry = "";
    private int _part = 1;
    private bool _disposed, _dropped;
    private readonly string _stem;

    public string Path { get; private set; }
    /// <summary>Test seam: the clock used for timestamps.</summary>
    internal Func<DateTime> Clock = () => DateTime.Now;

    public SessionLogger(string title, bool timestamps)
    {
        _title = title;
        _timestamps = timestamps;
        Directory.CreateDirectory(SessionLog.Dir);
        _stem = $"slog-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        Path = NewFile();
        _timer = new Timer(_ => { try { Flush(); } catch (Exception ex) { Log.Write("Session log", ex); } }, null, 1000, 1000);
    }

    private string NewFile()
    {
        var p = System.IO.Path.Combine(SessionLog.Dir, _part == 1 ? _stem + SessionLog.Ext : $"{_stem}-{_part}{SessionLog.Ext}");
        var header = JsonSerializer.Serialize(new SessionLog.Header(_title + (_part > 1 ? $" (part {_part})" : ""), DateTime.Now));
        File.WriteAllText(p, "H " + SecureStore.EncryptString(header, SessionLog.Context(p)) + "\n");
        return p;
    }

    /// <summary>Decoded output from the session (any thread).</summary>
    public void Write(string text)
    {
        if (_disposed || text.Length == 0) return;
        lock (_gate)
        {
            text = _carry + text;
            _carry = SessionLog.IncompleteEscapeTail(text) is var t and >= 0 ? text[t..] : "";
            if (_carry.Length > 0) text = text[..^_carry.Length];
            var clean = SessionLog.Strip(text);
            var lines = (_partial + clean).Split('\n');
            _partial = lines[^1];
            if (_partial.Length > 16_384) { lines = lines[..^1].Append(_partial).Append("").ToArray(); _partial = ""; }   // no newline for ages: cut
            foreach (var l in lines[..^1]) Add(SessionLog.Overwrite(l));
        }
    }

    private void Add(string line)
    {
        // 0.4.0: the same clean line can go to the syslog collector (the app wires SessionLog.Forward when that is on).
        if (SessionLog.Forward is { } fwd) { try { fwd(_title, line); } catch (Exception ex) { Log.Write("Session log forward", ex); } }
        if (_pending.Length > MaxPendingChars) { _dropped = true; return; }
        if (_timestamps) _pending.Append('[').Append(Clock().ToString("yyyy-MM-dd HH:mm:ss")).Append("] ");
        _pending.Append(line).Append('\n');
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_pending.Length == 0 && !_dropped) return;
            if (!SecureStore.IsUnlocked) return;   // kept (bounded) until unlocked
            if (_dropped) { _pending.Append("[… output not logged: the store was locked too long …]\n"); _dropped = false; }
            try
            {
                if (new FileInfo(Path).Length > MaxFileBytes) { _part++; Path = NewFile(); }
                File.AppendAllText(Path, "C " + SecureStore.EncryptString(_pending.ToString(), SessionLog.Context(Path)) + "\n");
                _pending.Clear();
            }
            catch (Exception ex) { Log.Write("Session log write", ex); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_gate)
        {
            if (_partial.Length > 0) { Add(SessionLog.Overwrite(_partial)); _partial = ""; }
            _disposed = true;
        }
        _timer.Dispose();
        Flush();
    }
}

public static class SessionLog
{
    public const string Ext = SessionLogFiles.Ext;
    public sealed record Header(string Title, DateTime Started);
    public sealed record Entry(string Path, string Title, DateTime Started, long Bytes);

    public static string Dir => System.IO.Path.Combine(SecureStore.DataDir, "sessionlogs");

    /// <summary>Every logged line, as it is written (session title, the clean line): set by the app to forward session
    /// logs over syslog TLS (0.4.0, AppSettings.SyslogSendSessionLogs); null = nothing leaves the store.</summary>
    public static Action<string, string>? Forward { get; set; }

    /// <summary>Whether a terminal logs its session (0.4.0): the app-wide setting (or policy) first, else the profile.</summary>
    public static bool Wanted(bool profileSays) => AppSettings.Current.SessionLogs switch { "on" => true, "off" => false, _ => profileSays };
    internal static string Context(string path) => "sessionlog:" + System.IO.Path.GetFileName(path);

    private static readonly Regex Escapes = new(
        @"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[PX^_][^\x1b]*\x1b\\|\x1b[()*+][ -~]|\x1b[ -/]*[0-~]|\u009b[0-?]*[ -/]*[@-~]",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex RowMoves = new(@"(?:\x1b\[[0-9;]*[HfEd])+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex Controls = new(@"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]", RegexOptions.Compiled);

    /// <summary>Escape sequences and control characters out; CR LF → LF (lone CRs stay for <see cref="Overwrite"/>).</summary>
    public static string Strip(string s)
    {
        if (s.IndexOf('\x1b') >= 0 || s.IndexOf('\u009b') >= 0)
        {
            // Cursor moves to another row (ConPTY and full-screen programs use them instead of CR LF) become line breaks,
            // or two lines run together in the log.
            try { s = RowMoves.Replace(s, "\n"); } catch (RegexMatchTimeoutException) { }
            try { s = Escapes.Replace(s, ""); }
            catch (RegexMatchTimeoutException) { s = s.Replace("\x1b", ""); }
        }
        s = s.Replace("\r\n", "\n");
        // Backspace erases the previous character (what a person saw), then the rest of the controls go.
        if (s.IndexOf('\b') >= 0) { var sb = new StringBuilder(); foreach (var c in s) { if (c == '\b') { if (sb.Length > 0 && sb[^1] != '\n') sb.Length--; } else sb.Append(c); } s = sb.ToString(); }
        return Controls.Replace(s, "");
    }

    /// <summary>A line with carriage returns shows what was drawn last (progress bars): the text after the last CR, padded
    /// by what it didn't overwrite.</summary>
    public static string Overwrite(string line)
    {
        if (line.IndexOf('\r') < 0) return line;
        var parts = line.Split('\r');
        var result = "";
        foreach (var p in parts) result = p.Length >= result.Length ? p : p + result[p.Length..];
        return result.TrimEnd();
    }

    /// <summary>Index where an escape sequence starts but isn't finished at the end of <paramref name="s"/>, else -1.</summary>
    public static int IncompleteEscapeTail(string s)
    {
        int i = s.LastIndexOf('\x1b');
        if (i < 0 || s.Length - i > 512) return -1;
        var tail = s[i..];
        if (tail.Length == 1) return i;
        // String sequences (OSC, DCS, SOS, PM, APC) end with BEL or ESC \ — the last ESC can't be their end here.
        if (tail[1] is ']' or 'P' or 'X' or '^' or '_') return tail.IndexOf('\x07') < 0 ? i : -1;
        if (tail[1] == '[') return Regex.IsMatch(tail, @"^\x1b\[[0-?]*[ -/]*[@-~]") ? -1 : i;
        if (tail[1] is '(' or ')' or '*' or '+') return tail.Length < 3 ? i : -1;
        return Regex.IsMatch(tail, @"^\x1b[ -/]*[0-~]") ? -1 : i;
    }

    public static List<Entry> List()
    {
        var list = new List<Entry>();
        if (!Directory.Exists(Dir)) return list;
        foreach (var f in Directory.EnumerateFiles(Dir, "slog-*" + Ext))
        {
            try
            {
                string? first;
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs)) first = sr.ReadLine();
                if (first is not { Length: > 2 } || !first.StartsWith("H ")) continue;
                var h = JsonSerializer.Deserialize<Header>(SecureStore.DecryptString(first[2..], Context(f)))!;
                list.Add(new Entry(f, h.Title, h.Started, new FileInfo(f).Length));
            }
            catch { list.Add(new Entry(f, "(unreadable log — another key or damaged)", File.GetCreationTime(f), new FileInfo(f).Length)); }
        }
        return list.OrderByDescending(e => e.Started).ToList();
    }

    /// <summary>The whole log decrypted (in memory only). Damaged chunks are marked, not skipped silently.</summary>
    public static string Read(string path, int maxChars = 20_000_000)
    {
        var sb = new StringBuilder();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) != null && sb.Length < maxChars)
        {
            if (!line.StartsWith("C ")) continue;
            try { sb.Append(SecureStore.DecryptString(line[2..], Context(path))); }
            catch { sb.Append("[… unreadable chunk (damaged, or from another key) …]\n"); }
        }
        if (sb.Length >= maxChars) sb.Append("\n[… the rest is not shown: export the log to see it all …]\n");
        return sb.ToString();
    }

    public static void Delete(string path)
    {
        if (!path.StartsWith(Dir + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(Ext)) throw new ArgumentException("not a session log");
        File.Delete(path);
        AuditLog.Write("you", "session logs", "deleted a session log", System.IO.Path.GetFileName(path));
    }
}
