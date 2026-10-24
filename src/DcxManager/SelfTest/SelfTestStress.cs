using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// ROADMAP #15 (docs/TESTING.md): deterministic fuzzing of every parser that sees untrusted input, fuzzing of the
/// terminal host bridge, endurance loops with handle / GDI / USER / memory thresholds, and closed-view leak checks
/// (WeakReference). Part of --selftest. DCX_ENDURANCE=N multiplies the loops, DCX_FUZZ_ITER multiplies fuzz iterations,
/// DCX_FUZZ_SEED changes the seed (failures write a repro file to &lt;report dir&gt;\fuzz).
/// </summary>
internal static class StressTests
{
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);

    internal sealed record Sample(int Handles, int Gdi, int User, long PrivateMb, int Threads)
    {
        public override string ToString() => $"{Handles} handles, {Gdi} GDI, {User} USER, {PrivateMb} MB private, {Threads} threads";
        public Sample Minus(Sample b) => new(Handles - b.Handles, Gdi - b.Gdi, User - b.User, PrivateMb - b.PrivateMb, Threads - b.Threads);
    }

    private static async Task Idle(int ms = 0)
    {
        if (ms > 0) await Task.Delay(ms);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    internal static async Task<Sample> TakeAsync()
    {
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); await Idle(60); }
        GC.Collect();
        var p = Process.GetCurrentProcess();
        p.Refresh();
        return new Sample(p.HandleCount, GetGuiResources(p.Handle, 0), GetGuiResources(p.Handle, 1), p.PrivateMemorySize64 >> 20, p.Threads.Count);
    }

    private static int Env(string name, int fallback, int min, int max) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? Math.Clamp(v, min, max) : fallback;

    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail)
    {
        int seed = Env("DCX_FUZZ_SEED", 20261018, int.MinValue, int.MaxValue);
        int fuzzScale = Env("DCX_FUZZ_ITER", 1, 1, 1000);
        int scale = Env("DCX_ENDURANCE", 1, 1, 100);
        var start = await TakeAsync();

        try { ok(await FuzzParsersAsync(seed, fuzzScale, Path.Combine(reportDir, "fuzz"))); }
        catch (Exception ex) { fail("parser fuzzing", ex); }

        try { ok(await FuzzTerminalAsync(main, seed, fuzzScale)); }
        catch (Exception ex) { fail("terminal bridge fuzzing", ex); }

        try { foreach (var line in await EnduranceAsync(main, scale)) ok(line); }
        catch (Exception ex) { fail("endurance", ex); }

        try { ok(await ViewLeaksAsync(main)); }
        catch (Exception ex) { fail("closed views are released", ex); }

        var end = await TakeAsync();
        ok($"resources over the whole stress run: start {start} → end {end} (Δ {end.Minus(start)})");
    }

    // ---- parser fuzzing ------------------------------------------------------------------------------------------------

    private sealed class Fuzzer
    {
        private readonly Random _r;
        private readonly string _dir;
        public readonly List<string> Bugs = new();
        public int Calls, Rejected;
        public readonly Dictionary<string, double> Slowest = new();
        public Fuzzer(int seed, string dir) { _r = new Random(seed); _dir = dir; }

        private static readonly string[] Tokens =
        {
            "\x1b[", "\x1b]8;;", "\x07", "\"", "{", "}", "[", "]", ":", ",", "|", "\n", "\r\n", "\0", "99999999999999999999999", "-1", "1e309", "NaN",
            "\uFFFF", "\uD800", "%", "..", "/", "\\", "password=", "-----BEGIN RSA PRIVATE KEY-----", "Bearer ", "eyJ", "@", "::", "host:", "tag:", // secret-scan: example
            "after:", "\"\"\"", "##HOST\n", "'", "`", "$(", "<", ">", "\t", "-----END", "0x", "%n", "😀",
        };

        public byte[] Mutate(byte[] seed)
        {
            var b = new List<byte>(seed);
            int ops = 1 + _r.Next(4);
            for (int k = 0; k < ops; k++)
            {
                int pos = b.Count == 0 ? 0 : _r.Next(b.Count);
                switch (_r.Next(9))
                {
                    case 0: if (b.Count > 0) b[pos] ^= (byte)(1 << _r.Next(8)); break;
                    case 1: if (b.Count > 0) b.RemoveRange(pos, Math.Min(b.Count - pos, 1 + _r.Next(16))); break;
                    case 2: { var x = new byte[1 + _r.Next(16)]; _r.NextBytes(x); b.InsertRange(pos, x); break; }
                    case 3: if (b.Count > 0) { int len = Math.Min(b.Count - pos, 1 + _r.Next(64)); b.InsertRange(pos, b.GetRange(pos, len)); } break;
                    case 4: b.InsertRange(pos, Encoding.UTF8.GetBytes(Tokens[_r.Next(Tokens.Length)])); break;
                    case 5: if (b.Count > 0) b.RemoveRange(pos, b.Count - pos); break;
                    case 6: if (b.Count > 0) b[pos] = new byte[] { 0, 0xFF, (byte)'"', (byte)'{', (byte)'|', (byte)'\n', 0x1B, (byte)'%', (byte)':' }[_r.Next(9)]; break;
                    case 7: { var t = Encoding.UTF8.GetBytes(Tokens[_r.Next(Tokens.Length)]); int rep = 50 + _r.Next(2000); for (int i = 0; i < rep && b.Count < 200_000; i++) b.InsertRange(pos, t); break; }
                    default: { var x = new byte[_r.Next(512)]; _r.NextBytes(x); b.AddRange(x); break; }
                }
                if (b.Count > 256 * 1024) b.RemoveRange(256 * 1024, b.Count - 256 * 1024);
            }
            return b.ToArray();
        }

        public string MutateText(string s) => Encoding.UTF8.GetString(Mutate(Encoding.UTF8.GetBytes(s)));
        public byte[] RandomBytes(int max) { var x = new byte[_r.Next(max)]; _r.NextBytes(x); return x; }
        public bool Coin(int pct) => _r.Next(100) < pct;

        /// <summary>Exceptions that mean an unchecked assumption (a bug), as opposed to a parser rejecting bad input.</summary>
        private static bool IsBug(Exception e) => e is NullReferenceException or IndexOutOfRangeException or ArgumentOutOfRangeException
            or KeyNotFoundException or InvalidCastException or DivideByZeroException or AccessViolationException or System.Text.RegularExpressions.RegexMatchTimeoutException;

        /// <param name="noThrow">The parser promises not to throw at all (only <paramref name="allowed"/> may escape).</param>
        public async Task RunAsync(string name, int n, Func<int, byte[]> make, Func<byte[], Task> call, TimeSpan budget, bool noThrow = false, Func<Exception, bool>? allowed = null)
        {
            int bugs = 0;
            for (int i = 0; i < n; i++)
            {
                var input = make(i);
                var sw = Stopwatch.StartNew();
                Exception? err = null;
                try { await call(input); }
                catch (Exception ex) { err = ex is AggregateException { InnerException: { } inner } ? inner : ex; }
                sw.Stop();
                Calls++;
                var ms = sw.Elapsed.TotalMilliseconds;
                if (!Slowest.TryGetValue(name, out var s) || ms > s) Slowest[name] = ms;
                string? why = null;
                if (err != null)
                {
                    if (allowed?.Invoke(err) == true) Rejected++;
                    else if (noThrow || IsBug(err)) why = $"{err.GetType().Name}: {err.Message.Split('\n')[0]}";
                    else Rejected++;
                }
                if (why == null && sw.Elapsed > budget) why = $"took {ms:N0} ms (budget {budget.TotalMilliseconds:N0} ms) — possible catastrophic backtracking";
                if (why == null) continue;
                if (bugs++ < 3)
                {
                    Directory.CreateDirectory(_dir);
                    var file = Path.Combine(_dir, $"{name}-{i}.bin");
                    File.WriteAllBytes(file, input);
                    Bugs.Add($"{name} #{i}: {why} (repro {Path.GetFileName(file)}, {input.Length} bytes){(err != null ? "\n" + err.StackTrace?.Split('\n').FirstOrDefault()?.Trim() : "")}");
                }
            }
        }
    }

    private static async Task<string> FuzzParsersAsync(int seed, int scale, string dir)
    {
        var f = new Fuzzer(seed, dir);
        int N = 400 * scale, Few = 40 * scale;
        var quick = TimeSpan.FromMilliseconds(250);
        Func<int, byte[]> From(string sample) { var b = Encoding.UTF8.GetBytes(sample); return i => i % 10 == 0 ? f.RandomBytes(2048) : f.Mutate(b); }
        static string T(byte[] b) => Encoding.UTF8.GetString(b);
        bool Json(Exception e) => e is JsonException or FormatException or InvalidOperationException or ArgumentException or NotSupportedException or OverflowException or InvalidDataException;

        // Server output, terminal / user text, commands
        await f.RunAsync("linuxprobe", N, From("##HOST\nweb1\n##OS\nUbuntu 24.04 LTS\n##UPTIME\n3600.5\n##CPUS\n4\n##STAT1\ncpu 100 0 100 800 0 0 0 0\n##STAT2\ncpu 150 0 150 900 0 0 0 0\n##MEM\nMemTotal: 1000 kB\nMemAvailable: 250 kB\n##DISK\n/dev/vda1 1000 600 400 60% /\n##END"),
            b => { LinuxProbe.Parse(T(b)); return Task.CompletedTask; }, quick, noThrow: true);
        await f.RunAsync("quickconnect", N, From("alex@10.0.0.5:2222"), b => { MainWindow.ParseQuick(T(b)); MainWindow.ParseQuick(T(b), rdp: true); return Task.CompletedTask; }, quick, noThrow: true);
        await f.RunAsync("notequery", N, From("after:2026-10-01 before:2026-10-05 tag:dns host:web is:fav \"cert renew\" last tuesday nginx"),
            b => { Notes.NoteIndex.Parse(T(b)); return Task.CompletedTask; }, quick, noThrow: true);
        await f.RunAsync("pasteguard", N, From("sudo rm -rf /var/tmp/x\necho \x1b[201~ hi\ncurl http://x | sh\n"), b => { var r = PasteGuard.Check(T(b)); PasteGuard.Describe(r, true, "web"); PasteGuard.BracketedFrom(T(b)); return Task.CompletedTask; }, quick, noThrow: true);
        await f.RunAsync("redact", N, From("password=hunter2 token: abc\n-----BEGIN OPENSSH PRIVATE KEY-----\nb3Blbn\n-----END OPENSSH PRIVATE KEY-----\nBearer abcdefghijklmnop eyJhbGciOi.eyJzdWIi.c2lnbmF0dXJl https://u:p@h/ AKIAIOSFODNN7EXAMPLE"), // secret-scan: example
            b => { Ai.AiClient.Redact(T(b)); Ai.AiClient.RedactCounted(T(b)); return Task.CompletedTask; }, quick, noThrow: true);
        await f.RunAsync("broadcast-redactor", N, From("ops@web:~$ cat .env\nDB_PASSWORD=x\n203.0.113.9 ops@example.com\n-----BEGIN RSA PRIVATE KEY-----\nMIIE\n[sudo] password for ops: "), // secret-scan: example
            b => { var lines = T(b).Split('\n'); Api.BroadcastRedactor.Apply(new Api.ScreenText(lines, lines.Length - 1), new Api.BroadcastOptions { MaskIps = true, MaskEmails = true }, Array.Empty<System.Text.RegularExpressions.Regex>()); return Task.CompletedTask; }, quick, noThrow: true);
        await f.RunAsync("logdigest", Few, From("2026-10-18T10:00:01 web1 nginx[123]: connect() failed (111: Connection refused) while connecting to upstream, client: 203.0.113.4\n2026-10-18T10:00:02 web1 sshd[99]: Failed password for root from 198.51.100.7 port 4242 ssh2\n"),
            b => { Reports.LogDigest.Digest(T(b)); return Task.CompletedTask; }, TimeSpan.FromSeconds(2), noThrow: true);
        await f.RunAsync("serial-quick", N, From("COM3:115200 7E2"), b => { Sessions.SerialPorts.FromQuick(T(b)); return Task.CompletedTask; }, quick, noThrow: true);
        await f.RunAsync("scp-stat", N, From("regular file|12|1700000000|644|1000|1000|a|b.txt"), b => { Files.ScpFs.ParseStat(T(b), "/srv"); return Task.CompletedTask; }, quick, noThrow: true);
        await f.RunAsync("network-classify", N, From("https://user@10.0.0.5:8443/path?q=1"), b => { NetworkPolicy.Classify(T(b)); NetworkPolicy.ClassifyHost(T(b)); return Task.CompletedTask; }, quick, noThrow: true);
        await f.RunAsync("help-search", Few, From("how do I reconnect a dropped ssh session"), b => { Services.HelpKb.Search(T(b)); return Task.CompletedTask; }, TimeSpan.FromSeconds(1), noThrow: true);
        await f.RunAsync("color-hex", N, From("#36D2CB"), b => { ThemeManager.ParseHex(T(b)); return Task.CompletedTask; }, quick, noThrow: true);
        await f.RunAsync("vultr-template", N, From("/domains/{parent.domain}/records/{id}?x={now}"), b =>
        {
            Tpl.Fill(T(b), JsonDocument.Parse("{\"id\":\"r 1\"}").RootElement, JsonDocument.Parse("{\"domain\":\"example.com\"}").RootElement, url: true);
            return Task.CompletedTask;
        }, quick, noThrow: true);

        // Remote APIs (JSON from servers)
        await f.RunAsync("docker-cli", N, From("{\"ID\":\"abc123\",\"Names\":\"web\",\"Image\":\"nginx:1.27\",\"State\":\"running\",\"Status\":\"Up 2 hours\",\"Ports\":\"0.0.0.0:80->80/tcp\"}\n{\"ID\":\"def\",\"Names\":\"db\",\"State\":\"exited\"}"),
            b => { Infra.SshDocker.ParseCli(T(b)); return Task.CompletedTask; }, quick, noThrow: true);
        await f.RunAsync("docker-demux", N, i => i % 3 == 0 ? f.RandomBytes(4096) : f.Mutate(new byte[] { 1, 0, 0, 0, 0, 0, 0, 5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 2, 0, 0, 0, 0, 0, 0, 3, (byte)'e', (byte)'r', (byte)'r' }),
            b => { Infra.ApiDocker.Demux(b); return Task.CompletedTask; }, quick, noThrow: true);
        await f.RunAsync("proxmox-resources", N, From("[{\"id\":\"qemu/100\",\"type\":\"qemu\",\"node\":\"pve1\",\"vmid\":100,\"name\":\"web\",\"status\":\"running\",\"maxmem\":2147483648,\"mem\":1073741824,\"cpu\":0.05,\"maxcpu\":2,\"uptime\":3600,\"template\":0},{\"id\":\"storage/pve1/local\",\"type\":\"storage\",\"node\":\"pve1\",\"storage\":\"local\",\"maxdisk\":100,\"disk\":50,\"status\":\"available\"}]"),
            b => { if (TryJson(b) is JsonArray a) Infra.ProxmoxProvider.ParseResources(a); return Task.CompletedTask; }, quick, allowed: Json);
        await f.RunAsync("proxmox-permissions", N, From("{\"/\":{\"Sys.Audit\":1},\"/vms/100\":{\"VM.PowerMgmt\":1,\"VM.Audit\":1}}"),
            b => { if (TryJson(b) is JsonObject o) Infra.ProxmoxProvider.ParsePermissions(o); return Task.CompletedTask; }, quick, allowed: Json);
        await f.RunAsync("unraid-schema", N, From("{\"__schema\":{\"queryType\":{\"name\":\"Query\"},\"mutationType\":{\"name\":\"Mutation\"},\"types\":[{\"name\":\"Query\",\"kind\":\"OBJECT\",\"fields\":[{\"name\":\"docker\",\"args\":[],\"type\":{\"kind\":\"OBJECT\",\"name\":\"Docker\",\"ofType\":null}}]},{\"name\":\"Docker\",\"kind\":\"OBJECT\",\"fields\":[{\"name\":\"containers\",\"args\":[],\"type\":{\"kind\":\"LIST\",\"name\":null,\"ofType\":{\"kind\":\"OBJECT\",\"name\":\"Container\"}}}]}]}}"),
            b => { if (TryJson(b) is JsonObject o) Infra.UnraidProvider.Schema.From(o); return Task.CompletedTask; }, quick, allowed: Json);
        await f.RunAsync("unraid-inventory", N, From("{\"info\":{\"os\":{\"hostname\":\"tower\"},\"cpu\":{\"brand\":\"x\"},\"memory\":{\"total\":1}},\"array\":{\"state\":\"STARTED\",\"capacity\":{\"kilobytes\":{\"free\":\"1\",\"used\":\"2\",\"total\":\"3\"}},\"disks\":[{\"name\":\"disk1\",\"device\":\"sdb\",\"size\":1000,\"temp\":30,\"type\":\"DATA\"}]},\"docker\":{\"containers\":[{\"id\":\"c1\",\"names\":[\"/web\"],\"state\":\"RUNNING\",\"image\":\"nginx\"}]},\"vms\":{\"domains\":[{\"id\":\"v1\",\"name\":\"win\",\"state\":\"SHUTOFF\"}]}}"),
            b => { if (TryJson(b) is JsonObject o) Infra.UnraidProvider.Parse(o, "tower"); return Task.CompletedTask; }, quick, allowed: Json);
        var manifest = "{\"product\":\"DarkCipher ServerManagement\",\"version\":\"0.2.0\",\"released\":\"2026-11-01\",\"notes\":\"Fixes.\",\"packages\":[{\"kind\":\"msi\",\"arch\":\"x64\",\"file\":\"DarkCipherServerManagement-0.2.0-x64.msi\",\"url\":\"DarkCipherServerManagement-0.2.0-x64.msi\",\"bytes\":1000,\"sha256\":\"" + new string('0', 64) + "\"}]}";
        var mu = new Uri("https://darkciphersystems.com/downloads/servermanagement/update.json");
        await f.RunAsync("update-manifest", N, From(manifest), b => { Updater.Parse(T(b), mu, new Version(0, 1, 0)); return Task.CompletedTask; }, quick, noThrow: true);
        // 0.2.0: Azure and Google Cloud answers (errors, sign-in errors, list items) and key files.
        await f.RunAsync("azure-error", N, From("{\"error\":{\"code\":\"AuthorizationFailed\",\"message\":\"The client 'x' does not have authorization.\\nline 2\"}}"),
            b => { foreach (var st in new[] { 400, 403, 404, 429, 503 }) Azure.AzureClient.ErrorText(new CloudResponse(st, T(b), 0, 1, null)); Azure.AzureAuth.SignInError(T(b)); return Task.CompletedTask; }, quick, noThrow: true);
        await f.RunAsync("azure-flatten", N, From("{\"id\":\"/subscriptions/s/resourceGroups/rg-web/providers/Microsoft.Compute/virtualMachines/vm-1\",\"name\":\"vm-1\",\"properties\":{\"instanceView\":{\"statuses\":[{\"code\":\"PowerState/running\"},{\"code\":7}]}}}"),
            b => { using var d = JsonDocument.Parse(b); Azure.AzureClient.Flatten(d.RootElement); return Task.CompletedTask; }, quick, allowed: Json);
        await f.RunAsync("gcp-error", N, From("{\"error\":{\"code\":403,\"message\":\"API disabled\",\"status\":\"PERMISSION_DENIED\",\"errors\":[{\"reason\":\"forbidden\"}],\"details\":[{\"reason\":\"SERVICE_DISABLED\"}]}}"),
            b => { foreach (var st in new[] { 400, 403, 404, 429, 503 }) Gcp.GcpClient.ErrorText(new CloudResponse(st, T(b), 0, 1, null)); Gcp.GcpAuth.SignInError(T(b)); return Task.CompletedTask; }, quick, noThrow: true);
        await f.RunAsync("gcp-flatten", N, From("{\"name\":\"projects/1/locations/europe-west2/services/api\",\"selfLink\":\"https://www.googleapis.com/compute/v1/projects/p/zones/z/instances/vm-1\",\"zone\":\"https://x/zones/z\",\"machineType\":\"https://x/e2-small\",\"network\":\"https://x/n\",\"networkInterfaces\":[{\"networkIP\":\"10.0.0.2\",\"accessConfigs\":[{\"natIP\":\"203.0.113.5\"}]}],\"ipAddresses\":[{\"type\":\"PRIMARY\",\"ipAddress\":\"203.0.113.6\"}]}"),
            b => { using var d = JsonDocument.Parse(b); var row = Gcp.GcpClient.Flatten(d.RootElement); Gcp.GcpProvider.Instance.Normalize(row, "fuzz"); return Task.CompletedTask; }, quick, allowed: Json);
        await f.RunAsync("gcp-keyfile", N, From("{\"type\":\"service_account\",\"project_id\":\"p-1\",\"private_key_id\":\"k\",\"private_key\":\"-----BEGIN PRIVATE KEY-----\\nMIIB\\n-----END PRIVATE KEY-----\\n\",\"client_email\":\"a@p-1.iam.gserviceaccount.com\",\"token_uri\":\"https://oauth2.googleapis.com/token\"}"), // secret-scan: example
            b => { Gcp.GcpAuth.ParseKeyFile(T(b)); return Task.CompletedTask; }, quick, allowed: e => e is CloudApiException);
        await f.RunAsync("cloud-safepath", N, From("/subscriptions/s/resourceGroups/rg 1/providers/Microsoft.Network/dnszones/example.com/A/www"),
            b => { var p = Tpl.SafePath(T(b)); if (p.Contains('?') || p.Contains('#') || p.Split('/').Skip(1).Any(x => x is "" or "." or "..")) throw new InvalidOperationException("unsafe path: " + p); return Task.CompletedTask; }, quick, noThrow: true);

        // Our own HTTP front door, store files, packages, documents
        await f.RunAsync("http-request", N, From("POST /api/v1/incidents/active/events HTTP/1.1\r\nHost: 127.0.0.1:47321\r\nAuthorization: Bearer dcx_x\r\nContent-Type: application/json\r\nContent-Length: 17\r\n\r\n{\"title\":\"disk\"}"),
            async b => { using var ms = new MemoryStream(b); await Api.LocalServer.ReadRequest(ms, new CancellationTokenSource(2000).Token); }, quick, noThrow: true, allowed: e => e is IOException);
        await f.RunAsync("store-parsecheck", N, i => i % 2 == 0 ? f.Mutate(Encoding.UTF8.GetBytes("{\"Hosts\":[{\"Name\":\"a\",\"Address\":\"10.0.0.1\"}],\"Credentials\":[]}")) : f.RandomBytes(1024),
            b => { foreach (var name in new[] { "data.dcx", "settings.dcx", "store.dcx", "reports.dcx", "workspace.dcx", "note-" + new string('a', 32) + ".dcx", "incident-" + new string('b', 32) + ".dcx" }) { try { StoreSchema.ParseCheck(name, b); } catch (Exception e) when (Json(e)) { } } return Task.CompletedTask; }, quick, noThrow: true);
        var tmp = Path.Combine(dir, "tmp");
        Directory.CreateDirectory(tmp);
        var snapSeed = SecureStore.Encrypt(Encoding.UTF8.GetBytes("{\"Version\":1,\"Files\":[]}"), "snapshot");
        await f.RunAsync("snapshot-open", Few, i => i % 2 == 0 ? f.Mutate(snapSeed) : f.RandomBytes(4096),
            b => { var p = Path.Combine(tmp, "x.dcxsnap"); File.WriteAllBytes(p, b); try { Snapshots.Open(p); } catch (InvalidDataException) { } return Task.CompletedTask; }, quick, noThrow: true);
        var zipSeed = MakeAddonZip();
        await f.RunAsync("addon-package", Few, i => i % 3 == 0 ? f.RandomBytes(8192) : f.Mutate(zipSeed),
            b => { var p = Path.Combine(tmp, "x.dcxaddon"); File.WriteAllBytes(p, b); Addons.AddonPackages.Inspect(p); return Task.CompletedTask; }, TimeSpan.FromSeconds(1), noThrow: true);
        var pdfSeed = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj\n4 0 obj<</Length 44>>stream\nBT /F1 12 Tf 20 100 Td (Hello fuzz) Tj ET\nendstream endobj\n5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF");
        // 0.6.16 sweep: what bw / bws, UniFi consoles, api.ui.com and small AI models send back. Stricter than Json(): only a
        // parse error or the parser's own refusal may escape (a shape error such as "the node must be a JsonObject" is a bug).
        bool Shape(Exception e) => e is JsonException || e is InvalidOperationException io && io.Message.StartsWith("“", StringComparison.Ordinal);
        const string G = "11111111-2222-4333-8444-555555555555";
        await f.RunAsync("bitwarden-items", N, From("[{\"id\":\"" + G + "\",\"type\":1,\"name\":\"web\",\"organizationId\":null,\"login\":{\"username\":\"root\",\"password\":\"x\",\"uris\":[{\"uri\":\"ssh://h\"}]}},{\"id\":\"" + G + "\",\"type\":5,\"name\":\"key\",\"sshKey\":{\"privateKey\":\"k\",\"keyFingerprint\":\"SHA256:x\"}},{\"type\":3,\"card\":{\"number\":\"4111111111111111\"}}]"),
            b => { Bitwarden.BitwardenVault.ParseItems(Encoding.UTF8.GetString(b)); return Task.CompletedTask; }, quick, noThrow: true, allowed: Shape);
        await f.RunAsync("bitwarden-secret", N, From("{\"id\":\"" + G + "\",\"type\":1,\"name\":\"web\",\"login\":{\"username\":\"root\",\"password\":\"x\"}}"),
            b => { Bitwarden.BitwardenVault.ParseSecret(Encoding.UTF8.GetString(b)); return Task.CompletedTask; }, quick, noThrow: true, allowed: Shape);
        await f.RunAsync("bws-secrets", N, From("[{\"object\":\"secret\",\"id\":\"" + G + "\",\"key\":\"db\",\"value\":\"v\",\"projectId\":\"" + G + "\",\"note\":\"n\"}]"),
            b => { Bitwarden.BitwardenSecrets.ParseSecrets(Encoding.UTF8.GetString(b)); return Task.CompletedTask; }, quick, noThrow: true, allowed: Shape);
        await f.RunAsync("bws-value", N, From("{\"id\":\"" + G + "\",\"key\":\"db\",\"value\":\"v\"}"),
            b => { Bitwarden.BitwardenSecrets.ParseValue(Encoding.UTF8.GetString(b)); return Task.CompletedTask; }, quick, noThrow: true, allowed: Shape);
        // 0.6.17 (ADR-096): the links a vault credential stores (a backup or an old store could carry anything there).
        await f.RunAsync("vault-credential-links", N, From("bw:pm:" + G + "|web-01 root"),
            b =>
            {
                var s = Encoding.UTF8.GetString(b);
                if (BitwardenSessions.Parse(s.Length > 3 ? s[3..] : s) is { } l && (l.Id.Length != 36 || l.Label.Length is 0 or > 200 || l.Label.Any(char.IsControl)))
                    throw new Exception("a malformed Bitwarden link parsed");
                if (PasswordSafeSessions.Parse(s.Length > 3 ? s[3..] : s) is { } p && (p.System < 0 || p.Account < 0 || p.Label.Any(char.IsControl)))
                    throw new Exception("a malformed Password Safe link parsed");
                _ = new Credential { External = s }.ExternalText;
                return Task.CompletedTask;
            }, quick, noThrow: true);
        // 0.6.20: a sign-in's TOTP setting (a Bitwarden item's or one typed into a credential) and the AI's written tool
        // calls in qwen's name-then-JSON form.
        await f.RunAsync("totp-settings", N, From("otpauth://totp/demo:root?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&digits=8&period=60&algorithm=SHA256"),
            b =>
            {
                if (Totp.Parse(Encoding.UTF8.GetString(b)) is { } t)
                {
                    if (t.Digits is < 5 or > 10 || t.Period is < 10 or > 300 || t.Secret.Length < 10) throw new Exception("an out-of-range TOTP setting parsed: " + t);
                    if (Totp.Now(t).Code.Length != t.Digits) throw new Exception("a TOTP code of the wrong length");
                }
                return Task.CompletedTask;
            }, quick, noThrow: true);
        var callTools = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["type"] = "function", ["function"] = new System.Text.Json.Nodes.JsonObject { ["name"] = "terminal_run", ["parameters"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "object", ["properties"] = new System.Text.Json.Nodes.JsonObject { ["command"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" } }, ["required"] = new System.Text.Json.Nodes.JsonArray { "command" } } } } };
        await f.RunAsync("ai-written-calls", N, From("terminal_run\n{\n  \"command\": \"free -h\",\n  \"purpose\": \"Check the RAM\"\n}"),
            b => { Ai.AiAgent.SalvageTextCalls(Encoding.UTF8.GetString(b), callTools); return Task.CompletedTask; }, quick, noThrow: true);
        await f.RunAsync("unifi-error", N, From("{\"statusCode\":400,\"statusName\":\"BAD_REQUEST\",\"message\":\"vlanId must be at least 2\",\"requestPath\":\"/x\"}"),
            b => { Infra.UniFiProvider.Message(Encoding.UTF8.GetString(b)); return Task.CompletedTask; }, quick, noThrow: true);
        // A hostile console: every request gets the same mutated answer (list page, details and statistics in one object).
        var unifiSeed = "{\"applicationVersion\":\"10.0.162\",\"offset\":0,\"limit\":100,\"count\":1,\"totalCount\":1,\"data\":[{\"id\":\"" + G + "\",\"name\":\"Lab\",\"state\":\"ONLINE\",\"model\":\"U7\",\"macAddress\":\"aa:bb:cc:dd:ee:ff\",\"ipAddress\":\"192.0.2.1\",\"features\":[\"switching\"],\"interfaces\":{\"ports\":[{\"idx\":1,\"state\":\"UP\",\"poe\":{\"enabled\":true,\"state\":\"UP\"}}]},\"uplink\":{\"deviceId\":\"" + G + "\"},\"access\":{\"type\":\"GUEST\",\"authorized\":true},\"enabled\":true,\"vlanId\":20,\"metadata\":{\"origin\":\"USER_DEFINED\"},\"securityConfiguration\":{\"type\":\"OPEN\"}}],\"uptimeSec\":100,\"cpuUtilizationPct\":5}";
        bool UniFiOk(Exception e) => e is JsonException || e.GetType().Name == "UniFiException";
        await f.RunAsync("unifi-inventory", Few, From(unifiSeed), async b =>
        {
            var text = Encoding.UTF8.GetString(b);
            var p = new Infra.UniFiProvider(new Infra.ProviderConfig { Kind = "unifi", Name = "fuzz", Endpoint = "https://unifi.example.test" }, new FakeHttp(_ => text)) { ListCap = 30, DetailCap = 5 };
            await p.InventoryAsync(CancellationToken.None);
        }, TimeSpan.FromSeconds(2), noThrow: true, allowed: UniFiOk);
        var smSeed = "{\"data\":[{\"id\":\"A1:1\",\"siteId\":\"s1\",\"hostId\":\"A1:1\",\"hostName\":\"h\",\"type\":\"console\",\"reportedState\":{\"hostname\":\"h\",\"state\":\"connected\"},\"meta\":{\"desc\":\"Lab\"},\"statistics\":{\"counts\":{\"totalDevice\":1,\"offlineDevice\":0}},\"devices\":[{\"id\":\"d\",\"name\":\"ap\",\"status\":\"online\",\"firmwareStatus\":\"upToDate\"}],\"periods\":[{\"metricTime\":\"t\",\"data\":{\"wan\":{\"avgLatency\":9,\"packetLoss\":0}}}]}],\"httpStatusCode\":200}";
        await f.RunAsync("unifi-sitemanager", Few, From(smSeed), async b =>
        {
            var text = Encoding.UTF8.GetString(b);
            var p = new Infra.UniFiCloudProvider(new Infra.ProviderConfig { Kind = "unifi-cloud", Name = "fuzz" }, new FakeHttp(_ => text)) { ListCap = 30 };
            await p.InventoryAsync(CancellationToken.None);
        }, TimeSpan.FromSeconds(2), noThrow: true, allowed: UniFiOk);
        var aiTools = new JsonArray(new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "terminal_run", ["parameters"] = new JsonObject { ["properties"] = new JsonObject { ["command"] = new JsonObject(), ["purpose"] = new JsonObject() }, ["required"] = new JsonArray("command", "purpose") } } });
        await f.RunAsync("ai-tool-calls", N, From("Checking. <tool_call>{\"name\": \"terminal_run\", \"arguments\": {\"command\": \"awk '{print $1}' /proc/loadavg\", \"purpose\": \"load\"}}}> then ```json\n{\"name\":\"terminal_run\",\"arguments\":{\"command\":\"uptime\"}}\n``` terminal_run(\"df -h\", \"disk\")"),
            b => { var t = Encoding.UTF8.GetString(b); Ai.AiAgent.ParseTextCalls(t, aiTools); Ai.AiAgent.SalvageTextCalls(t, aiTools); Ai.AiAgent.MentionsToolUse(t, aiTools); return Task.CompletedTask; }, quick, noThrow: true);
        await f.RunAsync("pdf-extract", Math.Max(10, Few / 2), i => f.Mutate(pdfSeed),
            async b => { var r = await Documents.PdfText.ExtractAsync(b); _ = r.Label; }, TimeSpan.FromSeconds(5), noThrow: true);
        try { Directory.Delete(tmp, true); } catch { }

        if (f.Bugs.Count > 0) throw new Exception($"{f.Bugs.Count} problem(s) in {f.Calls:N0} fuzzed calls:\n  " + string.Join("\n  ", f.Bugs));
        var slow = f.Slowest.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key} {kv.Value:N0} ms");
        return $"parser fuzzing (seed {seed}): {f.Calls:N0} calls over {f.Slowest.Count} parsers (server output, terminal/paste text, secrets masking, quick connect, note queries, Docker/Proxmox/Unraid JSON, update manifests, local API HTTP, store files, snapshots, add-on zips, PDFs) — no crash, no hang; {f.Rejected:N0} inputs rejected as designed; slowest: {string.Join(", ", slow)}";
    }

    private static JsonNode? TryJson(byte[] b) { try { return JsonNode.Parse(b); } catch (JsonException) { return null; } catch (ArgumentException) { return null; } }

    private static byte[] MakeAddonZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("addon.json").Open()))
                w.Write("{\"api\":2,\"id\":\"com.example.fuzz\",\"name\":\"Fuzz\",\"version\":\"1.0.0\",\"publisher\":\"x\",\"entry\":\"run.ps1\",\"permissions\":[\"hosts.read\"],\"commands\":[{\"id\":\"a\",\"title\":\"A\",\"context\":\"global\"}]}");
            using (var w = new StreamWriter(zip.CreateEntry("run.ps1").Open())) w.Write("exit 0");
        }
        return ms.ToArray();
    }

    // ---- terminal host bridge ---------------------------------------------------------------------------------------------

    private static async Task<string> FuzzTerminalAsync(MainWindow main, int seed, int scale)
    {
        var r = new Random(seed ^ 0x5EED);
        var host = AppData.Current.Hosts.First(h => h.Name == "selftest-host");
        main.OpenTerminal(host, connectOnOpen: false);
        var tv = main.Panes.AllTabs.Select(t => t.Content).OfType<TerminalView>().Last();
        for (int i = 0; i < 80 && !tv.PageReady; i++) await Idle(250);
        if (!tv.PageReady) throw new Exception("terminal page not ready");
        int logBefore = ErrorLogEntries();
        long bytes = 0;
        var named = new (string Name, byte[] Data)[]
        {
            ("OSC 8 javascript:/file: links", Encoding.UTF8.GetBytes("\x1b]8;;javascript:alert(1)\x07click\x1b]8;;\x07 \x1b]8;;file:///c:/\x07x\x1b]8;;\x07")),
            ("100 KB window title", Encoding.UTF8.GetBytes("\x1b]0;" + new string('T', 100_000) + "\x07")),
            ("5,000-parameter SGR", Encoding.UTF8.GetBytes("\x1b[" + string.Join(";", Enumerable.Range(0, 5000)) + "m")),
            ("bracketed-paste toggles", Encoding.UTF8.GetBytes("\x1b[?2004h\x1b[200~ \x1b[201~ \x1b[?2004l")),
            ("50 KB DCS", Encoding.UTF8.GetBytes("\x1bP" + new string('q', 50_000) + "\x1b\\")),
            ("OSC 52 clipboard write", Encoding.UTF8.GetBytes("\x1b]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes("clipboard grab attempt")) + "\x07")),
            ("invalid UTF-8", new byte[] { 0xC0, 0x80, 0xED, 0xA0, 0x80, 0xF8, 0x88, 0x80, 0x80, 0x80, 0xFF, 0xFE }),
            ("huge cursor position", Encoding.UTF8.GetBytes("\x1b[999999999;999999999H")),
            ("huge insert-chars (ICH)", Encoding.UTF8.GetBytes("\x1b[99999999@")),
            ("huge insert-lines (IL)", Encoding.UTF8.GetBytes("\x1b[99999999L")),
            ("huge delete-lines (DL)", Encoding.UTF8.GetBytes("\x1b[99999999M")),
            ("huge scroll up/down (SU/SD)", Encoding.UTF8.GetBytes("\x1b[99999999S\x1b[99999999T")),
            ("huge repeat (REP)", Encoding.UTF8.GetBytes("x\x1b[99999999b")),
            ("huge erase-chars (ECH)", Encoding.UTF8.GetBytes("\x1b[99999999X")),
        };
        // Each hostile input on its own, then the page must still answer — so a hang names its cause.
        foreach (var (nm, data) in named)
        {
            tv.TestFeed(data); bytes += data.Length;
            Api.ScreenText? probe = null;
            for (int i = 0; i < 3 && probe == null; i++) probe = await tv.CaptureScreenAsync();
            if (probe == null) throw new Exception($"the terminal page stopped answering after: {nm}");
        }
        // The clamp itself, and a huge parameter split across two flushes (the second half arrives after the first was sent).
        if (TerminalView.ClampCsi("\x1b[99999999L\x1b[38;2;255;128;0m\x1b[12;40H") != "\x1b[9999L\x1b[38;2;255;128;0m\x1b[12;40H"
            || TerminalView.IncompleteCsiTail("ok\x1b[999") != 2 || TerminalView.IncompleteCsiTail("ok\x1b[5m") != -1 || TerminalView.IncompleteCsiTail("ok\x1b") != 2)
            throw new Exception("CSI clamp helpers");
        tv.TestFeed(Encoding.UTF8.GetBytes("\x1b[9999"));
        if (await tv.CaptureScreenAsync() == null) throw new Exception("page stopped answering (split, first half)");
        tv.TestFeed(Encoding.UTF8.GetBytes("9999L"));
        if (await tv.CaptureScreenAsync() == null) throw new Exception("the terminal page stopped answering after a huge insert-lines split across two writes");
        for (int i = 0; i < 60 * scale; i++)
        {
            var b = new byte[r.Next(1, 16 * 1024)];
            r.NextBytes(b);
            tv.TestFeed(b); bytes += b.Length;
            if (r.Next(4) == 0) await Idle(20);
        }
        // Afterwards the page must still be alive and answering.
        Api.ScreenText? s = null;
        for (int i = 0; i < 20 && s == null; i++) { s = await tv.CaptureScreenAsync(); if (s == null) await Idle(250); }
        if (s == null) throw new Exception("the terminal page stopped answering after the fuzz input");
        tv.TestFeed(Encoding.UTF8.GetBytes("\x1bc\r\nFUZZ-OK-7731\r\n"));
        bool seen = false;
        Api.ScreenText? lastShot = null; int nulls = 0;
        // Up to 30 s (0.6.10: 10 s tripped on a loaded PC with the same seed that passed minutes earlier; s41: 5 s once): the
        // page may still be parsing the random bytes (seed-dependent escape sequences), and a slow render is
        // not a failure; a page that never renders again is.
        for (int i = 0; i < 120 && !seen; i++) { await Idle(250); lastShot = await tv.CaptureScreenAsync(); if (lastShot == null) nulls++; seen = lastShot?.Lines.Any(l => l.Contains("FUZZ-OK-7731")) == true; }
        if (!seen)
        {
            // Say what the page showed instead (0.4.0: the failure is diagnosable from the report alone).
            var shown = lastShot == null ? "the page did not answer" : "screen: " + string.Join(" ⏎ ", lastShot.Lines.Where(l => l.Trim().Length > 0).Take(4).Select(l => l.Length > 60 ? l[..60] + "…" : l));
            throw new Exception($"the terminal didn't render normal text after the fuzz input ({nulls} unanswered captures; {shown}; {bytes:N0} bytes fed)");
        }
        int newErrors = ErrorLogEntries() - logBefore;
        main.CloseTab(main.Panes.AllTabs.First(t => t.Content == tv));
        await Idle(300);
        if (newErrors > 0)
        {
            // Name the newest entry (decrypted; the report stays in the scratch folder) so the failure can be diagnosed.
            var last = App.ReadLogs(1).Split("\n[", StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Split('\n')[0] ?? "";
            throw new Exception($"{newErrors} error-log entr(ies) written while fuzzing the terminal; newest: {last}");
        }
        return $"terminal bridge fuzzing: {Fmt.Bytes(bytes)} of hostile output (OSC 8 javascript:/file: links, 100 KB titles, 5,000-parameter CSI, DCS, OSC 52 clipboard grab, overlong/invalid UTF-8, huge cursor/insert/delete/scroll/repeat parameters — clamped to 9999 even when split across writes, {60 * scale} random binary chunks) — page stayed alive, rendered normal text afterwards, no errors logged";
    }

    private static int ErrorLogEntries()
    {
        try { return Directory.Exists(App.LogDir) ? Directory.EnumerateFiles(App.LogDir, "errors-*.log").Sum(f => File.ReadAllText(f).Split("\r\n\r\n", StringSplitOptions.RemoveEmptyEntries).Length) : 0; }
        catch { return 0; }
    }

    // ---- endurance ---------------------------------------------------------------------------------------------------------

    private sealed record Limit(int Handles, int Gdi, int User, long Mb);

    private static bool Diag => Environment.GetEnvironmentVariable("DCX_HANDLE_DIAG") == "1";

    private static async Task<string> Loop(string name, int n, Limit limit, Func<int, Task> body)
    {
        for (int i = 0; i < 2; i++) await body(-1 - i);   // warm-up: first-use caches and lazy singletons don't count as leaks
        var before = await TakeAsync();
        var (typesBefore, threadsBefore, poolBefore) = (HandleCensus.Take(), HandleCensus.Threads(), ThreadPool.ThreadCount);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++) await body(i);
        sw.Stop();
        var after = await TakeAsync();
        var (typesAfter, threadsAfter, poolAfter) = (HandleCensus.Take(), HandleCensus.Threads(), ThreadPool.ThreadCount);
        var d = after.Minus(before);
        var over = new List<string>();
        if (d.Handles > limit.Handles) over.Add($"handles +{d.Handles} (limit {limit.Handles})");
        if (d.Gdi > limit.Gdi) over.Add($"GDI +{d.Gdi} (limit {limit.Gdi})");
        if (d.User > limit.User) over.Add($"USER +{d.User} (limit {limit.User})");
        if (d.PrivateMb > limit.Mb) over.Add($"memory +{d.PrivateMb} MB (limit {limit.Mb})");
        var line = $"{name} ×{n} in {sw.Elapsed.TotalSeconds:0.0} s: Δ handles {d.Handles:+0;-0;0}, GDI {d.Gdi:+0;-0;0}, USER {d.User:+0;-0;0}, memory {d.PrivateMb:+0;-0;0} MB";
        // What grew, so a failure can be diagnosed from the report (ADR-025; s36c found a starved thread pool this way).
        var why = $"by type: {HandleCensus.Diff(typesBefore, typesAfter)}; threads {d.Threads:+0;-0;0} (by start: {HandleCensus.Diff(threadsBefore, threadsAfter)}), managed pool {poolBefore}→{poolAfter}";
        if (over.Count > 0) throw new Exception($"{line} — LEAK? {string.Join(", ", over)}; {why}");
        if (Diag) line += $" [{why}]";
        return line;
    }

    private static async Task<List<string>> EnduranceAsync(MainWindow main, int scale)
    {
        var lines = new List<string>();
        var host = AppData.Current.Hosts.First(h => h.Name == "selftest-host");

        lines.Add("endurance: " + await Loop("terminal tabs (WebView2) open/close", 12 * scale, new(300, 40, 40, 120), async i =>
        {
            main.OpenTerminal(host, connectOnOpen: false);
            var tv = main.Panes.AllTabs.Select(t => t.Content).OfType<TerminalView>().Last();
            for (int k = 0; k < 60 && !tv.PageReady; k++) await Idle(100);
            tv.TestFeed(Encoding.UTF8.GetBytes($"churn {i}\r\n"));
            main.CloseTab(main.Panes.AllTabs.First(t => t.Content == tv));
            await Idle(50);
        }));

        var dock = AiModule.Dock;
        lines.Add("endurance: " + await Loop("AI sidebar detach/reattach", 20 * scale, new(120, 30, 30, 40), async _ =>
        {
            dock.Apply(AiDockState.Detached, persist: false); await Idle(40);
            dock.Apply(AiDockState.Docked, persist: false); await Idle(40);
        }));

        var s = AppSettings.Current;
        var (ui, mono) = (s.UiFont, s.MonoFont);
        lines.Add("endurance: " + await Loop("UI font / size changes", 20 * scale, new(60, 20, 20, 30), async i =>
        {
            Typography.Set(uiFont: i % 2 == 0 ? "Segoe UI" : "Arial", monoFont: i % 2 == 0 ? "Consolas" : "Cascadia Mono", scale: i % 2 == 0 ? 1.0 : 1.1);
            await Idle(30);
        }));
        Typography.Set(uiFont: ui, monoFont: mono, scale: 1.0);

        // A hung Ollama (accepts connections, never answers) is what made this loop grow +100…+180 handles on 2026-10-19
        // (s36c): every Settings open waited out its own 4 s Claude Code check. The fake reproduces that every run, and the
        // real Ollama on the PC can't change the result.
        var (ollamaUrl, ollamaTunnel) = (s.OllamaUrl, s.OllamaTunnelHostId);
        using (var hung = new HungServer())
        {
            s.OllamaUrl = hung.Url; s.OllamaTunnelHostId = null;
            try
            {
                // Settings must really reach the fake (its Claude Code check asks Ollama for its version), or this tests nothing.
                // A Settings tab left open by an earlier test is only re-selected (no new Loaded), so close it first.
                main.Navigate("settings"); await Idle(60);
                if (main.Panes.ActiveTab is { } old && (old.Tag as string) != "fixed") main.CloseTab(old);
                await Idle(60);
                main.Navigate("settings");
                for (int k = 0; k < 60 && hung.Accepted == 0; k++) await Idle(100);
                if (main.Panes.ActiveTab is { } st && (st.Tag as string) != "fixed") main.CloseTab(st);
                if (hung.Accepted == 0)
                {
                    var t0 = Stopwatch.StartNew();
                    var direct = await Ai.ClaudeCode.DetectAsync();
                    throw new Exception($"Settings never asked the fake hung Ollama ({hung.Url}) for its version within 6 s, so the screens loop wouldn't test a hung Ollama. A direct check took {t0.ElapsedMilliseconds} ms, url {direct.OllamaUrl}, version {direct.OllamaVersion ?? "none"}, connections then {hung.Accepted}; problem: {direct.Problem}; {Ai.ClaudeCode.SharedStateForTest}");
                }
                // DCX_HANDLE_DIAG=1: each screen on its own first, to see which one grows.
                if (Diag)
                    foreach (var key in new[] { "settings", "notes", "incidents", "addons", "privacy" })
                    {
                        try
                        {
                            lines.Add("diag: " + await Loop($"screen {key} open/close", 6 * scale, new(int.MaxValue, int.MaxValue, int.MaxValue, long.MaxValue), async _ =>
                            {
                                main.Navigate(key); await Idle(60);
                                var tab = main.Panes.ActiveTab;
                                if (tab != null && (tab.Tag as string) != "fixed") main.CloseTab(tab);
                                await Idle(20);
                            }));
                        }
                        catch (Exception ex) { lines.Add("diag: " + ex.Message); }
                    }
                var screens = await Loop("screens (settings, notes, incidents, add-ons, privacy) open/close", 6 * scale, new(150, 40, 40, 80), async _ =>
                {
                    foreach (var key in new[] { "settings", "notes", "incidents", "addons", "privacy" })
                    {
                        main.Navigate(key); await Idle(60);
                        var tab = main.Panes.ActiveTab;
                        if (tab != null && (tab.Tag as string) != "fixed") main.CloseTab(tab);
                        await Idle(20);
                    }
                });
                lines.Add($"endurance: {screens}, with a hung Ollama ({hung.Accepted} connections accepted, none answered)");

            }
            finally { s.OllamaUrl = ollamaUrl; s.OllamaTunnelHostId = ollamaTunnel; AppSettings.Save(); }
        }

        lines.Add("endurance: " + await Loop("notes create/save/delete", 60 * scale, new(40, 5, 5, 40), async i =>
        {
            var n = Notes.NoteStore.Create("endurance " + i, null, "body " + i);
            n.Body += "\nmore"; Notes.NoteStore.Save(n);
            Notes.NoteStore.Delete(n);
            if (i % 20 == 19) Notes.NoteStore.EmptyTrash();
            await Task.Yield();
        }));
        Notes.NoteStore.EmptyTrash();

        // Local API: many short connections must not leave sockets/handles behind.
        bool apiWas = s.LocalApiEnabled; int portWas = s.LocalApiPort;
        int port; { var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); probe.Start(); port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        s.LocalApiPort = port; s.LocalApiEnabled = true; Api.LocalServer.Sync();
        var (tok, secret) = Api.ApiTokens.Create("endurance", new[] { "status.read" }, TimeSpan.FromMinutes(10));
        using (var http = new System.Net.Http.HttpClient())
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + secret);
            lines.Add("endurance: " + await Loop("local API requests", 150 * scale, new(80, 5, 5, 40), async _ =>
            {
                var resp = await http.GetAsync($"http://127.0.0.1:{port}/api/v1/status");
                if ((int)resp.StatusCode is not (200 or 429)) throw new Exception("API answered " + (int)resp.StatusCode);
                if ((int)resp.StatusCode == 429) await Task.Delay(120);
            }));
        }
        Api.ApiTokens.Revoke(tok); Api.ApiTokens.Purge();
        s.LocalApiEnabled = apiWas; s.LocalApiPort = portWas; Api.LocalServer.Sync();

        if (Rdp.RdpAxHost.AvailableClsid() != null)
        {
            var rh = new HostEntry { Name = "rdp-endurance", Address = "127.0.0.1", Port = 1, Username = "x", Protocol = "rdp", IsAdHoc = true };
            lines.Add("endurance: " + await Loop("Remote Desktop control create/destroy (ActiveX)", 5 * scale, new(150, 40, 40, 80), async _ =>
            {
                var rv = new RdpView(rh, connectOnOpen: false);
                var tab = AddTab(main, rv);
                for (int k = 0; k < 40 && !rv.ControlCreated; k++) await Idle(100);
                main.CloseTab(tab);
                await Idle(100);
            }));
        }
        return lines;
    }

    /// <summary>A loopback server that accepts connections and never answers (a hung Ollama, s36c).</summary>
    private sealed class HungServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _l = new(System.Net.IPAddress.Loopback, 0);
        private readonly List<System.Net.Sockets.TcpClient> _held = new();
        private int _accepted;
        public int Accepted => Volatile.Read(ref _accepted);
        public string Url { get; }

        public HungServer()
        {
            _l.Start();
            Url = $"http://127.0.0.1:{((System.Net.IPEndPoint)_l.LocalEndpoint).Port}";
            _ = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var c = await _l.AcceptTcpClientAsync();
                    lock (_held) _held.Add(c);
                    Interlocked.Increment(ref _accepted);
                }
            }
            catch { }   // stopped
        }

        public void Dispose()
        {
            _l.Stop();
            lock (_held) { foreach (var c in _held) c.Dispose(); _held.Clear(); }
        }
    }

    private static System.Windows.Controls.TabItem AddTab(MainWindow main, FrameworkElement content) => main.AddTab("🖥 endurance", content);

    // ---- closed views are released ------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Ref, System.Windows.Controls.TabItem Tab) OpenScreen(MainWindow main, string key)
    {
        main.Navigate(key);
        var tab = main.Panes.ActiveTab!;
        return (new WeakReference(tab.Content), tab);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenTerminalWeak(MainWindow main, HostEntry h)
    {
        main.OpenTerminal(h, connectOnOpen: false);
        return new WeakReference(main.Panes.AllTabs.Select(t => t.Content).OfType<TerminalView>().Last());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CloseContent(MainWindow main, WeakReference r)
    {
        var tab = main.Panes.AllTabs.FirstOrDefault(t => ReferenceEquals(t.Content, r.Target));
        if (tab != null) main.CloseTab(tab);
    }

    private static async Task<string> ViewLeaksAsync(MainWindow main)
    {
        var refs = new List<(string Name, WeakReference Ref)>();
        foreach (var key in new[] { "settings", "notes", "incidents", "addons", "privacy", "commander", "serial", "infra", "hosts", "credentials", "knownhosts", "snippets", "tunnels", "lan", "toolbox", "ansible", "reports", "observe", "help", "audit", "apiexplorer", "assistant" })
        {
            var (r, _) = OpenScreen(main, key);
            await Idle(250);
            CloseContent(main, r);
            await Idle(100);
            refs.Add((key, r));
        }
        var tr = OpenTerminalWeak(main, AppData.Current.Hosts.First(h => h.Name == "selftest-host"));
        for (int k = 0; k < 60 && (tr.Target as TerminalView)?.PageReady != true; k++) await Idle(100);
        CloseContent(main, tr);
        refs.Add(("terminal", tr));
        // Something else must be on screen, or the last closed view can stay referenced by focus/visual state.
        main.Navigate("dashboard");
        // Poll: work a view started (a network refresh with its own timeout) may hold it briefly; a leak holds it forever.
        var alive = refs.Select(x => x.Name).ToList();
        for (int i = 0; i < 30 && alive.Count > 0; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await Idle(500);
            alive = refs.Where(x => x.Ref.IsAlive).Select(x => x.Name).ToList();
        }
        if (alive.Count > 0) throw new Exception("still in memory after closing their tab: " + string.Join(", ", alive) + " (a static event or field keeps them alive)");
        return $"closed views are released: {string.Join(", ", refs.Select(r => r.Name))} — all collected after their tab closed (no static event or field keeps them alive)";
    }
}
