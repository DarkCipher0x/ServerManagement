using System.IO;

namespace DcxManager.Services;

// ---------------------------------------------------------------------------------------------------------------------
// UI-free seams (0.2.0 phase 1, docs/history/PLAN-0.2.0.md). Code below the screens — stores, providers, SSH, add-ons, the AI —
// talks to the user and the window only through these interfaces. The WPF side registers the implementations at start-up
// (Views/WpfUi.cs); without a registration (a headless test) the defaults refuse every dialog, log it and run work
// inline. Nothing in this file may reference System.Windows.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Dialogs the lower layers may ask for. Signatures mirror <c>Views.Dialogs</c>; in the self-test every one is
/// refused and reported, as before.</summary>
public interface IDialogs
{
    string? Prompt(string title, string message, string initial = "", bool password = false, bool multiline = false);
    /// <summary>A password prompt with one tick box under it (0.6.20: "Remember on this PC"). Null text = cancelled.</summary>
    (string? Text, bool Option) PromptPassword(string title, string message, string option, bool optionDefault = false);
    bool Confirm(string title, string message, string action = "Continue", bool danger = false, string? typeToConfirm = null, (string Env, string Target)? banner = null);
    int Choose(string title, string message, string[] options, int cancelIndex = -1, double width = 560);
    void Info(string title, string message);
    void Error(string title, string message, bool explain = true, Exception? ex = null);
    void ShowText(string title, string text, bool modal = false);
    T? Pick<T>(string title, string message, IEnumerable<T> items, Func<T, string> label) where T : class;
}

/// <summary>The main window as the lower layers see it: a status line, navigation by screen key, and "save everything now".</summary>
public interface IShell
{
    void Status(string text);
    void Navigate(string key);
    void Activate();
    /// <summary>Writes the workspace layout now (tabs, panes, pop-outs).</summary>
    void SaveWorkspaceNow();
    /// <summary>Flushes every editor with unsaved work (notes, incidents) and the workspace: the emergency path.</summary>
    void SaveAllNow();
    /// <summary>A web page in the app's own browser window (consoles, provider portals); an open window with the same
    /// <paramref name="reuseKey"/> is reused.</summary>
    void OpenWeb(string title, string url, string? reuseKey = null);
    /// <summary>A document (PDF, HTML or text) in the in-memory document viewer.</summary>
    void ShowDocument(Documents.DocumentSource doc);
    /// <summary>A terminal tab to a saved host, optionally typing <paramref name="initialCommand"/> once connected.</summary>
    void OpenTerminal(HostEntry host, string? initialCommand = null);
    /// <summary>What the user is looking at, by name only (screen kinds, host names, environments; never content).</summary>
    string DescribeScreen();
    /// <summary>Adds text to the active incident's timeline (or asks which incident).</summary>
    void AddToIncident(Incidents.ItemKind kind, string title, string text);
}

/// <summary>The UI thread. Without a UI (<see cref="HasUi"/> false) work runs inline on the calling thread.</summary>
public interface IUiThread
{
    bool HasUi { get; }
    bool CheckAccess();
    /// <summary>Queue work on the UI thread and return at once.</summary>
    void Post(Action action);
    /// <summary>Run on the UI thread and wait (runs inline when already there).</summary>
    void Invoke(Action action);
    T Invoke<T>(Func<T> func);
    /// <summary>Run on the UI thread, waiting at most <paramref name="timeout"/>; false when it did not run in time.</summary>
    bool TryInvoke(Action action, TimeSpan timeout);
    Task InvokeAsync(Action action);
    Task<T> InvokeAsync<T>(Func<T> func);
    /// <summary>A repeating timer whose callback runs on the UI thread; dispose to stop.</summary>
    IDisposable Every(TimeSpan interval, Action tick);
}

/// <summary>The registered seams. Set once by the WPF start-up; read everywhere below the screens.</summary>
public static class Ui
{
    public static IDialogs Dialogs { get; set; } = new NoDialogs();
    public static IShell Window { get; set; } = new NoShell();
    public static IUiThread Thread { get; set; } = new NoUiThread();
}

/// <summary>Error log (sealed per entry). Moved here from <c>App</c> so code below the screens can log without the WPF
/// application; <c>App.Log</c> forwards to <see cref="Write"/>.</summary>
public static class Log
{
    /// <summary>Error logs. Under DCX_DATA_DIR (self-test) they go next to the scratch store, not into it (the store
    /// folder must stay movable/empty-checkable) and never into the real per-user folder.</summary>
    public static string Dir => Environment.GetEnvironmentVariable("DCX_DATA_DIR") is { Length: > 0 } scratch
        ? Path.GetFullPath(scratch).TrimEnd('\\') + "-logs"
        : Path.Combine(AppInfo.CacheDir, "logs");

    /// <summary>Appends an error to today's log file (no secrets are ever passed here). Never throws.</summary>
    public static string Write(string where, Exception? ex)
    {
        Interlocked.Increment(ref _errors);
        var path = Path.Combine(Dir, $"errors-{DateTime.Now:yyyy-MM-dd}.log");
        try
        {
            Directory.CreateDirectory(Dir);
            // Secret-looking values (keys, passwords, tokens, credentials in URLs) are masked first, sealed or not (0.2.0
            // audit: an exception message can carry a request URL with a token, and entries before unlocking are in clear).
            var entry = Redaction.Mask($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {where}: {ex}");
            // Exceptions can carry host names, paths and server output, so once the store is unlocked every entry is sealed
            // (one AES-GCM line per entry; Settings → Data & storage → View error log decrypts them). Only failures before
            // unlocking — when there is no key yet — can be written in clear.
            var plain = entry;
            if (SecureStore.IsUnlocked) entry = "ENC " + SecureStore.EncryptString(entry, "errlog");
            lock (Gate) File.AppendAllText(path, entry + "\r\n\r\n");
            try { Written?.Invoke(where, plain); } catch { }
        }
        catch { }
        return path;
    }

    /// <summary>After an entry is written: where, and the entry already masked (syslog forwarding, 0.3.0 §3). A handler
    /// must never call <see cref="Write"/> itself.</summary>
    public static event Action<string, string>? Written;

    private static readonly object Gate = new();
    private static int _errors;

    /// <summary>Errors logged since start (the dashboard's local warnings, ADR-018).</summary>
    public static int ErrorsThisSession => Volatile.Read(ref _errors);

    /// <summary>The error logs of the last <paramref name="days"/> days, newest last, with sealed entries decrypted.</summary>
    public static string ReadLogs(int days)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            if (!Directory.Exists(Dir)) return "";
            foreach (var f in Directory.EnumerateFiles(Dir, "errors-*.log").OrderBy(f => f).Where(f => File.GetLastWriteTime(f) > DateTime.Now.AddDays(-days)))
            {
                sb.AppendLine("──── " + Path.GetFileName(f));
                string[] lines;
                lock (Gate) lines = File.ReadAllLines(f);
                foreach (var line in lines)
                {
                    if (!line.StartsWith("ENC ")) { sb.AppendLine(line); continue; }
                    try { sb.AppendLine(SecureStore.DecryptString(line[4..], "errlog")); }
                    catch { sb.AppendLine("[sealed entry — written under another data key]"); }
                }
            }
        }
        catch (Exception ex) { sb.AppendLine("Could not read the logs: " + ex.Message); }
        return sb.ToString();
    }
}

// ---- defaults when no UI is registered ------------------------------------------------------------------------------

/// <summary>No UI: every dialog is refused (cancel) and logged, so a headless run never blocks.</summary>
internal sealed class NoDialogs : IDialogs
{
    private static void Refused(string kind, string title) => Log.Write("Dialog without a UI", new InvalidOperationException($"{kind}: {title}"));
    public string? Prompt(string title, string message, string initial = "", bool password = false, bool multiline = false) { Refused("prompt", title); return null; }
    public (string? Text, bool Option) PromptPassword(string title, string message, string option, bool optionDefault = false) { Refused("prompt", title); return (null, false); }
    public bool Confirm(string title, string message, string action = "Continue", bool danger = false, string? typeToConfirm = null, (string Env, string Target)? banner = null) { Refused("confirm", title); return false; }
    public int Choose(string title, string message, string[] options, int cancelIndex = -1, double width = 560) { Refused("choose", title); return cancelIndex; }
    public void Info(string title, string message) => Refused("info", title);
    public void Error(string title, string message, bool explain = true, Exception? ex = null) => Log.Write(title, ex ?? new Exception(message));
    public void ShowText(string title, string text, bool modal = false) => Refused("text", title);
    public T? Pick<T>(string title, string message, IEnumerable<T> items, Func<T, string> label) where T : class { Refused("pick", title); return null; }
}

internal sealed class NoShell : IShell
{
    public void Status(string text) { }
    public void Navigate(string key) { }
    public void Activate() { }
    public void SaveWorkspaceNow() { }
    public void SaveAllNow() { }
    public void OpenWeb(string title, string url, string? reuseKey = null) { }
    public void ShowDocument(Documents.DocumentSource doc) { }
    public void OpenTerminal(HostEntry host, string? initialCommand = null) { }
    public string DescribeScreen() => "";
    public void AddToIncident(Incidents.ItemKind kind, string title, string text) { }
}

/// <summary>No UI thread: everything runs inline; timers use the thread pool.</summary>
internal sealed class NoUiThread : IUiThread
{
    public bool HasUi => false;
    public bool CheckAccess() => true;
    public void Post(Action action) => action();
    public void Invoke(Action action) => action();
    public T Invoke<T>(Func<T> func) => func();
    public bool TryInvoke(Action action, TimeSpan timeout) { action(); return true; }
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
    public IDisposable Every(TimeSpan interval, Action tick) => new Timer(_ => tick(), null, interval, interval);
}
