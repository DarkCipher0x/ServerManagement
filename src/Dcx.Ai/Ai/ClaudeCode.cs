using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DcxManager.Reports;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager.Ai;

/// <summary>
/// Claude Code (Anthropic's agentic CLI) running on this PC with a LOCAL Ollama model as its brain. Ollama 0.14+ speaks the
/// Anthropic Messages API, so Claude Code is pointed at it with ANTHROPIC_BASE_URL — prompts and server data stay on
/// machines you control. (Claude's own models don't run locally; this is the Claude Code agent + your open model.)
/// </summary>
public static class ClaudeCode
{
    public sealed record Status(string? ClaudePath, string? OllamaVersion, string OllamaUrl, bool OllamaNewEnough, string Model, string? Problem);

    public static string Workspace
    {
        get
        {
            var w = AppSettings.Current.ClaudeCodeWorkspace;
            // Documents is often redirected into OneDrive; the reports written here are plain Markdown, so stay local then.
            return w.Trim().Length > 0 ? w.Trim() : StorageGuard.LocalDefault(Environment.SpecialFolder.MyDocuments, Path.Combine(AppInfo.Company, "claude-workspace"), "claude-workspace");
        }
    }

    public static string Model => AppSettings.Current.ClaudeCodeModel.Trim() is { Length: > 0 } m ? m : AppSettings.Current.OllamaModel;

    private static readonly object SharedLock = new();
    private static Task<Status>? _shared;
    private static CancellationTokenSource? _sharedCts;
    private static int _waiters, _calls, _started;

    /// <summary>Self-test: shared-check bookkeeping.</summary>
    internal static string SharedStateForTest { get { lock (SharedLock) return $"{_calls} shared calls, {_started} checks started, {_waiters} waiting, in flight: {_shared is { IsCompleted: false }}"; } }

    /// <summary>
    /// DetectAsync for Settings: a caller that arrives while a check is running joins it instead of starting another, and
    /// the check is cancelled once every screen waiting for it has closed. Null = <paramref name="closed"/> fired first.
    /// A hung Ollama (accepts, never answers) made every Settings open wait out its own 4 s check; quick reopens stacked
    /// them up and grew the thread pool (s36c, the endurance flake). Now it costs at most one waiting request.
    /// </summary>
    public static async Task<Status?> SharedDetectAsync(CancellationToken closed)
    {
        Task<Status> t;
        lock (SharedLock)
        {
            _calls++;
            if (_shared is not { IsCompleted: false })
            {
                _started++;
                _sharedCts = new CancellationTokenSource();
                _shared = DetectAsync(_sharedCts.Token);
            }
            t = _shared;
            _waiters++;
        }
        try { return await t.WaitAsync(closed); }
        catch (OperationCanceledException) when (closed.IsCancellationRequested) { return null; }
        finally
        {
            lock (SharedLock) { if (--_waiters == 0 && _shared == t && !t.IsCompleted) { _sharedCts!.Cancel(); _shared = null; } }
        }
    }

    public static async Task<Status> DetectAsync(CancellationToken ct = default)
    {
        string? path = null;
        var (code, output) = await AiClient.RunLocalAsync("where.exe", "claude", TimeSpan.FromSeconds(5), ct);
        if (code == 0) path = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (path == null)
        {
            var native = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
            if (File.Exists(native)) path = native;
        }
        string url = "http://localhost:11434"; string? ver = null; bool ok = false;
        try
        {
            url = await OllamaClient.BaseUrlAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(4000);
            ver = await OllamaClient.VersionAsync(cts.Token);
            ok = Version.TryParse(Regex.Match(ver, @"^\d+\.\d+(\.\d+)?").Value, out var v) && v >= new Version(0, 14);
        }
        catch { }
        string? problem = path == null ? "Claude Code isn't installed. Install it (PowerShell): irm https://claude.ai/install.ps1 | iex"
            : ver == null ? $"Ollama isn't reachable at {url}. Start Ollama (or set its address / SSH tunnel in Settings)."
            : !ok ? $"Ollama {ver} is too old — Claude Code needs Ollama 0.14 or newer (Anthropic-compatible API). Update Ollama."
            : !AiClient.FeatureOn(AiFeature.ClaudeCode) || !AppSettings.Current.AiEnabled ? "Claude Code integration is switched off in Settings → AI engine."
            : null;
        return new Status(path, ver, url, ok, Model, problem);
    }

    /// <summary>Opens Claude Code in a console window in the workspace, wired to local Ollama.</summary>
    public static async Task LaunchAsync(string? prompt = null)
    {
        var st = await DetectAsync();
        if (st.Problem != null) { Ui.Dialogs.Error("Claude Code (local)", st.Problem); return; }
        // Offline mode (ADR-016): only with an allowed model endpoint (the CLI also runs with its non-essential traffic off).
        try { await Offline.EnforceAsync(st.OllamaUrl, "Claude Code (model endpoint)"); }
        catch (OfflineBlockedException ex) { Offline.Tell(ex); return; }
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, "reports"));
        var guide = Path.Combine(Workspace, "CLAUDE.md");
        if (!File.Exists(guide)) File.WriteAllText(guide, Guide, new UTF8Encoding(false));

        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = false, WorkingDirectory = Workspace };
        psi.ArgumentList.Add("/k");
        psi.ArgumentList.Add(st.ClaudePath!);
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(st.Model);
        if (!string.IsNullOrWhiteSpace(prompt)) psi.ArgumentList.Add(Regex.Replace(prompt, @"[""&|<>^%!]", " "));
        var env = psi.Environment;
        env["ANTHROPIC_BASE_URL"] = st.OllamaUrl;
        env["ANTHROPIC_AUTH_TOKEN"] = "ollama";
        env["ANTHROPIC_API_KEY"] = "";
        foreach (var k in new[] { "ANTHROPIC_MODEL", "ANTHROPIC_SMALL_FAST_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL" }) env[k] = st.Model;
        env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        env["DISABLE_TELEMETRY"] = "1";
        try
        {
            Process.Start(psi);
            AuditLog.Write("you", "claude-code", "launched with local model " + st.Model, st.OllamaUrl);
            Ui.Window.Status($"Claude Code started in {Workspace} using {st.Model} via {st.OllamaUrl}");
        }
        catch (Exception ex) { Ui.Dialogs.Error("Claude Code (local)", ex.Message); }
    }

    /// <summary>Writes the report into the workspace (after consent — it becomes a plain file) and opens Claude Code on it.</summary>
    public static void OpenWithReport(ServerReport r)
    {
        if (!Ui.Dialogs.Confirm("Open in Claude Code", $"Write this report as a plain Markdown file into\n{Path.Combine(Workspace, "reports")}\nand open Claude Code (local model {Model}) to work through it?\n\nThe file isn't encrypted. Secrets in evidence are masked{(AppSettings.Current.AiRedactSecrets ? "" : " — masking is OFF in Settings")}.", "Open")) return;
        Directory.CreateDirectory(Path.Combine(Workspace, "reports"));
        var name = $"report-{r.Created:yyyyMMdd-HHmm}-{string.Concat(r.Hosts.FirstOrDefault()?.Where(char.IsLetterOrDigit) ?? "fleet")}.md";
        var md = ReportEngine.Markdown(r);
        if (AppSettings.Current.AiRedactSecrets) md = AiClient.Redact(md);
        File.WriteAllText(Path.Combine(Workspace, "reports", name), md, new UTF8Encoding(false));
        _ = LaunchAsync($"Read reports/{name} and help me work through the findings most important first. Explain the commands before I run anything.");
    }

    private const string Guide = """
        # Workspace for DarkCipher ServerManagement

        You are helping an experienced Linux / infrastructure engineer. This folder holds reports and notes exported from
        DarkCipher ServerManagement (reports/). The engineer runs changes themselves — explain commands and their impact,
        prefer read-only diagnostics first, and never ask for or print secrets (private keys, passwords, API tokens).
        You are running on a local open-weight model through Ollama, so keep answers focused and verify claims against the files.
        """;
}
