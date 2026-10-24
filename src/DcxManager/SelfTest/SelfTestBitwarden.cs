using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DcxManager.Bitwarden;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Views;
using static DcxManager.Bitwarden.BitwardenCli;

namespace DcxManager;

/// <summary>
/// ADR-093 (0.6.14, DarkCipher0x: "integrate bitwarden support"). The CLI runner on a real process (a copy of cmd.exe
/// stands in for bw: refused until approved, the environment it gets, a changed file refused, the time limit), then the
/// Password Manager and Secrets Manager protocols against a scripted fake bw / bws (sign-in with the API key, unlock,
/// server changes, sync, lists without secrets, one item read, lock and idle lock, offline mode, refusals before
/// anything runs), host sessions signed in from a vault item, an SSH key and a secret, the clipboard formats, the policies
/// and the screens. No real CLI, no network, no window, demo values only.
/// </summary>
public static class BitwardenTests
{
    private const string ClientId = "user.00000000-test-0000-0000-000000000000";
    private const string ClientSecret = "fake-client-secret-qx7";
    private const string Master = "correct horse battery staple (test)";
    private const string Session = "U0VTU0lPTi1LRVktZm9yLXRlc3RzLW9ubHk=";
    private const string Token = "0.fake-access-token-for-tests.qx7";
    private const string LoginPw = "P@ss-fake-qx7!";
    private const string KeyText = "-----BEGIN OPENSSH PRIVATE KEY-----\nZmFrZSBrZXkgZm9yIHRlc3Rz\n-----END OPENSSH PRIVATE KEY-----";   // secret-scan: example (base64 of "fake key for tests")
    private const string SmValue = "sm-value-fake-qx7";
    private const string TotpUri = "otpauth://totp/demo:root?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=demo";   // RFC 6238's test secret "12345678901234567890"
    private const string LoginId = "11111111-2222-4333-8444-555555555555";
    private const string KeyId = "22222222-3333-4444-8555-666666666666";
    private const string NoteId = "33333333-4444-4555-8666-777777777777";
    private const string ProjectId = "44444444-5555-4666-8777-888888888888";
    private const string SecretId = "55555555-6666-4777-8888-999999999999";

    /// <summary>A scripted bw / bws: what each call returns, and every call it saw.</summary>
    private sealed class FakeCli
    {
        public string State = "unauthenticated", Server = "";
        public readonly List<Call> Calls = new();

        public Task<Result> Answer(Call c, CancellationToken ct)
        {
            lock (Calls) Calls.Add(c);
            return Task.FromResult(c.Tool == Tool.Bws ? Bws(c) : Bw(c));
        }

        private static Result Ok(string s) => new(0, s, "");
        private static Result Err(string s) => new(1, "", s);
        private static bool Has(Call c, string k, string v) => c.Env.TryGetValue(k, out var x) && x == v;

        private Result Bw(Call c)
        {
            var a = c.Args.Where(x => x != "--nointeraction").ToList();
            bool session = Has(c, "BW_SESSION", Session);
            switch (a.FirstOrDefault())
            {
                case "status": return Ok(new JsonObject { ["serverUrl"] = Server.Length > 0 ? Server : null, ["userEmail"] = State == "unauthenticated" ? null : "tester@example.test", ["status"] = session && State == "unlocked" ? "unlocked" : State == "unlocked" ? "locked" : State }.ToJsonString());
                case "config" when a.Count == 3 && a[1] == "server": if (State != "unauthenticated") return Err("Logout required before server config update."); Server = a[2]; return Ok("Saved setting `config`.");
                case "logout": State = "unauthenticated"; return Ok("You have logged out.");
                case "login" when a.Contains("--apikey"):
                    if (!Has(c, "BW_CLIENTID", ClientId) || !Has(c, "BW_CLIENTSECRET", ClientSecret)) return Err("client_id or client_secret is incorrect. Try again.");
                    State = "locked"; return Ok("You are logged in!");
                case "unlock":
                    if (State == "unauthenticated") return Err("You are not logged in.");
                    if (!Has(c, "BW_PASSWORD", Master)) return Err("Invalid master password.");
                    State = "unlocked"; return Ok(Session);
                case "lock": State = "locked"; return Ok("Your vault is locked.");
                case "sync": return session ? Ok("Syncing complete.") : Err("Vault is locked.");
                case "list" when session:
                    var items = new JsonArray
                    {
                        Item(LoginId, 1, "web-01 root (demo)", new JsonObject { ["username"] = "root", ["password"] = LoginPw, ["uris"] = new JsonArray { new JsonObject { ["uri"] = "ssh://198.51.100.61" } } }),
                        Item(KeyId, 5, "deploy key (demo)", null, new JsonObject { ["privateKey"] = KeyText, ["publicKey"] = "ssh-ed25519 AAAAfake", ["keyFingerprint"] = "SHA256:fakeFingerprintQx7" }),
                        Item(NoteId, 2, "secure note (demo)", null),
                        Item("not-an-id", 1, "bad id (demo)", new JsonObject { ["username"] = "x", ["password"] = "y" }),
                    };
                    var search = a.IndexOf("--search") is >= 0 and var i && i + 1 < a.Count ? a[i + 1] : "";
                    return Ok(new JsonArray(items.Where(x => search.Length == 0 || x!["name"]!.GetValue<string>().Contains(search, StringComparison.OrdinalIgnoreCase)).Select(x => x!.DeepClone()).ToArray()).ToJsonString());
                case "get" when session && a.Count == 3 && a[1] == "item":
                    return a[2] == LoginId ? Ok(Item(LoginId, 1, "web-01 root (demo)", new JsonObject { ["username"] = "root", ["password"] = LoginPw, ["totp"] = TotpUri }).ToJsonString())
                        : a[2] == KeyId ? Ok(Item(KeyId, 5, "deploy key (demo)", null, new JsonObject { ["privateKey"] = KeyText }).ToJsonString())
                        : Err("Not found.");
                case "list" or "get": return Err("Vault is locked.");
            }
            return Err("unknown command");
        }

        private static JsonObject Item(string id, int type, string name, JsonObject? login, JsonObject? key = null)
        {
            var o = new JsonObject { ["object"] = "item", ["id"] = id, ["organizationId"] = null, ["type"] = type, ["name"] = name, ["notes"] = "demo note with a secret-looking value" };
            if (login != null) o["login"] = login;
            if (key != null) o["sshKey"] = key;
            return o;
        }

        private static Result Bws(Call c)
        {
            if (!Has(c, "BWS_ACCESS_TOKEN", Token)) return Err("Error: Access token is not in a valid format.");
            var a = c.Args.TakeWhile(x => !x.StartsWith("--", StringComparison.Ordinal)).ToList();
            return string.Join(" ", a.Take(2)) switch
            {
                "project list" => Ok(new JsonArray { new JsonObject { ["object"] = "project", ["id"] = ProjectId, ["name"] = "Lab services (demo)" } }.ToJsonString()),
                "secret list" => Ok(new JsonArray { new JsonObject { ["object"] = "secret", ["id"] = SecretId, ["key"] = "svc-backup password (demo)", ["value"] = SmValue, ["projectId"] = ProjectId, ["note"] = "for 198.51.100.62" } }.ToJsonString()),
                "secret get" when a.Count == 3 && a[2] == SecretId => Ok(new JsonObject { ["object"] = "secret", ["id"] = SecretId, ["key"] = "svc-backup password (demo)", ["value"] = SmValue, ["projectId"] = ProjectId }.ToJsonString()),
                _ => Err("Error: Resource not found."),
            };
        }
    }

    /// <summary>Records the X-API-KEY of each request and answers 200 (nothing leaves the process).</summary>
    private sealed class Capture(List<string> seen) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage req, CancellationToken ct)
        {
            lock (seen) seen.Add(req.Headers.TryGetValues("X-API-KEY", out var v) ? v.First() : "");
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("{}") });
        }
    }

    private static readonly string[] Secrets = { ClientSecret, Master, Session, Token, LoginPw, "ZmFrZSBrZXkgZm9yIHRlc3Rz", SmValue };

    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail, Action<Window, string> snap)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        var s = AppSettings.Current;
        var was = (s.BitwardenEnabled, s.BitwardenServer, s.BitwardenAllowSessions, s.BitwardenAllowCopy, s.BitwardenLockMinutes, s.BitwardenSyncOnUnlock, s.BitwardenCliPins, s.BitwardenCliPath, s.OfflineMode);
        var secretsWas = s.ObserveSecrets.Where(kv => kv.Key.StartsWith("bw:") || kv.Key.StartsWith("bws:")).ToList();
        var dataDirWas = DataDir;
        var resolverWas = Offline.Resolver;
        var scratch = Path.Combine(reportDir, "bitwarden-test");
        Directory.CreateDirectory(scratch);
        DataDir = Path.Combine(scratch, "bw-data");
        try
        {
            // 1. The runner on a real process: a copy of cmd.exe (not signed in its own file) stands in for bw.exe.
            try
            {
                var exe = Path.Combine(scratch, "bw.exe");
                File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe, true);
                ExeForTest = exe; s.BitwardenCliPins = ""; Recheck();
                var t = Check(exe);
                if (t.Ok || !t.Why.Contains("isn't run") || t.Sha256.Length != 64) throw new Exception("an unapproved, unsigned CLI was trusted: " + t.Why);
                try { await BitwardenCli.RunAsync(Tool.Bws, new[] { "/d", "/c", "echo", "x" }); throw new Exception("an unapproved CLI ran"); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("isn't run")) { }
                if (ProtectedSettings.Find("bw.pins")!.Check(JsonValue.Create("abc")) == null || ProtectedSettings.Find("bw.pins")!.Check(JsonValue.Create(t.Sha256 + "," + t.Sha256)) != null) throw new Exception("the approval list's check");
                s.BitwardenCliPins = t.Sha256;
                if (!Check(exe).Ok) throw new Exception("an approved build was refused");
                Environment.SetEnvironmentVariable("NODE_OPTIONS", "--require evil.js");
                Environment.SetEnvironmentVariable("BW_SESSION", "inherited-session");
                Environment.SetEnvironmentVariable("NODE_TLS_REJECT_UNAUTHORIZED", "0");
                Result r;
                try { r = await BitwardenCli.RunAsync(Tool.Bw, new[] { "/d", "/c", "echo", "[%BW_PASSWORD%]", "[%NODE_OPTIONS%]", "[%BW_SESSION%]", "[%NODE_TLS_REJECT_UNAUTHORIZED%]", "[%BW_NOINTERACTION%]", "[%BITWARDENCLI_APPDATA_DIR%]" }, new Dictionary<string, string> { ["BW_PASSWORD"] = "env-only-qx7" }, TimeSpan.FromSeconds(20)); }
                finally { foreach (var v in new[] { "NODE_OPTIONS", "BW_SESSION", "NODE_TLS_REJECT_UNAUTHORIZED" }) Environment.SetEnvironmentVariable(v, null); }
                var o = r.Output.Trim();
                if (r.ExitCode != 0 || !o.Contains("[env-only-qx7]") || !o.Contains("[%NODE_OPTIONS%]") || !o.Contains("[%BW_SESSION%]") || !o.Contains("[%NODE_TLS_REJECT_UNAUTHORIZED%]") || !o.Contains("[true]") || !o.Contains("[" + DataDir + "]") || !o.EndsWith("--nointeraction"))
                    throw new Exception("environment / arguments: " + o);
                try { await BitwardenCli.RunAsync(Tool.Bws, new[] { "/d", "/c", "ping", "-n", "6", "127.0.0.1" }, timeout: TimeSpan.FromSeconds(1)); throw new Exception("no time limit"); }
                catch (TimeoutException) { }
                for (int i = 0; ; i++)   // the killed process may still hold its image for a moment
                    try { await File.AppendAllTextAsync(exe, "x"); break; }
                    catch (IOException) when (i < 25) { await Task.Delay(200); }
                if (Check(exe).Ok) throw new Exception("a changed CLI file stayed trusted");
                s.BitwardenCliPath = Path.Combine(scratch, "bw.cmd"); ExeForTest = null;
                if (Find(Tool.Bw) != null) throw new Exception("a .cmd wrapper was accepted as the CLI");
                s.BitwardenCliPath = was.BitwardenCliPath;
                var env = new Dictionary<string, string?> { ["BW_CLIENTSECRET"] = "x", ["bws_access_token"] = "y", ["NODE_OPTIONS"] = "z", ["NODE_EXTRA_CA_CERTS"] = @"C:\ca.pem", ["PATH"] = "p" };
                Clean(env);
                if (env.Count != 2 || !env.ContainsKey("NODE_EXTRA_CA_CERTS") || !env.ContainsKey("PATH")) throw new Exception("environment cleaning: " + string.Join(",", env.Keys));
                ok("0.6.14 Bitwarden CLI runner (a copy of cmd.exe as bw.exe): refused while neither signed by Bitwarden nor approved (with its SHA-256 to compare), run once an administrator approved that SHA-256; secrets only as environment variables of that process (an inherited BW_SESSION, NODE_OPTIONS and NODE_TLS_REJECT_UNAUTHORIZED were removed, NODE_EXTRA_CA_CERTS kept), BW_NOINTERACTION and the app's own bw folder set, --nointeraction added; a call over its time limit is killed; one changed byte and the file is refused again; a .cmd wrapper is never used");
            }
            catch (Exception ex) { fail("Bitwarden CLI runner", ex); }
            finally { ExeForTest = null; s.BitwardenCliPins = was.BitwardenCliPins; s.BitwardenCliPath = was.BitwardenCliPath; Recheck(); }

            // 1b. 0.6.16 (DarkCipher0x tried it without bw installed and nothing said why): the install button's winget call,
            // and Unlock saying what's missing before it ever asks for the master password.
            var wingetWas = Winget;
            try
            {
                var missingExe = Path.Combine(scratch, "not-here", "bw.exe");
                s.BitwardenEnabled = true; s.BitwardenCliPath = missingExe;
                int prompts = 0;
                BitwardenSessions.MasterPasswordForTest = () => { prompts++; return Master; };
                try { await BitwardenSessions.EnsureUnlockedAsync("test"); throw new Exception("unlock went ahead without bw"); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("isn't installed") && ex.Message.Contains("winget install Bitwarden.CLI")) { }
                if (prompts != 0) throw new Exception("the master password was asked for although bw is missing");
                List<string>? seenArgs = null;
                Winget = (a, ct) => { seenArgs = a.ToList(); return Task.FromResult((-1, "")); };
                try { await InstallBwAsync(); throw new Exception("no error without winget"); } catch (InvalidOperationException ex) when (ex.Message.Contains("winget isn't available")) { }
                if (seenArgs == null || string.Join(" ", seenArgs) != "install --id Bitwarden.CLI -e --source winget --scope user --accept-source-agreements --accept-package-agreements --disable-interactivity")
                    throw new Exception("winget arguments: " + string.Join(" ", seenArgs ?? new()));
                var fakeBw = Path.Combine(scratch, "bw-installed.exe");
                File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fakeBw, true);
                Winget = (a, ct) => { ExeForTest = fakeBw; return Task.FromResult((0, "Successfully installed")); };
                if (await InstallBwAsync() != fakeBw) throw new Exception("the installed CLI wasn't found");
                ExeForTest = null;
                // Through Offline.Set, as a user switches it: everything listening hears it go on and off again.
                Offline.Set(true);
                try { await InstallBwAsync(); throw new Exception("installed in offline mode"); } catch (OfflineBlockedException) { }
                finally { Offline.Set(was.OfflineMode); }
                ok("0.6.16 Bitwarden set-up: with bw missing, Unlock says so (and how to install it) without asking for the master password; “Install Bitwarden CLI” runs winget install --id Bitwarden.CLI -e --source winget --scope user (agreements accepted, no prompt), finds the installed bw.exe, explains a missing winget, and is refused in offline mode");
            }
            catch (Exception ex) { fail("Bitwarden set-up", ex); }
            finally { Winget = wingetWas; ExeForTest = null; BitwardenSessions.MasterPasswordForTest = null; s.BitwardenCliPath = was.BitwardenCliPath; s.OfflineMode = was.OfflineMode; Recheck(); }

            // 2. The Password Manager vault against a scripted bw.
            var fake = new FakeCli();
            Fake = fake.Answer;
            BitwardenVault.ResetForTest();
            s.BitwardenEnabled = false;
            try
            {
                try { await BitwardenVault.StatusAsync(); throw new Exception("used while the integration is off"); } catch (InvalidOperationException ex) when (ex.Message.Contains("off")) { }
                s.BitwardenEnabled = true; s.BitwardenServer = ""; s.BitwardenSyncOnUnlock = true;
                s.ObserveSecrets.Remove("bw:clientid"); s.ObserveSecrets.Remove("bw:clientsecret");
                try { await BitwardenVault.UnlockAsync(Master); throw new Exception("signed in without an API key"); } catch (InvalidOperationException ex) when (ex.Message.Contains("API key")) { }
                s.ObserveSecrets["bw:clientid"] = ClientId; s.ObserveSecrets["bw:clientsecret"] = ClientSecret;
                try { await BitwardenVault.UnlockAsync("wrong password"); throw new Exception("a wrong master password unlocked"); } catch (InvalidOperationException ex) when (ex.Message.Contains("Invalid master password")) { }
                if (BitwardenVault.Unlocked) throw new Exception("unlocked after a wrong password");
                fake.Calls.Clear();
                await BitwardenVault.UnlockAsync(Master); await BitwardenVault.WarmForTest;
                var steps = fake.Calls.Select(c => c.Args[0]).ToList();
                if (!BitwardenVault.Unlocked || string.Join(",", steps) != "status,unlock,list,sync,list") throw new Exception("unlock steps: " + string.Join(",", steps));   // 0.6.20: the list and the sync follow in the background
                var items = await BitwardenVault.ItemsAsync();
                if (items.Count != 2 || items.All(i => i.Type != 5) || items.First(i => i.Type == 1) is not { Username: "root", HasPassword: true, Uri: "ssh://198.51.100.61" } || items.First(i => i.Type == 5).Fingerprint != "SHA256:fakeFingerprintQx7")
                    throw new Exception("items: " + string.Join("; ", items));
                if (items.SelectMany(i => typeof(VaultItem).GetProperties().Select(p => p.GetValue(i)?.ToString() ?? "")).Any(v => Secrets.Any(v.Contains))) throw new Exception("a listed item kept a secret");
                if ((await BitwardenVault.ItemsAsync("deploy")).Count != 1) throw new Exception("search");
                var pw = await BitwardenVault.SecretAsync(LoginId);
                var key = await BitwardenVault.SecretAsync(KeyId);
                if (pw is not { Secret: LoginPw, Username: "root", IsKey: false } || key is not { Secret: KeyText, IsKey: true } || pw.ToString().Contains(LoginPw)) throw new Exception("item secrets");
                int n = fake.Calls.Count;
                foreach (var bad in new[] { "../" + LoginId, "--help", LoginId + " x" })
                    try { await BitwardenVault.SecretAsync(bad); throw new Exception("a bad id was used: " + bad); } catch (ArgumentException) { }
                if ((await BitwardenVault.ItemsAsync("--raw")).Count != 0) throw new Exception("an option-shaped search matched");   // 0.6.20: searched in memory, never passed to bw
                if (fake.Calls.Count != n) throw new Exception("a refused request still started the CLI");
                // Offline: syncing (network) refused, reading the unlocked cache allowed.
                s.OfflineMode = true; Offline.Resolver = (h, ct) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.90") });
                try { await BitwardenVault.SyncAsync(); throw new Exception("synced in offline mode"); } catch (OfflineBlockedException) { }
                if ((await BitwardenVault.SecretAsync(LoginId)).Secret != LoginPw) throw new Exception("the cached vault wasn't readable offline");
                s.OfflineMode = was.OfflineMode; Offline.Resolver = resolverWas;
                // Lock: the session key goes, bw is told (with it), reads stop.
                BitwardenVault.Lock("self-test");
                await Task.Delay(300);
                if (BitwardenVault.Unlocked || fake.Calls.LastOrDefault() is not { } lc || lc.Args[0] != "lock" || !lc.Env.ContainsKey("BW_SESSION")) throw new Exception("lock");
                try { await BitwardenVault.SecretAsync(LoginId); throw new Exception("read while locked"); } catch (InvalidOperationException ex) when (ex.Message.Contains("locked")) { }
                // Idle lock.
                await BitwardenVault.UnlockAsync(Master); await BitwardenVault.WarmForTest;
                s.BitwardenLockMinutes = 5; BitwardenVault.AgeForTest(TimeSpan.FromMinutes(6)); BitwardenVault.IdleCheckForTest();
                if (BitwardenVault.Unlocked) throw new Exception("no idle lock");
                // A server change signs out first, then points bw at the new server.
                await BitwardenVault.UnlockAsync(Master); await BitwardenVault.WarmForTest;
                s.BitwardenServer = "eu"; fake.Calls.Clear();
                await BitwardenVault.UnlockAsync(Master); await BitwardenVault.WarmForTest;
                if (string.Join(",", fake.Calls.Select(c => string.Join(" ", c.Args.Where(a => a != "--nointeraction")))) is var seq && !seq.StartsWith("status,logout,config server https://vault.bitwarden.eu,login --apikey,unlock --passwordenv BW_PASSWORD --raw")) throw new Exception("server change: " + seq);
                if (fake.Calls.Where(c => c.Tool == Tool.Bw).Any(c => c.Args[^1] != "--nointeraction")) throw new Exception("a bw call without --nointeraction");
                foreach (var bad in new[] { "http://vault.example.test", "https://user@vault.example.test", "https://vault.example.test/?x=1", "ftp://x" })
                    if (ProtectedSettings.BitwardenServerCheck(bad) == null || ProtectedSettings.Find("bw.server")!.Check(JsonValue.Create(bad)) == null) throw new Exception("a bad server passed: " + bad);
                if (ProtectedSettings.BitwardenServerCheck("https://vault.example.test") != null || ProtectedSettings.BitwardenServerCheck("eu") != null) throw new Exception("a good server was refused");
                var leaked = fake.Calls.SelectMany(c => c.Args).Where(a => Secrets.Any(a.Contains)).ToList();
                if (leaked.Count > 0) throw new Exception("a secret on a command line: " + leaked.Count);
                ok($"0.6.14 Bitwarden vault (scripted bw): off = refused; no API key or a wrong master password = refused and still locked; sign-in with the personal API key, unlock and sync in order ({fake.Calls.Count} calls in the last run, every one with --nointeraction and no secret among its arguments); the list keeps a login and an SSH key (a secure note and a malformed id dropped, no password or key kept); search; a password and a key read one item at a time; option-shaped ids and searches refused before bw starts; offline mode refuses the sync but reads the unlocked cache; lock tells bw and stops reads; idle lock; a server change (bitwarden.eu) signs out and re-points bw; http, user names and queries refused as servers");
            }
            catch (Exception ex) { fail("Bitwarden vault", ex); }
            finally { s.OfflineMode = was.OfflineMode; Offline.Resolver = resolverWas; }

            // 3. Secrets Manager against a scripted bws.
            try
            {
                s.ObserveSecrets.Remove("bws:token");
                try { await BitwardenSecrets.ProjectsAsync(); throw new Exception("ran without a token"); } catch (InvalidOperationException ex) when (ex.Message.Contains("access token")) { }
                s.ObserveSecrets["bws:token"] = Token;
                s.BitwardenServer = ""; fake.Calls.Clear();
                var projects = await BitwardenSecrets.ProjectsAsync();
                var list = await BitwardenSecrets.SecretsAsync(ProjectId);
                var value = await BitwardenSecrets.ValueAsync(SecretId);
                if (projects.Count != 1 || list.Count != 1 || list[0].Key != "svc-backup password (demo)" || value.Secret != SmValue) throw new Exception("projects / secrets / value");
                if (list.SelectMany(x => typeof(SmSecret).GetProperties().Select(p => p.GetValue(x)?.ToString() ?? "")).Any(v => v.Contains(SmValue))) throw new Exception("a listed secret kept its value");
                if (fake.Calls.Any(c => c.Args.Contains("--server-url") || c.Args.Any(a => a.Contains(Token)))) throw new Exception("US cloud: no --server-url, and the token never as an argument");
                s.BitwardenServer = "https://vault.example.test"; fake.Calls.Clear();
                await BitwardenSecrets.ProjectsAsync();
                var args = fake.Calls.Single().Args.ToList();
                int si = args.IndexOf("--server-url");
                if (si < 0 || si + 1 >= args.Count || args[si + 1] != "https://vault.example.test") throw new Exception("self-hosted --server-url");
                try { await BitwardenSecrets.SecretsAsync("not-a-project"); throw new Exception("a bad project id ran"); } catch (ArgumentException) { }
                s.OfflineMode = true; Offline.Resolver = (h, ct) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.91") });
                s.BitwardenServer = "";
                try { await BitwardenSecrets.SecretsAsync(); throw new Exception("Secrets Manager reached in offline mode"); } catch (OfflineBlockedException) { }
                ok("0.6.14 Bitwarden Secrets Manager (scripted bws): no token = refused; the access token only as BWS_ACCESS_TOKEN; projects, a project's secrets (keys and notes, values dropped) and one value read; --server-url only for a self-hosted server; a bad project id refused; offline mode refuses every call");
            }
            catch (Exception ex) { fail("Bitwarden Secrets Manager", ex); }
            finally { s.OfflineMode = was.OfflineMode; Offline.Resolver = resolverWas; s.BitwardenServer = ""; }

            // 4. Sessions signed in from Bitwarden; links; the clipboard formats; policies.
            var host = new HostEntry { Name = "web-01 (bitwarden demo)", Address = "198.51.100.61", Username = "", Group = "Lab/Bitwarden" };
            var keyHost = new HostEntry { Name = "deploy-01 (bitwarden demo)", Address = "198.51.100.63", Username = "deploy", Group = "Lab/Bitwarden" };
            var rdp = new HostEntry { Name = "win-01 (bitwarden demo)", Address = "198.51.100.62", Username = "svc-backup", Protocol = "rdp", Group = "Lab/Bitwarden" };
            AppData.Current.Hosts.AddRange(new[] { host, keyHost, rdp });
            try
            {
                BitwardenVault.Lock("self-test"); await Task.Delay(200);
                BitwardenSessions.MasterPasswordForTest = () => Master;
                await BitwardenVault.UnlockAsync(Master); await BitwardenVault.WarmForTest;
                var all = await BitwardenVault.ItemsAsync();
                BitwardenVault.Lock("self-test"); await Task.Delay(200);
                host.BitwardenItem = BitwardenSessions.Link(all.First(i => i.Type == 1));
                keyHost.BitwardenItem = BitwardenSessions.Link(all.First(i => i.Type == 5));
                rdp.BitwardenItem = BitwardenSessions.Link((await BitwardenSecrets.SecretsAsync())[0]);
                var h1 = await BitwardenSessions.PrepareAsync(host);   // locked: the (test) prompt unlocks it
                var h2 = await BitwardenSessions.PrepareAsync(keyHost);
                var h3 = await BitwardenSessions.PrepareAsync(rdp);
                if (h1.Transient is not { Kind: CredentialKind.Password, Secret: LoginPw } || h1.Username != "root" || h1.Auth != AuthMethod.Password) throw new Exception("vault login → session");
                if (h2.Transient is not { Kind: CredentialKind.PrivateKey, Secret: KeyText } || h2.Username != "deploy" || h2.Auth != AuthMethod.PrivateKey) throw new Exception("vault SSH key → session");
                if (h3.Transient is not { Kind: CredentialKind.Password, Secret: SmValue } || h3.Rdp.SignOn != "password") throw new Exception("Secrets Manager secret → Remote Desktop session");
                if (host.Transient != null || keyHost.Transient != null || host.Username != "" || ReferenceEquals(h1, host)) throw new Exception("the saved host was changed");
                if (SshConnector.CredentialFor(h2)?.Secret != KeyText) throw new Exception("the connector doesn't use the in-memory key");
                var stored = System.Text.Json.JsonSerializer.Serialize(h1);
                if (Secrets.Any(stored.Contains)) throw new Exception("a session copy would save its secret");
                s.BitwardenAllowSessions = false;
                try { await BitwardenSessions.PrepareAsync(host); throw new Exception("used for a session while not allowed"); } catch (InvalidOperationException ex) when (ex.Message.Contains("switched off")) { }
                s.BitwardenAllowSessions = true;
                if (BitwardenSessions.Parse("pm:" + LoginId + "|a|b") is not { Label: "a|b" } || BitwardenSessions.Parse("pm:x|y") != null || BitwardenSessions.Parse("xx:" + LoginId + "|a") != null) throw new Exception("link parsing");
                var odd = BitwardenSessions.Link(new VaultItem(LoginId, "evil|name\r\nwith control", 1, "", "", "", true, ""));
                if (odd.Contains('\n') || odd.Count(c => c == '|') != 1 || BitwardenSessions.Parse(odd) is not { Secrets: false }) throw new Exception("labels aren't cleaned: " + odd);
                var month = AuditLog.Months().FirstOrDefault();
                var audit = month == null ? "" : string.Join("\n", AuditLog.Read(month).Where(e => e.Time > DateTime.Now.AddMinutes(-10)).Select(e => $"{e.Target} {e.Action} {e.Result}"));
                if (!audit.Contains("Bitwarden credential used for a session") || Secrets.Any(audit.Contains)) throw new Exception("audit: missing, or it holds a secret");
                var d = SecretClipboard.Make(LoginPw);
                if (!d.GetDataPresent("CanIncludeInClipboardHistory") || !d.GetDataPresent("CanUploadToCloudClipboard") || !d.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || (string?)d.GetData(DataFormats.UnicodeText) != LoginPw)
                    throw new Exception("clipboard formats");
                var pol = PolicySettings.Catalog.Where(e => e.Category == "Bitwarden").Select(e => e.Name).ToList();
                if (pol.Count < 10 || !pol.Contains(nameof(AppSettings.BitwardenEnabled)) || !pol.Contains(nameof(AppSettings.BitwardenCliPins))) throw new Exception("policies: " + string.Join(",", pol));
                foreach (var k in new[] { "bw.enabled", "bw.server", "bw.sessions", "bw.copy", "bw.pins" }) if (ProtectedSettings.Find(k) == null) throw new Exception("not admin-protected: " + k);
                ok($"0.6.14 Bitwarden sessions: a locked vault asks for the master password, then a vault login (password + its user name), an SSH key item and a Secrets Manager secret (Remote Desktop) sign their hosts in as in-memory credentials the connector uses; the saved hosts keep only an id and a cleaned label, a session copy serialises without its secret; refused when sessions aren't allowed; the audit log names the item, never the secret; copies are marked to stay out of clipboard history and cloud sync; {pol.Count} Group Policy settings (Bitwarden category) and five admin-protected switches");
            }
            catch (Exception ex) { fail("Bitwarden sessions", ex); }

            // 4b. 0.6.17 (ADR-096; DarkCipher0x: "the password safe and bitwarden creds to be able to be passed to all portions of
            // the software"): vault credentials kept in Bitwarden, used wherever a credential is.
            var extCreds = new List<Credential>();
            try
            {
                BitwardenVault.Lock("self-test"); await Task.Delay(200);
                int asked = 0;
                BitwardenSessions.MasterPasswordForTest = () => { asked++; return Master; };
                await BitwardenVault.UnlockAsync(Master); await BitwardenVault.WarmForTest;
                var all = await BitwardenVault.ItemsAsync();
                var smSecret = (await BitwardenSecrets.SecretsAsync())[0];
                BitwardenVault.Lock("self-test"); await Task.Delay(200);
                CredentialSources.Forget();
                asked = 0;
                var login = CredentialSources.AddToVault(CredentialSources.New(CredentialSources.Link(all.First(i => i.Type == 1)), "web-01 root (demo)", CredentialKind.Password, "root", "")).Cred;
                var key = CredentialSources.AddToVault(CredentialSources.New(CredentialSources.Link(all.First(i => i.Type == 5)), "deploy key (demo)", CredentialKind.PrivateKey, "", "ssh")).Cred;
                var api = CredentialSources.AddToVault(CredentialSources.New(CredentialSources.Link(smSecret), "svc-backup (demo)", CredentialKind.Password, "", "api token")).Cred;
                extCreds.AddRange(new[] { login, key, api });
                if (CredentialSources.AddToVault(CredentialSources.New(login.External, "again", CredentialKind.Password, "", "")) is not { Existed: true } || AppData.Current.Credentials.Count(c => c.External == login.External) != 1)
                    throw new Exception("the same item was added twice");
                // Unattended: a locked vault is never asked for.
                try { using (ExternalCredentials.Background()) await ExternalCredentials.ResolveAsync(login, "a dashboard check"); throw new Exception("read in the background while locked"); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("never ask")) { }
                if (asked != 0) throw new Exception("the background asked for the master password");
                // Synchronous use on the UI thread (the connector): the wait keeps pumping, the (test) prompt unlocks once.
                var extHost = new HostEntry { Name = "ext-01 (bitwarden demo)", Address = "198.51.100.64", Username = "", Group = "Lab/Bitwarden", CredentialId = login.Id, SudoCredentialId = login.Id };
                var linked = new HostEntry { Name = "ext-02 (bitwarden demo)", Address = "198.51.100.65", Username = "deploy", Group = "Lab/Bitwarden", BitwardenItem = BitwardenSessions.Link(all.First(i => i.Type == 5)) };
                AppData.Current.Hosts.AddRange(new[] { extHost, linked });
                if (SshConnector.CredentialFor(extHost) is not { Kind: CredentialKind.Password, Secret: LoginPw, Username: "root", IsExternal: false } || asked != 1)
                    throw new Exception($"host → vault credential in Bitwarden (asked {asked})");
                int gets = fake.Calls.Count(c => c.Args.Contains("get"));
                if (RemoteExec.SudoPassword(extHost) != LoginPw || fake.Calls.Count(c => c.Args.Contains("get")) != gets) throw new Exception("sudo: not read, or read again within the hold");
                if (SshConnector.CredentialFor(linked) is not { Kind: CredentialKind.PrivateKey, Secret: KeyText }) throw new Exception("a host linked straight to an item, outside its session tabs");
                if ((await ExternalCredentials.ResolveAsync(api, "self-test"))?.Secret != SmValue) throw new Exception("Secrets Manager secret");
                // Infrastructure: signed when a request is sent, never when the client is built.
                CredentialSources.Forget();
                var seen = new List<string>();
                int bwsBefore = fake.Calls.Count(c => c.Tool == Tool.Bws);
                using (var invoker = new System.Net.Http.HttpMessageInvoker(new CredentialAuthHandler(() => api.Id, "self-test", (r, c) => r.Headers.TryAddWithoutValidation("X-API-KEY", c.Secret), new Capture(seen))))
                {
                    if (fake.Calls.Count(c => c.Tool == Tool.Bws) != bwsBefore) throw new Exception("building the client read the secret");
                    using var resp = await invoker.SendAsync(new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "https://192.0.2.10/v1/sites"), CancellationToken.None);
                }
                if (seen.SingleOrDefault() != SmValue) throw new Exception("the request wasn't signed from Secrets Manager");
                // Portals: the external entry is offered for sign-in (nothing secret on it); it is read only when filling.
                if (Browser.Portals.SignInCredential(new Browser.PortalBookmark { Title = "demo", Url = "https://198.51.100.64/", CredentialId = login.Id }) is not { IsExternal: true, Secret.Length: 0 })
                    throw new Exception("portal sign-in credential");
                // Nothing secret saved; a key kept in Bitwarden isn't exported from here; a lock forgets what was read.
                var json = System.Text.Json.JsonSerializer.Serialize(AppData.Current.Credentials.Where(c => c.IsExternal).ToList());
                if (Secrets.Any(json.Contains) || extCreds.Any(c => c.Secret.Length > 0)) throw new Exception("a vault credential kept in Bitwarden would save its secret");
                var keyFile = Path.Combine(scratch, "exported-key");
                try { Ssh.KeyTools.ExportPrivateKey(key, keyFile); throw new Exception("a key kept in Bitwarden was exported"); } catch (InvalidOperationException) { }
                if (File.Exists(keyFile)) throw new Exception("a key file was written");
                BitwardenVault.Lock("self-test"); await Task.Delay(200);
                try { using (ExternalCredentials.Background()) await ExternalCredentials.ResolveAsync(login, "after the lock"); throw new Exception("a value outlived the vault lock"); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("never ask")) { }
                s.BitwardenAllowSessions = false;
                try { await ExternalCredentials.ResolveAsync(api, "self-test"); throw new Exception("read while not allowed"); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("switched off")) { }
                s.BitwardenAllowSessions = true;
                var month = AuditLog.Months().FirstOrDefault();
                var audit = month == null ? "" : string.Join("\n", AuditLog.Read(month).Where(e => e.Time > DateTime.Now.AddMinutes(-10)).Select(e => $"{e.Target} {e.Action} {e.Result}"));
                if (!audit.Contains("external credential used") || Secrets.Any(audit.Contains)) throw new Exception("audit: missing, or it holds a secret");
                ok("0.6.17 vault credentials kept in Bitwarden (ADR-096): a login, an SSH key and a Secrets Manager secret added to the vault once each (ids and labels only, nothing secret saved); a dashboard-style background use never asks while locked; a host's SSH sign-in on the UI thread asks once and gets the login, sudo reuses it within the hold, a host linked straight to an item signs in outside its tabs, an infrastructure client is signed when a request goes (not when built), a portal offers it for sign-in; a key isn't exported from here, the vault lock forgets every value, the policy refuses even a held value, and the audit log names, never shows");
            }
            catch (Exception ex) { fail("0.6.17 vault credentials kept in Bitwarden", ex); }
            finally
            {
                AppData.Current.Credentials.RemoveAll(c => extCreds.Contains(c));
                CredentialSources.Forget();
                s.BitwardenAllowSessions = true;
            }

            // 4c. 0.6.19 (DarkCipher0x: "it doesnt have the option for me to search for any bitwarden credentials when editing a
            // host or portal entry … make it easy to search … there could be a lot"): the picker and the editors.
            var pickCreds = new List<Credential>();
            try
            {
                BitwardenVault.Lock("self-test"); await Task.Delay(200);
                CredentialSources.Forget();
                int asked = 0;
                BitwardenSessions.MasterPasswordForTest = () => { asked++; return Master; };
                var srcs = CredentialSearch.Sources();
                if (srcs[0] != CredentialSearch.Vault || !srcs.Contains(CredentialSearch.Bitwarden) || !srcs.Contains(CredentialSearch.Secrets)) throw new Exception("sources: " + string.Join(",", srcs));
                var bwHits = await CredentialSearch.LoadAsync(CredentialSearch.Bitwarden);
                await BitwardenVault.WarmForTest;   // 0.6.20: the sync after the unlock runs in the background
                if (bwHits.Count != 2 || asked != 1) throw new Exception($"Bitwarden list: {bwHits.Count} item(s), asked {asked}");
                int lists = fake.Calls.Count(c => c.Args.Contains("list"));
                await CredentialSearch.LoadAsync(CredentialSearch.Bitwarden);
                if (fake.Calls.Count(c => c.Args.Contains("list")) != lists) throw new Exception("the list was read again within the hold");
                if (CredentialSearch.Filter(bwHits, "root WEB", CredentialKind.Password) is not [{ Name: "web-01 root (demo)" }]) throw new Exception("words in any order and case");
                if (CredentialSearch.Filter(bwHits, "", CredentialKind.PrivateKey) is not [{ Kind: CredentialKind.PrivateKey }]) throw new Exception("keys only for key sign-in");
                if (CredentialSearch.Filter(bwHits, "198.51.100.61", null).Count != 1 || CredentialSearch.Filter(bwHits, "nothing-like-this", null).Count != 0) throw new Exception("search by address");
                if (bwHits.Any(h => Secrets.Any(s => $"{h.Name}{h.User}{h.Where}{h.External}".Contains(s)))) throw new Exception("a secret is in the picker's list");
                // A big library: 5,000 items filter at once, best match first, capped at 500.
                var many = Enumerable.Range(0, 5000).Select(i => new CredentialHit(CredentialSearch.Bitwarden, $"server-{i:0000} (demo)", "root", $"ssh://198.51.100.{i % 250}", CredentialKind.Password, "", null)).ToList();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var f42 = CredentialSearch.Filter(many, "server-42", CredentialKind.Password);
                var fAll = CredentialSearch.Filter(many, "", CredentialKind.Password);
                if (f42.Count != 100 || !f42[0].Name.StartsWith("server-42") || fAll.Count != 500 || sw.ElapsedMilliseconds > 1000) throw new Exception($"5,000 items: {f42.Count} / {fAll.Count} in {sw.ElapsedMilliseconds} ms");
                if ((await CredentialSearch.LoadAsync(CredentialSearch.Secrets)).Count != 1) throw new Exception("Secrets Manager list");
                // Picking an outside item makes it a vault credential once; listed again it is "in the vault" and reused.
                var picked = CredentialSearch.Use(CredentialSearch.Filter(bwHits, "web root", null)[0]);
                pickCreds.Add(picked);
                if (!picked.IsExternal || picked.Secret.Length != 0 || picked.Username != "root" || AppData.Current.FindCredential(picked.Id) == null) throw new Exception("picked item → vault credential");
                var again = CredentialSearch.Filter(await CredentialSearch.LoadAsync(CredentialSearch.Bitwarden), "web root", null)[0];
                if (again.Existing?.Id != picked.Id || CredentialSearch.Use(again).Id != picked.Id || AppData.Current.Credentials.Count(c => c.External == picked.External) != 1) throw new Exception("picked twice → two vault credentials");
                if (CredentialSearch.Filter(await CredentialSearch.LoadAsync(CredentialSearch.Vault), "web-01", null).All(h => h.Existing?.Id != picked.Id)) throw new Exception("this vault doesn't list it");
                // The picker window (built, never shown): typing filters, choosing gives the vault credential.
                var picker = new CredentialPicker(CredentialKind.Password, "a self-test", "", CredentialSearch.Bitwarden);
                await picker.SourceForTest(CredentialSearch.Bitwarden);
                picker.QueryForTest("deploy");
                if (picker.ShownForTest.Count != 0) throw new Exception("an SSH key was offered for a password");
                picker.QueryForTest("web");
                if (picker.ShownForTest.Count != 1 || picker.ChooseForTest(0)?.Id != picked.Id) throw new Exception("picker: " + picker.StatusForTest);
                picker.Window.Close();
                // The host editor: 🔍 Find… selects it, fills the user name, and replaces a direct link (which would win otherwise).
                CredentialPicker.PickForTest = (kind, _) => kind == CredentialKind.Password ? picked : null;
                var hostLinked = new HostEntry { Name = "pick-01 (bitwarden demo)", Address = "198.51.100.66", Username = "", Group = "Lab/Bitwarden", Auth = AuthMethod.Password, BitwardenItem = $"pm:{KeyId}|deploy key (demo)" };
                var editor = new HostEditWindow(hostLinked);
                if (!editor.NoteForTest.Contains("Linked")) throw new Exception("the editor doesn't say the host is linked");
                editor.UserForTest = "";
                editor.FindCredentialForTest();
                editor.FindSudoForTest();
                var built = editor.BuildForTest() ?? throw new Exception("the editor refused the form");
                editor.Close();
                if (built.CredentialId != picked.Id || built.SudoCredentialId != picked.Id || built.Username != "root" || built.BitwardenItem.Length != 0)
                    throw new Exception($"host editor: cred {built.CredentialId == picked.Id}, sudo {built.SudoCredentialId == picked.Id}, user {built.Username}, link {built.BitwardenItem}");
                // An unchanged credential keeps the link (opening and saving a linked host changes nothing).
                var untouched = new HostEditWindow(hostLinked);
                var kept = untouched.BuildForTest();
                untouched.Close();
                if (kept?.BitwardenItem != hostLinked.BitwardenItem) throw new Exception("saving a linked host unchanged dropped its link");
                // The infrastructure editor's checks accept it (Save refused every Bitwarden / Password Safe credential before).
                if (InfraView.NoSecret(picked) || InfraView.NoUserSecret(picked) || !InfraView.NoSecret(new Credential { Name = "empty (demo)" })) throw new Exception("infrastructure Save checks");
                ok("0.6.19 credential picker: this vault, Bitwarden and Secrets Manager searched by any words of the name, user name or address (keys only for key sign-in); a source is read once (names only, no secret) and held; 5,000 items filter in milliseconds, best first, capped at 500; a picked Bitwarden item becomes a vault credential once and is then shown as in the vault; the window filters as you type; the host editor's 🔍 Find… selects it for sign-in and sudo, fills the user name and replaces a direct link (an untouched link is kept); infrastructure Save accepts it");
            }
            catch (Exception ex) { fail("0.6.19 credential picker", ex); }
            finally
            {
                CredentialPicker.PickForTest = null;
                AppData.Current.Credentials.RemoveAll(c => pickCreds.Contains(c));
                CredentialSources.Forget();
            }

            // 4d. 0.6.20 (DarkCipher0x: "the bitwarden search is very very slow. it should also allow you to save your master
            // password encrypted, so that it remembers the next time you use it and launch ServerManager").
            try
            {
                BitwardenVault.Lock("self-test"); await Task.Delay(200);
                BitwardenSessions.Forget("self-test");
                int asked = 0;
                BitwardenSessions.MasterPasswordForTest = () => { asked++; return Master; };
                BitwardenSessions.RememberForTest = true;
                fake.Calls.Clear();
                if (!await BitwardenSessions.EnsureUnlockedAsync("self-test") || asked != 1) throw new Exception("first unlock");
                // The unlock returned before the list and the sync (they follow in the background).
                if (fake.Calls.Any(c => c.Args[0] == "sync" && fake.Calls.IndexOf(c) < fake.Calls.FindIndex(x => x.Args[0] == "unlock"))) throw new Exception("synced before unlocking");
                await BitwardenVault.WarmForTest;
                if (!BitwardenSessions.IsRemembered || s.ObserveSecrets.GetValueOrDefault("bw:master") != Master) throw new Exception("the ticked master password wasn't remembered");
                // Searches answer from memory: no bw started however many there are.
                int before = fake.Calls.Count;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 200; i++) await BitwardenVault.ItemsAsync(i % 2 == 0 ? "root web" : "deploy");
                sw.Stop();
                if (fake.Calls.Count != before) throw new Exception($"a search started bw ({fake.Calls.Count - before} call(s))");
                if ((await BitwardenVault.ItemsAsync("ROOT 198.51")).Count != 1 || (await BitwardenVault.ItemsAsync("deploy")).Count != 1 || (await BitwardenVault.ItemsAsync("")).Count != 2) throw new Exception("in-memory search");
                // A sync reads the list again (so what it brought in is searchable at once).
                await BitwardenVault.SyncAsync();
                if (string.Join(",", fake.Calls.Skip(before).Select(c => c.Args[0])) != "sync,list") throw new Exception("sync: " + string.Join(",", fake.Calls.Skip(before).Select(c => c.Args[0])));
                // Locking drops the list; the remembered password unlocks again without asking.
                BitwardenVault.Lock("self-test"); await Task.Delay(200);
                if (BitwardenVault.ItemsHeld) throw new Exception("the list outlived the lock");
                asked = 0;
                BitwardenSessions.MasterPasswordForTest = () => { asked++; return null; };
                if (!await BitwardenSessions.EnsureUnlockedAsync("self-test") || asked != 0) throw new Exception("the remembered password wasn't used");
                await BitwardenVault.WarmForTest;
                // At start-up (and after the app unlocks): unlocked in the background, never a prompt.
                BitwardenVault.Lock("self-test"); await Task.Delay(200);
                await BitwardenSessions.AutoUnlockAsync("self-test start");
                await BitwardenVault.WarmForTest;
                if (!BitwardenVault.Unlocked || asked != 0 || !BitwardenVault.ItemsHeld) throw new Exception("start-up unlock");
                // A master password changed since: Bitwarden refuses the remembered one, it is forgotten and asked for.
                BitwardenVault.Lock("self-test"); await Task.Delay(200);
                s.ObserveSecrets["bw:master"] = "old master password (demo)";
                BitwardenSessions.MasterPasswordForTest = () => { asked++; return Master; };
                BitwardenSessions.RememberForTest = false;
                if (!await BitwardenSessions.EnsureUnlockedAsync("self-test") || asked != 1 || BitwardenSessions.IsRemembered || s.ObserveSecrets.ContainsKey("bw:master")) throw new Exception("a refused remembered password");
                await BitwardenVault.WarmForTest;
                // At start-up the refused one is forgotten too, with no prompt.
                BitwardenVault.Lock("self-test"); await Task.Delay(200);
                s.ObserveSecrets["bw:master"] = "old master password (demo)"; s.ObserveSecrets["bw:master-server"] = BitwardenVault.ServerUrl(s.BitwardenServer);
                asked = 0;
                await BitwardenSessions.AutoUnlockAsync("self-test start");
                if (BitwardenVault.Unlocked || asked != 0 || s.ObserveSecrets.ContainsKey("bw:master")) throw new Exception("start-up with a refused password");
                // Bound to its server; not used once the policy says no (and deleted at the next unlock).
                s.ObserveSecrets["bw:master"] = Master; s.ObserveSecrets["bw:master-server"] = "https://vault.example.test";
                if (BitwardenSessions.IsRemembered) throw new Exception("used for another server");
                s.ObserveSecrets["bw:master-server"] = BitwardenVault.ServerUrl(s.BitwardenServer);
                s.BitwardenAllowRemember = false;
                if (BitwardenSessions.IsRemembered) throw new Exception("used against the policy");
                asked = 0;
                if (!await BitwardenSessions.EnsureUnlockedAsync("self-test") || asked != 1 || s.ObserveSecrets.ContainsKey("bw:master")) throw new Exception("policy off: not asked or not deleted");
                await BitwardenVault.WarmForTest;
                s.BitwardenAllowRemember = true;
                // TOTP: read with the item (never listed), and RFC 6238's own test values.
                var withTotp = await BitwardenVault.SecretAsync(LoginId);
                if (Totp.Parse(withTotp.Totp) is not { } spec || Totp.Now(spec, DateTimeOffset.FromUnixTimeSeconds(59)).Code != "287082") throw new Exception("the item's TOTP");
                byte[] A(string t) => Encoding.ASCII.GetBytes(t);
                var vectors = new[] { ("SHA1", A("12345678901234567890"), 59L, "94287082"), ("SHA1", A("12345678901234567890"), 1111111109L, "07081804"),
                    ("SHA256", A("12345678901234567890123456789012"), 59L, "46119246"), ("SHA512", A("1234567890123456789012345678901234567890123456789012345678901234"), 59L, "90693936") };
                foreach (var (algo, key, t, want) in vectors)
                    if (Totp.Now(new Totp.Spec(key, 8, 30, algo, false), DateTimeOffset.FromUnixTimeSeconds(t)).Code != want) throw new Exception($"RFC 6238 {algo} at {t}");
                if (Totp.Parse("GEZD GNBV-GY3T QOJQ gezdgnbvgy3tqojq") is not { Digits: 6, Period: 30 } || Totp.Parse("otpauth://hotp/x?secret=GEZDGNBVGY3TQOJQ") != null || Totp.Parse("not base32!") != null
                    || Totp.Parse("otpauth://totp/x?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&digits=8&period=60&algorithm=sha256") is not { Digits: 8, Period: 60, Algorithm: "SHA256" }
                    || Totp.Parse("steam://GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ") is not { } steam || Totp.Now(steam).Code is not { Length: 5 } sc || sc.Any(ch => !"23456789BCDFGHJKMNPQRTVWXY".Contains(ch)))
                    throw new Exception("TOTP settings");
                // Nothing secret in the audit trail.
                if (AuditLog.Months().FirstOrDefault() is { } month && AuditLog.Read(month).Any(e => $"{e.Target}{e.Action}{e.Result}".Contains(Master))) throw new Exception("the master password is in the audit log");
                ok($"0.6.20 Bitwarden speed and remembered master password: the unlock returns at once and the list and sync follow in the background; 200 searches answered from memory in {sw.ElapsedMilliseconds} ms without starting bw (any words, any case); a sync reads the list again; locking drops it; a ticked master password is remembered (encrypted settings) and unlocks without asking, also at start-up; one Bitwarden refuses (changed since) is forgotten and asked for; it is bound to its server and unused and deleted once the policy says no; a login's TOTP is read with it (RFC 6238 SHA1/SHA256/SHA512 vectors, otpauth:// links, bare Base32, Steam); never in the audit log");
            }
            catch (Exception ex) { fail("0.6.20 Bitwarden speed and remembered master password", ex); }
            finally
            {
                BitwardenSessions.MasterPasswordForTest = null; BitwardenSessions.RememberForTest = false;
                BitwardenSessions.Forget("self-test");
                s.BitwardenAllowRemember = true;
            }

            // 5. The screens: VAULT → Bitwarden lists names only; Settings → Integrations → Bitwarden has its six cards.
            try
            {
                main.Navigate("bitwarden"); await Settle(400);
                var view = BitwardenView.Current ?? throw new Exception("no Bitwarden screen");
                int rows = await view.SearchForTest("");
                int sm = await view.SecretsForTest();
                await Settle(300);
                if (rows != 2 || sm != 1) throw new Exception($"rows {rows}, secrets {sm}");
                var texts = new List<string>();
                void Walk(DependencyObject o) { if (o is System.Windows.Controls.TextBlock tb) texts.Add(tb.Text); for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++) Walk(VisualTreeHelper.GetChild(o, i)); }
                Walk(view);
                if (texts.Any(t => Secrets.Any(t.Contains))) throw new Exception("a secret is on the screen");
                snap(main, Path.Combine(reportDir, "shots", "bitwarden.png"));
                main.Navigate("settings"); await Settle(400);
                var sv = SettingsView.Current ?? throw new Exception("no Settings screen");
                sv.RevealIntegration("Bitwarden"); await Settle(300);
                var cards = sv.VisibleCardsForTest;
                if (cards.Count != 6 || !cards.All(c => c.StartsWith("Bitwarden", StringComparison.Ordinal))) throw new Exception("Bitwarden page: " + string.Join(", ", cards));
                sv.RevealIntegration("All"); await Settle(100);
                ok("0.6.14 Bitwarden screens: VAULT → Bitwarden lists the vault's two sign-in items and the secret with no password, key or value anywhere on screen (renders bitwarden.png); Settings → Integrations → Bitwarden shows its six cards");
            }
            catch (Exception ex) { fail("Bitwarden screens", ex); }
            finally
            {
                AppData.Current.Hosts.RemoveAll(h => h.Group == "Lab/Bitwarden");
                try { AppData.Save(); } catch { }
            }
        }
        finally
        {
            Fake = null; ExeForTest = null; BitwardenSessions.MasterPasswordForTest = null;
            BitwardenVault.ResetForTest();
            DataDir = dataDirWas;
            Offline.Resolver = resolverWas;
            (s.BitwardenEnabled, s.BitwardenServer, s.BitwardenAllowSessions, s.BitwardenAllowCopy, s.BitwardenLockMinutes, s.BitwardenSyncOnUnlock, s.BitwardenCliPins, s.BitwardenCliPath, s.OfflineMode) = was;
            foreach (var k in s.ObserveSecrets.Keys.Where(k => k.StartsWith("bw:") || k.StartsWith("bws:")).ToList()) s.ObserveSecrets.Remove(k);
            foreach (var (k, v) in secretsWas) s.ObserveSecrets[k] = v;
            try { Directory.Delete(scratch, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
