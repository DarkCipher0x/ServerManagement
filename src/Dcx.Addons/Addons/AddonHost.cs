using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using DcxManager.Incidents;
using DcxManager.Services;

namespace DcxManager.Addons;

/// <summary>Where a command was run from: a host, a terminal (with a way to read its screen), or nowhere in particular.</summary>
public sealed record AddonContext(HostEntry? Host = null, string? TerminalTitle = null, Func<Task<Api.ScreenText?>>? Screen = null);

public sealed record AddonRunResult(bool Ok, string Text, bool Show, List<string> Log, int ExitCode, string Status);

/// <summary>
/// Runs one add-on command (ADR-012): verifies the installed files, starts the sandboxed process, sends "invoke", answers
/// the add-on's method calls (each checked against its granted permissions; app-state calls on the UI thread), and
/// collects its "done". Crashes, hangs, floods and protocol abuse end the run — never the app.
/// </summary>
public static class AddonHost
{
    /// <summary>The shared library's hosts and credentials (ADR-033), and its audit of a shared credential given to an add-on
    /// (credential, add-on id): registered by the app, since the library sits above the add-ons (0.2.0 phase 6).</summary>
    public static Func<IEnumerable<HostEntry>> SharedHosts { get; set; } = () => Enumerable.Empty<HostEntry>();
    public static Func<IEnumerable<Credential>> SharedCredentials { get; set; } = () => Enumerable.Empty<Credential>();
    public static Action<Credential, string>? SharedCredentialGiven { get; set; }

    public const int MaxMessages = 2000;
    public const long MaxBytes = 1L << 20;
    public const int MaxLine = 256 * 1024;

    /// <summary>The credential prompt (tests replace it). Returns the chosen credential or null for "deny".</summary>
    internal static Func<InstalledAddon, string, Credential?> CredentialPrompt = AskForCredential;
    /// <summary>The file prompt (tests replace it). Returns a path or null.</summary>
    internal static Func<InstalledAddon, string, string?> FilePrompt = AskForFile;

    /// <summary>Self-test: sees each sandboxed process right after it starts (to check that others can't open it).</summary>
    internal static Action<SandboxProcess>? Started;

    private static int _running;
    public static bool Busy => _running > 0;

    public static async Task<AddonRunResult> RunAsync(InstalledAddon a, string commandId, AddonContext ctx, CancellationToken ct = default)
    {
        var log = new List<string>();
        AddonRunResult Fail(string why, string status = "refused") { Record(a, commandId, status + ": " + why); return new(false, why, true, log, -1, status); }
        if (!a.Enabled) return Fail("The add-on is disabled" + (a.DisabledReason != null ? $" ({a.DisabledReason})." : "."));
        if (Offline.AddonsBlocked() is { } offline) { Offline.Block(a.Manifest.Name, "an add-on", "add-ons can use the network themselves"); return Fail(offline, "blocked"); }
        var cmd = a.Manifest.Commands.FirstOrDefault(c => c.Id == commandId);
        if (cmd == null) return Fail("Unknown command.");
        // ADR-035: every message between the app and a program add-on is encrypted. An add-on built for the old, plain-text
        // bridge (API 1) is not run at all.
        if (a.Manifest.Api < 2) return Fail("This add-on uses the old, unencrypted bridge (API 1). Install an updated version built for API 2 (encrypted); see docs/ADDONS.md.", "blocked");
        if (AddonPackages.UseRefusal(a) is { } policy) return Fail(policy, "blocked");
        if (AddonPackages.VerifyFiles(a) is { } tamper)
        {
            AddonPackages.SetEnabled(a, false, "files changed after install: " + tamper);
            return Fail($"The add-on's files no longer match what you approved ({tamper}). It was disabled; reinstall it from a trusted package.", "blocked");
        }

        var entry = Path.Combine(a.Folder, a.Manifest.Entry);
        var data = AddonPackages.DataFolder(a.Id);
        Directory.CreateDirectory(data);
        var cmdLine = entry.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
            ? $"\"{Path.Combine(System.Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")}\" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{entry}\""
            : $"\"{entry}\"";

        SandboxProcess p;
        try { p = SandboxProcess.Start(cmdLine, a.Folder, ChildEnvironment(a, data)); }
        catch (Exception ex) { Log.Write("Add-on start", ex); return Fail("The sandboxed process could not be created: " + ex.Message + ". The add-on did not run (there is no unsandboxed fallback).", "failed"); }

        Started?.Invoke(p);
        Interlocked.Increment(ref _running);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(a.Manifest.TimeoutSeconds, 5, 600));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        string? doneText = null; bool show = false; string status = "ok";
        var stderr = new StringBuilder();
        try
        {
            _ = Task.Run(async () =>
            {
                var buf = new byte[4096];
                try { int n; while ((n = await p.StdErr.ReadAsync(buf)) > 0 && stderr.Length < 8000) stderr.Append(Encoding.UTF8.GetString(buf, 0, n)); } catch { }
            });
            var writer = new StreamWriter(p.StdIn, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            var reader = new LineReader(p.StdOut);
            // Encrypted bridge (API 2, ADR-035): plain-text hello both ways (ephemeral ECDH keys), then AES-256-GCM frames only.
            using var bridge = new AddonBridge();
            await writer.WriteLineAsync(bridge.Hello());
            string? hello = null;
            try { do hello = await reader.ReadLineAsync(cts.Token); while (hello is { } hl && hl.Trim().Length == 0); }
            catch (OperationCanceledException) { status = ct.IsCancellationRequested ? "cancelled" : "timed out"; }
            catch (InvalidDataException ex) { status = "protocol error"; log.Add("✕ " + ex.Message); }
            if (status == "ok")
            {
                if (hello == null) status = "ended before the encrypted handshake";
                else try { bridge.Accept(hello); }
                    catch (InvalidDataException ex) { status = "protocol error"; log.Add("✕ " + ex.Message); }
            }
            if (status == "ok")
                await writer.WriteLineAsync(bridge.Seal(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", method = "invoke",
                    @params = new { api = 2, command = commandId, context = new { hostId = ctx.Host is { IsAdHoc: false } h ? h.Id : null, hostName = ctx.Host?.Display, terminal = ctx.TerminalTitle } },
                })));
            int messages = 0; long bytes = 0;
            while (status == "ok")
            {
                string? line;
                try { line = await reader.ReadLineAsync(cts.Token); }
                catch (OperationCanceledException) { status = ct.IsCancellationRequested ? "cancelled" : "timed out"; break; }
                catch (InvalidDataException ex) { status = "protocol error"; log.Add("✕ " + ex.Message); break; }
                if (line == null) break;
                if (line.Trim().Length == 0) continue;
                bytes += line.Length; messages++;
                if (messages > MaxMessages || bytes > MaxBytes) { status = "flood"; log.Add($"✕ more than {MaxMessages} messages / {MaxBytes >> 20} MB — stopped"); break; }
                string text;
                try { text = bridge.Open(line); }
                catch (InvalidDataException ex) { status = "protocol error"; log.Add("✕ " + ex.Message + " — the run was stopped"); break; }
                JsonObject? msg;
                try { msg = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { msg = null; }
                if (msg == null) { status = "protocol error"; log.Add("✕ not a JSON object: " + Trim(text, 120)); break; }
                var method = msg["method"]?.GetValue<string>() ?? "";
                if (method == "log") { log.Add(Trim(msg["params"]?["text"]?.ToString() ?? "", 2000)); continue; }
                if (method == "done") { doneText = Trim(msg["params"]?["text"]?.ToString() ?? "", 100_000); show = msg["params"]?["show"]?.GetValue<bool>() == true; break; }
                if (msg["id"] is not JsonValue idv) { status = "protocol error"; log.Add("✕ a call without an id"); break; }
                JsonObject reply;
                try
                {
                    var result = await Ui.Thread.InvokeAsync(() => Call(a, method, msg["params"] as JsonObject ?? new JsonObject(), ctx, log, cts.Token)).Unwrap();
                    reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idv.DeepClone(), ["result"] = result };
                }
                catch (AddonError e) { reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idv.DeepClone(), ["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message } }; if (e.Code == -32001) log.Add($"⛔ {method}: {e.Message}"); }
                // 0.5.1: a padlock or a policy restriction said no — the reason is safe to give (it names no secret).
                catch (UnauthorizedAccessException e) { reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idv.DeepClone(), ["error"] = new JsonObject { ["code"] = -32001, ["message"] = e.Message } }; log.Add($"⛔ {method}: {e.Message}"); }
                catch (Exception e) { Log.Write("Add-on call " + method, e); reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idv.DeepClone(), ["error"] = new JsonObject { ["code"] = -32000, ["message"] = "internal error" } }; }
                try { await writer.WriteLineAsync(bridge.Seal(reply.ToJsonString())); } catch (IOException) { break; }
            }
            // Normal end or end-of-output: give the program a moment to exit so its exit code is real (a crash closes
            // stdout a little before the process is gone). Timeouts / floods / garbage are killed right away below.
            if (status == "ok") await p.WaitForExitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            int code = p.HasExited ? p.ExitCode : -1;
            if (!p.HasExited) p.Kill();
            p.Dispose();
            Interlocked.Decrement(ref _running);
            if (doneText == null && status == "ok") status = code == 0 ? "ended without a result" : $"crashed (exit code {code})";
            if (stderr.Length > 0 && status != "ok") log.Add("stderr: " + Trim(stderr.ToString(), 2000));
            Record(a, commandId, status + (doneText != null ? " · " + Trim(doneText.Replace('\n', ' '), 120) : ""));
            AuditLog.Write("you", "addons", "add-on command run", $"{a.Id} {commandId} · {status}");
        }
        bool ok = doneText != null && status == "ok";
        return new(ok, doneText ?? $"The add-on {status}.", show || !ok, log, 0, status);
    }

    private static void Record(InstalledAddon a, string cmd, string what)
    {
        a.RunLog.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {cmd}  {what}");
        while (a.RunLog.Count > 50) a.RunLog.RemoveAt(0);
        try { AppSettings.Save(); } catch (Exception ex) { Log.Write("Add-on run log", ex); }
    }

    private static string Trim(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    /// <summary>The child's whole environment: a whitelist of ours (no secrets that happen to be in it) plus DCX_*.</summary>
    private static Dictionary<string, string> ChildEnvironment(InstalledAddon a, string data)
    {
        string[] keep = { "SystemRoot", "SystemDrive", "windir", "ComSpec", "PATHEXT", "PATH", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS",
                          "USERNAME", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData",
                          "CommonProgramFiles", "CommonProgramFiles(x86)", "PSModulePath", "LOCALAPPDATA", "APPDATA" };
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in keep) if (System.Environment.GetEnvironmentVariable(k) is { } v) env[k] = v;
        env["TEMP"] = env["TMP"] = data;
        env["DCX_ADDON_ID"] = a.Id;
        env["DCX_ADDON_VERSION"] = a.Manifest.Version;
        env["DCX_ADDON_DATA"] = data;
        env["DCX_ADDON_API"] = "2";
        return env;
    }

    // ---- methods (UI thread) ----------------------------------------------------------------------------------------

    public sealed class AddonError : Exception { public int Code { get; } public AddonError(int code, string msg) : base(msg) { Code = code; } }

    private static void Need(InstalledAddon a, string perm)
    {
        if (!a.Granted.Contains(perm)) throw new AddonError(-32001, $"permission denied: {perm}" + (a.Manifest.Permissions.Contains(perm) ? " (not granted by the user)" : " (not requested in addon.json)"));
    }

    private static string Str(JsonObject p, string name, int max, bool required = false)
    {
        var v = p[name];
        if (v == null) { if (required) throw new AddonError(-32602, $"\"{name}\" is required"); return ""; }
        if (v is not JsonValue jv || !jv.TryGetValue<string>(out var s)) throw new AddonError(-32602, $"\"{name}\" must be a string");
        if (s.Length > max) throw new AddonError(-32602, $"\"{name}\" is longer than {max:N0} characters");
        return new string(s.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray());
    }

    private static JsonNode? J(object? o) => o == null ? null : JsonSerializer.SerializeToNode(o, Api.ApiJson.Options);

    private static async Task<JsonNode?> Call(InstalledAddon a, string method, JsonObject p, AddonContext ctx, List<string> log, CancellationToken ct)
    {
        switch (method)
        {
            case "app.info":
                return J(new { version = AppInfo.Version, addon = a.Id, granted = a.Granted, api = 2, encrypted = true });
            // ADR-036: the full-control API — discover everything, call any app capability, and the registered methods below.
            case "app.capabilities":
                return AddonApi.CapabilityList(a);
            case "app.call":
                return await AddonApi.CallAsync(a, p, log, ct);
            case "ui.notify":
                Ui.Window.Status($"◇ {a.Manifest.Name}: {Str(p, "text", 300)}");
                return J(new { ok = true });
            case "hosts.list":
                Need(a, "hosts.read");
                if (Locks.IsCategoryLocked("hosts")) throw new AddonError(-32001, "the Hosts category is locked in the app");
                // Shared library hosts (ADR-033) only with library.read, flagged; never a secret either way.
                // 0.5.1 #10: a padlocked host is listed by name only (add-ons never use locked items).
                return J(AppData.Current.Hosts.Where(h => !h.IsAdHoc)
                    .Concat(a.Granted.Contains("library.read") ? SharedHosts() : Enumerable.Empty<HostEntry>())
                    .Select(h => Locks.IsLocked("host:" + h.Id) ? (object)new { h.Id, name = h.Display, locked = true }
                        : new { h.Id, name = h.Display, h.Address, h.Port, user = h.Username, folder = h.Group, environment = Envs.For(h), shared = h.IsShared, library = h.SharedFrom }));
            case "incidents.active":
                Need(a, "incidents.read");
                return J(IncidentStore.Active is { } ai ? new { ai.Id, ai.Title, status = ai.Status, severity = ai.Severity } : null);
            case "incidents.list":
                Need(a, "incidents.read");
                return J(IncidentStore.All.Select(i => new { i.Id, i.Title, status = i.Status, severity = i.Severity, entries = i.Items.Count }));
            case "incidents.addEvent":
            {
                Need(a, "incidents.write");
                var which = Str(p, "incident", 40);
                var inc = which.Length == 0 ? IncidentStore.Active : IncidentStore.ValidId(which) ? IncidentStore.Find(which) : null;
                if (inc is not { Deleted: null }) throw new AddonError(-32602, which.Length == 0 ? "there is no active incident" : "no such incident");
                var kind = Str(p, "kind", 10);
                var item = IncidentStore.Add(inc, new IncidentItem
                {
                    Kind = kind == "log" ? ItemKind.Log : ItemKind.Event, Title = $"{Str(p, "title", 200, required: true)} (add-on: {a.Manifest.Name})", Text = Str(p, "text", 100_000),
                    HostId = ctx.Host is { IsAdHoc: false } h ? h.Id : null,
                });
                return J(new { item.Id, incident = inc.Id });
            }
            case "notes.search":
                Need(a, "notes.read");
                if (Locks.IsCategoryLocked("notes")) throw new AddonError(-32001, "the Notes category is locked in the app");
                return J(Notes.NoteStore.Search(Str(p, "query", 200), 50).Where(h => !Locks.IsLocked("note:" + h.Note.Id)).Select(h => new { h.Note.Id, h.Note.Title, snippet = h.Snippet }));
            case "notes.create":
            {
                Need(a, "notes.write");
                var hostId = Str(p, "hostId", 40);
                if (hostId.Length > 0 && AppData.Current.FindHost(hostId) == null) throw new AddonError(-32602, "no such host");
                var n = Notes.NoteStore.Create(Str(p, "title", 200, required: true), hostId.Length > 0 ? new[] { hostId } : null, Str(p, "body", 100_000));
                n.Tags.Add("addon"); n.Tags.Add(a.Id); Notes.NoteStore.Save(n);
                return J(new { n.Id });
            }
            case "terminal.screen":
            {
                Need(a, "terminal.read");
                if (ctx.Screen == null) throw new AddonError(-32602, "this command was not run from a terminal");
                var s = await ctx.Screen();
                if (s == null) throw new AddonError(-32000, "the terminal did not answer");
                var (lines, hidden) = Api.BroadcastRedactor.Apply(s, new Api.BroadcastOptions { HidePasswordPrompts = true }, Array.Empty<System.Text.RegularExpressions.Regex>());
                return J(new { lines = hidden ? Array.Empty<string>() : lines, hidden });
            }
            case "files.pick":
            {
                Need(a, "filesystem.read");
                var path = FilePrompt(a, Str(p, "title", 120));
                if (path == null) return J(new { cancelled = true });
                var fi = new FileInfo(path);
                if (fi.Length > 2 * 1024 * 1024) throw new AddonError(-32602, "files above 2 MB are not passed to add-ons");
                AuditLog.Write("you", "addons", "file given to add-on", $"{a.Id}: {fi.Name}");
                return J(new { name = fi.Name, text = await File.ReadAllTextAsync(path) });
            }
            case "credentials.request":
            {
                Need(a, "credentials.request");
                var reason = Str(p, "reason", 300, required: true);
                var c = CredentialPrompt(a, reason);
                AuditLog.Write("you", "addons", c == null ? "credential request DENIED" : "credential given to add-on", $"{a.Id}: {reason}" + (c != null ? $" → {c.Name}" : ""));
                log.Add(c == null ? "credential request denied" : $"credential “{c.Name}” given");
                if (c == null) throw new AddonError(-32003, "the user declined");
                Locks.Refuse("cred:" + c.Id, "addon:" + a.Id);
                if (c.IsShared) SharedCredentialGiven?.Invoke(c, a.Id);   // the library audits it (ADR-033)
                var v = ExternalCredentials.Resolve(c, "the add-on " + a.Id)!;   // 0.6.17 (ADR-096): kept in Bitwarden or Password Safe, read for this request
                return J(new { name = v.Name, username = v.Username, secret = v.Secret });
            }
            default:
                if (AddonApi.Find(method) is { } m)
                {
                    Need(a, m.Permission);
                    return await m.Run(a, p, ctx, ct);
                }
                throw new AddonError(-32601, "unknown method " + Trim(method, 60) + " (app.capabilities lists them all)");
        }
    }

    private static Credential? AskForCredential(InstalledAddon a, string reason)
    {
        // Shared credentials are offered too, labelled with their library and collection (Credential.Display); picking one is audited there.
        var creds = AppData.Current.Credentials.Concat(SharedCredentials()).ToList();
        if (creds.Count == 0) { Ui.Dialogs.Info("Credential request", $"“{a.Manifest.Name}” asked for a credential, but the vault is empty. Nothing was given."); return null; }
        if (!Ui.Dialogs.Confirm("Add-on asks for a credential",
                $"The add-on “{a.Manifest.Name}” ({a.Manifest.Publisher}, {a.Manifest.Version}) asks for a password or key:\n\n“{reason}”\n\n" +
                "If you continue you pick ONE credential; its secret is handed to that program. It is not remembered — the next request asks again. Deny unless you expected this.",
                "Choose a credential…", danger: true)) return null;
        return Ui.Dialogs.Pick("Credential for " + a.Manifest.Name, "Which credential may this add-on have (this once)?", creds, c => c.Display);
    }

    private static string? AskForFile(InstalledAddon a, string title)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = $"“{a.Manifest.Name}” asks for a file" + (title.Length > 0 ? ": " + title : "") + " — only the file you pick is given to it" };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>UTF-8 line reader with a per-line cap (a missing newline can't make us buffer forever).</summary>
    private sealed class LineReader
    {
        private readonly Stream _s;
        private readonly byte[] _buf = new byte[8192];
        private int _len, _pos;
        private readonly List<byte> _line = new();
        public LineReader(Stream s) { _s = s; }

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                while (_pos < _len)
                {
                    var b = _buf[_pos++];
                    if (b == (byte)'\n') { var s = Encoding.UTF8.GetString(_line.ToArray()).TrimEnd('\r'); _line.Clear(); return s; }
                    _line.Add(b);
                    if (_line.Count > MaxLine) throw new InvalidDataException($"a message longer than {MaxLine / 1024} KB");
                }
                // Pipe reads on a synchronous handle don't observe the token, so race them against it.
                var read = _s.ReadAsync(_buf, 0, _buf.Length);
                var done = await Task.WhenAny(read, Task.Delay(Timeout.Infinite, ct));
                if (done != read) throw new OperationCanceledException(ct);
                _len = await read; _pos = 0;
                if (_len == 0) return _line.Count > 0 ? Encoding.UTF8.GetString(_line.ToArray()) : null;
            }
        }
    }
}
