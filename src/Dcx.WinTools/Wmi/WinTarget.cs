using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Security;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.WinTools.Wmi;

/// <summary>
/// The computer Windows management works on (ADR-119): this PC with your own sign-in, or a saved host by its address
/// with the host's saved credential (or, without one, your Windows sign-in: Kerberos / NTLM). WMI goes over DCOM with
/// packet privacy (encrypted), event logs over the event log RPC. The password is held as a SecureString only while
/// the target is open and is never written anywhere. A target is disposed when you switch computers or close the screen.
/// </summary>
public sealed class WinTarget : IDisposable
{
    private readonly string? _user, _domain;
    private SecureString? _password;
    // One connection per namespace (and whether it enables privileges), reused by every query and action on this target.
    private readonly Dictionary<string, ManagementScope> _scopes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"." for this PC, else the host's address.</summary>
    public string Computer { get; }
    public string Display { get; }
    public bool IsLocal => Computer == ".";

    private WinTarget(string computer, string display, string? user, SecureString? password)
    {
        Computer = computer; Display = display;
        if (user is { Length: > 0 })
        {
            var i = user.IndexOf('\\');
            (_domain, _user) = i > 0 ? (user[..i], user[(i + 1)..]) : (null, user);
        }
        _password = password;
    }

    public static WinTarget Local() => new(".", "This PC (" + Environment.MachineName + ")", null, null);

    /// <summary>A computer name or address: letters, digits, dots and hyphens, or an IPv6 address.</summary>
    public static string? ValidateComputer(string host) =>
        Regex.IsMatch(host.Trim(), @"^[A-Za-z0-9]([A-Za-z0-9.\-]{0,252}[A-Za-z0-9])?$|^[0-9a-fA-F:]{2,39}$") ? null
        : "That isn't a computer name or address (letters, digits, dots and hyphens only).";

    /// <summary>A saved host: its address, with its saved credential when it has one.</summary>
    public static async Task<WinTarget> ForHostAsync(HostEntry h, CancellationToken ct = default)
    {
        if (WinToolsPolicy.RemoteBlocked() is { } no) throw new InvalidOperationException(no);
        var addr = h.Address.Trim();
        if (ValidateComputer(addr) is { } bad) throw new InvalidOperationException(bad);
        string? user = null; SecureString? pw = null;
        if (await ExternalCredentials.UseAsync(h.CredentialId, "Windows management " + h.Name, ct).ConfigureAwait(false) is { Username.Length: > 0, Secret.Length: > 0 } c)
        {
            user = c.Username;
            pw = new SecureString();
            foreach (var ch in c.Secret) pw.AppendChar(ch);
            pw.MakeReadOnly();
        }
        return new WinTarget(addr, (h.Name.Length > 0 ? h.Name : addr) + (user is null ? " (your Windows sign-in)" : " (as " + user + ")"), user, pw);
    }

    /// <summary>Any computer by name, with your Windows sign-in (the toolbar's "Connect to…").</summary>
    public static WinTarget ForComputer(string computer)
    {
        computer = computer.Trim();
        if (computer is "." or "localhost" || computer.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return Local();
        if (WinToolsPolicy.RemoteBlocked() is { } no) throw new InvalidOperationException(no);
        if (ValidateComputer(computer) is { } bad) throw new InvalidOperationException(bad);
        return new WinTarget(computer, computer + " (your Windows sign-in)", null, null);
    }

    /// <summary>The connected WMI scope for <paramref name="ns"/> (reused). <paramref name="changes"/>: the caller's privileges are
    /// enabled, for the confirmed actions only; queries that read run without them.</summary>
    public ManagementScope Scope(string ns = @"root\cimv2", bool changes = false)
    {
        if (!WmicCommand.ValidNamespace(ns)) throw new ArgumentException("That isn't a WMI namespace (such as root\\cimv2).");
        var key = (changes ? "w:" : "r:") + ns;
        lock (_scopes)
            if (_scopes.TryGetValue(key, out var open) && open.IsConnected) return open;
        var opts = new ConnectionOptions
        {
            Impersonation = ImpersonationLevel.Impersonate,
            Authentication = AuthenticationLevel.PacketPrivacy,
            EnablePrivileges = changes,
            Timeout = TimeSpan.FromSeconds(30),
        };
        if (!IsLocal && _user is not null)
        {
            opts.Username = _domain is null ? _user : _domain + "\\" + _user;
            opts.SecurePassword = _password;
        }
        var scope = new ManagementScope(@"\\" + Computer + @"\" + ns, opts);
        scope.Connect();
        lock (_scopes) _scopes[key] = scope;
        return scope;
    }

    /// <summary>Runs <paramref name="work"/>; a WMI or RPC failure drops the namespace's connections so the next try reconnects.</summary>
    private T Using<T>(string ns, Func<T> work)
    {
        try { return work(); }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            lock (_scopes) { _scopes.Remove("r:" + ns); _scopes.Remove("w:" + ns); }
            throw;
        }
    }

    /// <summary>Runs a WQL query that only reads, and returns up to <paramref name="max"/> rows of its properties.</summary>
    public Task<WmiTable> QueryAsync(string wql, string ns = @"root\cimv2", int max = 5000, string[]? columns = null, CancellationToken ct = default) =>
        Task.Run(() => Using(ns, () =>
        {
            if (!WmicCommand.IsReadOnlyWql(wql)) throw new InvalidOperationException("Only queries that read (SELECT, ASSOCIATORS OF, REFERENCES OF) are run here.");
            var scope = Scope(ns);
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql), new EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(60) });
            var table = new WmiTable();
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using (o)
                {
                    ct.ThrowIfCancellationRequested();
                    if (table.Rows.Count >= max) { table.Truncated = true; break; }
                    if (table.Columns.Count == 0)
                        table.Columns.AddRange(columns ?? o.Properties.Cast<PropertyData>().Where(p => !p.Name.StartsWith("__", StringComparison.Ordinal)).Select(p => p.Name));
                    var row = new string[table.Columns.Count];
                    for (int i = 0; i < row.Length; i++)
                    {
                        try { row[i] = Format(o.Properties[table.Columns[i]]); }
                        catch (ManagementException) { row[i] = ""; }
                    }
                    table.Rows.Add(row);
                }
            }
            if (table.Columns.Count == 0 && columns is not null) table.Columns.AddRange(columns);
            return table;
        }), ct);

    /// <summary>Calls a method on one WMI object, identified by its path (such as <c>Win32_Service.Name='Spooler'</c>),
    /// and returns its ReturnValue (0 = success).</summary>
    public Task<uint> InvokeAsync(string path, string method, IReadOnlyDictionary<string, object>? args = null, string ns = @"root\cimv2") =>
        Task.Run(() => Using(ns, () =>
        {
            var scope = Scope(ns, changes: true);
            // A path with a key is one object; a bare class name is the class (its static methods: Win32_Process.Create).
            using ManagementObject obj = path.Contains('=') ? new ManagementObject(scope, new ManagementPath(path), null) : new ManagementClass(scope, new ManagementPath(path), null);
            ManagementBaseObject? input = null;
            try
            {
                if (args is { Count: > 0 })
                {
                    input = obj.GetMethodParameters(method);
                    foreach (var (k, v) in args) input[k] = v;
                }
                using var result = obj.InvokeMethod(method, input, null);
                return result?["ReturnValue"] is { } rv ? Convert.ToUInt32(rv, CultureInfo.InvariantCulture) : 0u;
            }
            finally { input?.Dispose(); }
        }));

    /// <summary>Sets one writable property of a WMI object and saves it (e.g. Win32_UserAccount.Disabled).</summary>
    public Task PutAsync(string path, string property, object value, string ns = @"root\cimv2") =>
        Task.Run(() => Using(ns, () =>
        {
            var scope = Scope(ns, changes: true);
            using var obj = new ManagementObject(scope, new ManagementPath(path), null);
            obj.Get();
            obj[property] = value;
            obj.Put();
            return true;
        }));

    /// <summary>The newest events of a log (newest first), up to <paramref name="max"/>, at <paramref name="level"/> or
    /// worse (1 critical … 4 information; 0 = all).</summary>
    public Task<WmiTable> EventsAsync(string log, int max, int level = 0, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var session = IsLocal ? new EventLogSession()
                : _user is null ? new EventLogSession(Computer)
                : new EventLogSession(Computer, _domain, _user, _password, SessionAuthentication.Default);
            var xpath = level is >= 1 and <= 4 ? $"*[System[Level>=1 and Level<={level}]]" : "*";
            var q = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true, Session = session };
            using var reader = new EventLogReader(q);
            var table = new WmiTable();
            table.Columns.AddRange(["Time", "Level", "Source", "Event ID", "Task", "Message"]);
            for (EventRecord? r = reader.ReadEvent(); r is not null; r = reader.ReadEvent())
            {
                using (r)
                {
                    ct.ThrowIfCancellationRequested();
                    if (table.Rows.Count >= max) { table.Truncated = true; break; }
                    string msg, task;
                    try { msg = r.FormatDescription() ?? ""; } catch (EventLogException) { msg = ""; }
                    try { task = r.TaskDisplayName ?? ""; } catch (EventLogException) { task = ""; }
                    table.Rows.Add([
                        r.TimeCreated?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "",
                        LevelName(r.Level), r.ProviderName ?? "", r.Id.ToString(CultureInfo.InvariantCulture), task, Flat(msg, 2000)]);
                }
            }
            return table;
        }, ct);

    public static string LevelName(byte? level) => level switch { 1 => "Critical", 2 => "Error", 3 => "Warning", 4 or 0 => "Information", 5 => "Verbose", _ => "" };

    /// <summary>One line, no control characters, at most <paramref name="max"/> characters.</summary>
    internal static string Flat(string s, int max)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        s = new string(s.Where(c => !char.IsControl(c)).ToArray());
        return s.Length > max ? s[..max] + "…" : s;
    }

    private static string Format(PropertyData p)
    {
        if (p.Value is null) return "";
        if (p.Type == CimType.DateTime && !p.IsArray && p.Value is string dmtf && dmtf.Length >= 14)
        {
            try { return ManagementDateTimeConverter.ToDateTime(dmtf).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }
            catch (ArgumentOutOfRangeException) { return dmtf; }
        }
        if (p.Value is Array a) return Flat(string.Join(", ", a.Cast<object?>().Select(x => Convert.ToString(x, CultureInfo.InvariantCulture))), 1000);
        return Flat(Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? "", 4000);
    }

    public void Dispose()
    {
        lock (_scopes) _scopes.Clear();
        _password?.Dispose(); _password = null;
    }
}

/// <summary>A result: column names and rows of text (what the grid shows).</summary>
public sealed class WmiTable
{
    public List<string> Columns { get; } = new();
    public List<string[]> Rows { get; } = new();
    public bool Truncated { get; set; }

    public int Col(string name) => Columns.FindIndex(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
}
