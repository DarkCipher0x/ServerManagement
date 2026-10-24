using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Ai;

// AiClient: model lists and Foundry Local (moved unchanged from AiClient.cs in 0.2.0 phase 6½).
public static partial class AiClient
{
    // ---- models + Foundry Local -----------------------------------------------------------------------------------

    public static async Task<List<string>> ModelsAsync(CancellationToken ct = default)
    {
        switch (Engine)
        {
            case "off": return new();
            case "openai":
            case "npu":
            {
                var url = await BaseUrlAsync(ct);
                using var req = new HttpRequestMessage(HttpMethod.Get, url + "/models");
                if (Engine == "openai" && S.OpenAiKey.Length > 0)
                {
                    if (url.Contains(".azure.com", StringComparison.OrdinalIgnoreCase)) req.Headers.Add("api-key", S.OpenAiKey);
                    else req.Headers.Add("Authorization", "Bearer " + S.OpenAiKey);
                }
                using var resp = await Http.SendAsync(req, ct);
                var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                return node?["data"]?.AsArray().Select(m => m?["id"]?.GetValue<string>() ?? "").Where(s => s.Length > 0).OrderBy(s => s).ToList() ?? new();
            }
            case "anthropic":
            {
                var url = await BaseUrlAsync(ct);
                if (IsLocal(url)) return await OllamaClient.ModelsAsync(ct);   // Ollama behind its Anthropic endpoint
                using var req = new HttpRequestMessage(HttpMethod.Get, url + "/v1/models");
                req.Headers.Add("anthropic-version", "2023-06-01");
                if (S.AnthropicKey.Length > 0) req.Headers.Add("x-api-key", S.AnthropicKey);
                using var resp = await Http.SendAsync(req, ct);
                var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                return node?["data"]?.AsArray().Select(m => m?["id"]?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList() ?? new();
            }
            default: return await OllamaClient.ModelsAsync(ct);
        }
    }

    /// <summary>A small CPU-only Foundry Local model (Qwen2.5 0.5B, ~0.8 GB): light on RAM and power, can call tools.</summary>
    public const string FoundryLightCpuModel = "qwen2.5-0.5b-instruct-generic-cpu";

    /// <summary>Runs the foundry CLI. The self-test swaps in a fake one.</summary>
    internal static Func<string, TimeSpan, CancellationToken, Task<(int Code, string Output)>> FoundryCli =
        (args, timeout, ct) => RunLocalAsync("foundry", args, timeout, ct);

    /// <summary>
    /// Runs <c>foundry server &lt;args&gt;</c> (current CLI); a CLI from before the rename answers "Unknown command"
    /// to that, and then gets <c>foundry service &lt;oldArgs&gt;</c>. Verb = the one that ran last.
    /// </summary>
    public static async Task<(int Code, string Output, string Verb)> FoundryServerAsync(string args, string oldArgs, TimeSpan timeout, CancellationToken ct = default)
    {
        var (code, output) = await FoundryCli("server " + args, timeout, ct);
        if (code is 0 or -1 || !UnknownCommand(output)) return (code, output, "server");
        (code, output) = await FoundryCli("service " + oldArgs, timeout, ct);
        return (code, output, "service");
    }

    /// <summary>The CLI's answer to a verb it doesn't have ("Unknown command: 'service' — Did you mean 'server'?").</summary>
    internal static bool UnknownCommand(string output) =>
        Regex.IsMatch(output, @"unknown command|unrecogni[sz]ed (command|argument)|not a (recogni[sz]ed|valid) command|did you mean", RegexOptions.IgnoreCase);

    /// <summary>The local service URL in `foundry server status` / `foundry service status` output, or null.
    /// Loopback first; a wildcard binding means loopback; port 0 is the "not bound" placeholder.</summary>
    internal static string? FoundryUrlFrom(string output)
    {
        output = Regex.Replace(output, @"\x1B\[[0-9;?]*[A-Za-z]", "");   // colour codes
        var found = Regex.Matches(output, @"(https?)://(127\.0\.0\.1|localhost|\[::1\]|0\.0\.0\.0|\+|\*):(\d{1,5})(?!\d)", RegexOptions.IgnoreCase)
            .Select(m => (Scheme: m.Groups[1].Value.ToLowerInvariant(), Host: m.Groups[2].Value.ToLowerInvariant(), Port: int.Parse(m.Groups[3].Value)))
            .Where(u => u.Port is > 0 and < 65536)
            .Select(u => (u.Scheme, Host: u.Host is "0.0.0.0" or "+" or "*" ? "127.0.0.1" : u.Host, u.Port))
            .OrderBy(u => u.Host == "[::1]" ? 1 : 0)
            .ToList();
        return found.Count == 0 ? null : $"{found[0].Scheme}://{found[0].Host}:{found[0].Port}";
    }

    /// <summary>Foundry Local listens on a dynamic port; `foundry server status` prints it.</summary>
    public static async Task<string> FoundryBaseAsync(CancellationToken ct = default)
    {
        if (S.FoundryUrl.Trim().Length > 0) return S.FoundryUrl.Trim().TrimEnd('/').Replace("/v1", "");
        if (_foundryBase != null) return _foundryBase;
        var (code, output, verb) = await FoundryServerAsync("status", "status", TimeSpan.FromSeconds(15), ct);
        if (code == -1) throw new InvalidOperationException("Foundry Local isn't installed. Install it with: winget install Microsoft.FoundryLocal");
        if (FoundryUrlFrom(output) is not { } url)
            throw new InvalidOperationException(verb == "service"
                ? "Foundry Local isn't running. Start it with: foundry service start (or update Foundry Local: winget upgrade Microsoft.FoundryLocal)"
                : "Foundry Local isn't running. Start it with: foundry server start --idle-timeout 0");
        return _foundryBase = url;
    }

    public static void ForgetFoundry() { _foundryBase = null; _foundryModel = null; }

    private static (string Base, string Wanted, string Id)? _foundryModel;

    /// <summary>
    /// The /v1 endpoint wants a model ID ("Phi-4-mini-instruct-openvino-npu:2"), not the alias ("phi-4-mini") the CLI takes.
    /// Resolves the configured name against the server's models and catalogue; falls back to the name as typed.
    /// </summary>
    internal static async Task<string> FoundryModelIdAsync(string baseUrl, CancellationToken ct)
    {
        var wanted = Model.Trim();
        if (_foundryModel is { } c && c.Base == baseUrl && c.Wanted == wanted) return c.Id;
        var ids = new List<string>();
        var catalog = new List<(string Alias, string Name, string Device)>();
        try
        {
            using var r = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, r.Token);
            var node = JsonNode.Parse(await Http.GetStringAsync(baseUrl + "/v1/models", both.Token));
            ids = node?["data"]?.AsArray().Select(m => m?["id"]?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList() ?? new();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { }
        if (!ids.Any(i => SameFoundryModel(i, wanted)))
            try
            {
                using var r = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, r.Token);
                var node = JsonNode.Parse(await Http.GetStringAsync(baseUrl + "/foundry/list", both.Token));
                var arr = node as JsonArray ?? node?["models"] as JsonArray;
                foreach (var m in arr?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                {
                    string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
                    catalog.Add((Str(m["alias"]), Str(m["name"]), Str(m["runtime"]?["deviceType"])));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { }
        var id = PickFoundryModel(wanted, ids, catalog);
        _foundryModel = (baseUrl, wanted, id);
        return id;
    }

    private static string BareModel(string id) => id.Split(':')[0];
    private static bool SameFoundryModel(string a, string b) => string.Equals(BareModel(a), BareModel(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Exact ID wins (so a deliberately chosen CPU variant stays on the CPU); then the alias's variants, NPU → GPU → CPU,
    /// preferring one the server already has; then an ID that starts with the alias.</summary>
    internal static string PickFoundryModel(string wanted, IReadOnlyList<string> ids, IReadOnlyList<(string Alias, string Name, string Device)> catalog)
    {
        if (wanted.Length == 0) return wanted;
        if (ids.FirstOrDefault(i => SameFoundryModel(i, wanted)) is { } exact) return exact;
        static int Rank(string device) => device.ToLowerInvariant() switch { var d when d.Contains("npu") => 0, var d when d.Contains("gpu") => 1, var d when d.Contains("cpu") => 2, _ => 3 };
        var variants = catalog.Where(m => m.Name.Length > 0 && string.Equals(m.Alias, wanted, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => Rank(m.Device.Length > 0 ? m.Device : m.Name)).Select(m => m.Name).ToList();
        foreach (var v in variants)
            if (ids.FirstOrDefault(i => SameFoundryModel(i, v)) is { } have) return have;
        if (variants.Count > 0) return variants[0];
        var prefixed = ids.Where(i => BareModel(i).StartsWith(wanted + "-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => BareModel(i)[(wanted.Length + 1)..].StartsWith("instruct", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(i => Rank(i)).ToList();
        return prefixed.Count > 0 ? prefixed[0] : wanted;
    }

    /// <summary>Runs a local CLI (foundry, ollama, claude) without a window. code -1 = not found.</summary>
    /// <remarks>Output is read until the process exits plus a moment: a daemon it starts (foundry server start) may inherit the pipes and never close them.</remarks>
    public static async Task<(int Code, string Output)> RunLocalAsync(string exe, string args, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "");
            var output = new StringBuilder();
            // Redirected process pipes are synchronous handles, so ReadAsync on them blocks a thread-pool thread until the
            // child exits. Two per running CLI starved the pool when Settings was reopened quickly (it starts `foundry status`
            // and `where claude` each time); the pool then added threads and the endurance check saw +150 handles (s36c).
            // A short-lived thread of its own per pipe ends when the pipe closes and leaves the pool alone.
            Task Pump(System.IO.StreamReader r)
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                new Thread(() =>
                {
                    var buf = new char[4096];
                    try { int n; while ((n = r.Read(buf, 0, buf.Length)) > 0) lock (output) output.Append(buf, 0, n); }
                    catch { }
                    done.TrySetResult();
                }) { IsBackground = true, Name = "cli output " + exe }.Start();
                return done.Task;
            }
            var pumps = Task.WhenAll(Pump(p.StandardOutput), Pump(p.StandardError));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (124, "timed out"); }
            await Task.WhenAny(pumps, Task.Delay(2000, CancellationToken.None));
            lock (output) return (p.ExitCode, output.ToString());
        }
        catch (System.ComponentModel.Win32Exception) { return (-1, ""); }
    }
}
