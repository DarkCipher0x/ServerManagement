using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using DcxManager.Services;
using DcxManager.SignIn;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.3.0 §19 / §23: whole-app encryption (every file in the test store sealed), user profiles (index, folders, policy),
/// and directory sign-in with fakes: bindings validated, JWTs verified (RS256 only, claims checked), the Microsoft Entra
/// flow end to end against a fake identity provider (handler + "browser" on 127.0.0.1), the Windows-account sign-in with
/// a substituted identity, LDAP certificate validation (no accept-any; pin), the cached proof and its grace, and the
/// first-run setup window built off screen. No window is shown, nothing leaves the PC.
/// </summary>
public static class ProfileTests
{
    public static async Task RunAsync(Action<string> ok, Action<string, Exception?> fail)
    {
        // ---- whole-app encryption: the test store itself ---------------------------------------------------------------
        try
        {
            AppData.Save(); AuditLog.Write("selftest", "encryption", "audit marker");
            var r = EncryptionAudit.Run(SecureStore.DataDir, Log.Dir);
            if (r.Sealed < 5) throw new Exception("too few files checked: " + r.Sealed);
            var plain = r.Plain.ToList();
            if (plain.Count > 0) throw new Exception("PLAIN files in the store: " + string.Join("; ", plain.Select(p => Path.GetFileName(p.Path) + " (" + p.Note + ")")));
            // A stray plaintext file is caught, and a .dcx that isn't sealed is caught.
            var stray = Path.Combine(SecureStore.DataDir, "stray-selftest.txt"); File.WriteAllText(stray, "hello");
            var fake = Path.Combine(SecureStore.DataDir, "fake-selftest.dcx"); File.WriteAllText(fake, "{\"not\":\"sealed\"}");
            try
            {
                var r2 = EncryptionAudit.Run(SecureStore.DataDir);
                if (r2.Plain.Count() != 2) throw new Exception("stray files not caught: " + r2.Summary);
            }
            finally { File.Delete(stray); File.Delete(fake); }
            if (!EncryptionAudit.Matches("addons/x/1.0/a.dll", "addons/**") || EncryptionAudit.Matches("x/keyring.json", "keyring.json") || !EncryptionAudit.Matches("a.tmp", "*.tmp")) throw new Exception("pattern matching");
            ok($"whole-app encryption: {r.Sealed} file(s) in the store and logs are sealed (AES-256-GCM / sealed lines), {r.ByDesign} plain by design (key ring with wrapped keys, pointers, program files); a stray text file and an unsealed .dcx are reported");
        }
        catch (Exception ex) { fail("whole-app encryption audit", ex); }

        // ---- profiles -----------------------------------------------------------------------------------------------------
        try
        {
            if (Directory.Exists(Profiles.Root)) Directory.Delete(Profiles.Root, true);
            var ix = Profiles.LoadWithDefault();
            if (ix.Profiles.Count != 1 || ix.Profiles[0].Id != Profiles.DefaultId || Profiles.DirFor(Profiles.DefaultId) != SecureStore.DataDir) throw new Exception("default profile");
            var p = Profiles.Create("Lab-qx7");
            var d = Profiles.Create("Work-qx7", new DirectoryBinding { Kind = "windows", Subject = "S-1-5-21-1-2-3-1001", User = "DOMAIN\\demo" });
            ix = Profiles.Load();
            if (ix.Profiles.Count != 3 || !Directory.Exists(Profiles.DirFor(p.Id)) || ix.Profiles.Single(x => x.Id == d.Id).Directory?.Subject != "S-1-5-21-1-2-3-1001") throw new Exception("create / index");
            try { Profiles.Create("lab-QX7"); throw new Exception("a duplicate name was accepted"); } catch (ArgumentException) { }
            try { Profiles.Create("a\u0007"); throw new Exception("a control character was accepted"); } catch (ArgumentException) { }
            try { Profiles.Delete(Profiles.DefaultId); throw new Exception("the default profile was deleted"); } catch (InvalidOperationException) { }
            if (Profiles.Requested(new[] { "--profile", p.Id }) != p.Id || Profiles.Requested(new[] { "--profile", "../x" }) != null) throw new Exception("--profile parsing");
            var text = File.ReadAllText(Profiles.IndexPath);
            if (text.Contains("Secret", StringComparison.OrdinalIgnoreCase) || text.Contains("keyring")) throw new Exception("the index holds more than names");
            Profiles.Delete(p.Id); Profiles.Delete(d.Id);
            if (Directory.Exists(Profiles.DirFor(p.Id)) || Profiles.Load().Profiles.Count != 1) throw new Exception("delete");
            PolicySettings.SourceForTest = h => h == "HKLM" ? new Dictionary<string, (object, Microsoft.Win32.RegistryValueKind)> { ["RequireDirectorySignIn"] = (1, Microsoft.Win32.RegistryValueKind.DWord) } : null;
            try { if (!Profiles.DirectoryRequiredByPolicy() || Profiles.LocalProfilesAllowed()) throw new Exception("the RequireDirectorySignIn policy isn't read"); }
            finally { PolicySettings.SourceForTest = _ => null; }
            ok("profiles: the existing store is the default profile (nothing moved); local and directory profiles get their own folders and index entries (names and kinds only); duplicates, control characters and deleting the default are refused; --profile is validated; the RequireDirectorySignIn machine policy forbids local profiles");
        }
        catch (Exception ex) { fail("profiles", ex); }

        // ---- bindings and JWTs ----------------------------------------------------------------------------------------
        try
        {
            string? P(DirectoryBinding b) => b.Problem();
            if (P(new DirectoryBinding { Kind = "windows", Subject = "S-1-5-21-1-2-3-500" }) != null) throw new Exception("windows binding");
            if (P(new DirectoryBinding { Kind = "windows", Subject = "S-1-5-18" }) == null) throw new Exception("a non-account SID accepted");
            if (P(new DirectoryBinding { Kind = "ldaps", Server = "ldap.example.test", Transport = "plain", User = "a@b" }) == null) throw new Exception("plain LDAP accepted");
            if (P(new DirectoryBinding { Kind = "ldaps", Server = "ldap.example.test", User = "a;b" }) == null) throw new Exception("a bad user accepted");
            if (P(new DirectoryBinding { Kind = "ldaps", Server = "ldap.example.test", User = "demo@example.test", CertPin = "zz" }) == null) throw new Exception("a bad pin accepted");
            if (P(new DirectoryBinding { Kind = "entra", Server = "example.test", ClientId = "not-a-guid", User = "a@b" }) == null) throw new Exception("a bad client id accepted");
            if (P(new DirectoryBinding { Kind = "entra", Server = "example.test", ClientId = "11111111-2222-3333-4444-555555555555", User = "a@b.test", Authority = "http://x" }) == null) throw new Exception("an http authority accepted");
            if (P(new DirectoryBinding { Kind = "entra", Server = "example.test", ClientId = "11111111-2222-3333-4444-555555555555", User = "a@b.test", GroupRoles = { ["g"] = "king" } }) == null) throw new Exception("an unknown role accepted");
            var b2 = new DirectoryBinding { GroupRoles = { ["Ops Admins"] = "admin", ["Everyone"] = "viewer" } };
            if (b2.RoleFor(new[] { "Everyone", "Ops Admins" }) != "admin" || b2.RoleFor(new[] { "Everyone" }) != "viewer" || b2.RoleFor(Array.Empty<string>()) != "operator") throw new Exception("role mapping");

            using var key = RSA.Create(2048); using var other = RSA.Create(2048);
            var jwks = JsonDocument.Parse(JsonSerializer.Serialize(new { keys = new[] { Jwt.Jwk(key, "k1") } })).RootElement;
            var now = DateTimeOffset.UtcNow;
            var claims = new { iss = "https://idp.test/v2.0", aud = "11111111-2222-3333-4444-555555555555", exp = now.AddMinutes(10).ToUnixTimeSeconds(), nbf = now.AddMinutes(-1).ToUnixTimeSeconds(), nonce = "n1", oid = "oid-1", preferred_username = "demo@example.test", name = "DarkCipher0x", groups = new[] { "Ops" } };
            var good = Jwt.Sign(key, "k1", claims);
            var c = Jwt.Verify(good, jwks);
            if (Jwt.CheckClaims(c, "https://idp.test/v2.0", "11111111-2222-3333-4444-555555555555", "n1", now) != null) throw new Exception("good token refused");
            if (Jwt.CheckClaims(c, "https://idp.test/v2.0", "other", "n1", now) == null || Jwt.CheckClaims(c, "https://idp.test/v2.0", "11111111-2222-3333-4444-555555555555", "n2", now) == null || Jwt.CheckClaims(c, "https://idp.test/v2.0", "11111111-2222-3333-4444-555555555555", "n1", now.AddHours(1)) == null) throw new Exception("bad claims accepted");
            try { Jwt.Verify(Jwt.Sign(other, "k1", claims), jwks); throw new Exception("a token signed by another key verified"); } catch (CryptographicException) { }
            var parts = good.Split('.');
            var none = Jwt.B64Url(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"kid\":\"k1\"}")) + "." + parts[1] + ".";
            try { Jwt.Verify(none, jwks); throw new Exception("alg=none accepted"); } catch (CryptographicException) { }
            var hs = Jwt.B64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"kid\":\"k1\"}")) + "." + parts[1] + "." + parts[2];
            try { Jwt.Verify(hs, jwks); throw new Exception("HS256 accepted"); } catch (CryptographicException) { }
            ok("directory bindings: plain LDAP, bad users, pins, client ids, http authorities and unknown roles refused; group → role mapping in order. JWT: RS256 verified against the JWKS by kid; another key, alg=none and HS256 refused; audience, nonce and expiry checked");
        }
        catch (Exception ex) { fail("bindings / JWT", ex); }

        // ---- Entra end to end against a fake identity provider -------------------------------------------------------------
        try
        {
            using var key = RSA.Create(2048);
            const string Tenant = "22222222-2222-2222-2222-222222222222", Client = "11111111-2222-3333-4444-555555555555";
            string issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";
            string? codeIssued = null, nonceSeen = null, challengeSeen = null; int tokenCalls = 0;
            var handler = new FakeHttp(async (req, ct) =>
            {
                var u = req.RequestUri!;
                if (u.AbsolutePath.EndsWith("openid-configuration")) return Json(new { issuer, authorization_endpoint = issuer + "/authorize", token_endpoint = issuer + "/token", jwks_uri = issuer + "/keys" });
                if (u.AbsolutePath.EndsWith("/keys")) return Json(new { keys = new[] { Jwt.Jwk(key, "k9") } });
                if (u.AbsolutePath.EndsWith("/token"))
                {
                    tokenCalls++;
                    var form = await req.Content!.ReadAsStringAsync(ct);
                    var kv = form.Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
                    if (kv["code"] != codeIssued || kv["client_id"] != Client) return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}") };
                    if (Jwt.B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(kv["code_verifier"]))) != challengeSeen) return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\",\"error_description\":\"PKCE\"}") };
                    var now = DateTimeOffset.UtcNow;
                    var idt = Jwt.Sign(key, "k9", new { iss = issuer, aud = Client, exp = now.AddMinutes(5).ToUnixTimeSeconds(), nbf = now.AddMinutes(-1).ToUnixTimeSeconds(), nonce = nonceSeen, tid = Tenant, oid = "oid-demo", preferred_username = "demo@example.test", name = "Demo Example", groups = new[] { "Ops", "Everyone" } });
                    return Json(new { id_token = idt, access_token = "at", token_type = "Bearer" });
                }
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            });
            EntraSignIn.Handler = () => handler;
            EntraSignIn.OpenBrowser = url =>
            {
                // The "browser": reads the request, "signs in", and follows the redirect to the app's loopback listener with a code.
                var q = url.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
                if (q["code_challenge_method"] != "S256" || q["client_id"] != Client || !q["redirect_uri"].StartsWith("http://127.0.0.1:")) throw new Exception("authorize request");
                nonceSeen = q["nonce"]; challengeSeen = q["code_challenge"]; codeIssued = "code-" + Guid.NewGuid().ToString("N");
                var redirect = new Uri(q["redirect_uri"] + "?code=" + codeIssued + "&state=" + Uri.EscapeDataString(q["state"]));
                _ = Task.Run(async () =>
                {
                    // A stray probe without state first (ignored), then the real redirect.
                    foreach (var path in new[] { "/favicon.ico", redirect.PathAndQuery })
                    {
                        using var tcp = new TcpClient(); await tcp.ConnectAsync("127.0.0.1", redirect.Port);
                        var s = tcp.GetStream(); await s.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));
                        var buf = new byte[4096]; await s.ReadAtLeastAsync(buf, 1, throwOnEndOfStream: false);
                    }
                });
            };
            EntraSignIn.BrowserTimeout = TimeSpan.FromSeconds(20);
            var b = new DirectoryBinding { Kind = "entra", Server = Tenant, ClientId = Client, User = "demo@example.test", GroupRoles = { ["Ops"] = "admin" } };
            var r = await new EntraSignIn().SignInAsync(b, _ => null, CancellationToken.None);
            if (!r.Ok || r.Subject != "oid-demo" || r.DisplayName != "Demo Example" || !r.Groups.Contains("Ops") || tokenCalls != 1) throw new Exception("entra: " + (r.Error ?? string.Join(",", r.Groups)));
            var wrongUser = await new EntraSignIn().SignInAsync(new DirectoryBinding { Kind = "entra", Server = Tenant, ClientId = Client, User = "someone-else@example.test" }, _ => null, CancellationToken.None);
            if (wrongUser.Ok || !wrongUser.Error!.Contains("belongs to")) throw new Exception("another account's token was accepted: " + wrongUser.Error);
            ok("Microsoft Entra sign-in (fake identity provider, 127.0.0.1 only): discovery and keys fetched, PKCE S256 + state + nonce sent, the code arrives on the loopback listener (a stray request without state is ignored), the code is exchanged with the verifier, the id_token verified by kid and its claims (issuer, audience, nonce, tenant) checked; a token for another account is refused");
        }
        catch (Exception ex) { fail("Entra sign-in", ex); }
        finally { EntraSignIn.Handler = () => new SocketsHttpHandler(); EntraSignIn.OpenBrowser = _ => throw new InvalidOperationException("no browser in the self-test"); }

        // ---- Windows account, LDAP certificate rules, the cached proof --------------------------------------------------------
        try
        {
            var was = WindowsAccountSignIn.Identity;
            try
            {
                WindowsAccountSignIn.Identity = () => ("S-1-5-21-9-9-9-1001", "EXAMPLE\\demo", "Kerberos", true, new List<string> { "EXAMPLE\\Domain Users", "EXAMPLE\\Ops Admins" });
                var b = new DirectoryBinding { Kind = "windows", Subject = "S-1-5-21-9-9-9-1001", User = "EXAMPLE\\demo", RequiredGroup = "EXAMPLE\\Ops Admins", GroupRoles = { ["EXAMPLE\\Ops Admins"] = "admin" }, OfflineGraceDays = 7 };
                DirectorySignIn.ForgetProof();
                if (await DirectorySignIn.OpenAsync(b, _ => null) is { } why1) throw new Exception("windows sign-in refused: " + why1);
                if (Profiles.Role != "admin" || DirectorySignIn.ReadProof() is not { } proof || proof.Subject != "S-1-5-21-9-9-9-1001") throw new Exception("role / proof");
                WindowsAccountSignIn.Identity = () => ("S-1-5-21-9-9-9-2002", "EXAMPLE\\mallory", "Kerberos", true, new List<string> { "EXAMPLE\\Domain Users" });
                if (await DirectorySignIn.OpenAsync(b, _ => null) is not { } why2 || !why2.Contains("isn't the one")) throw new Exception("another account opened the profile");
                WindowsAccountSignIn.Identity = () => ("S-1-5-21-9-9-9-1001", "EXAMPLE\\demo", "Kerberos", true, new List<string> { "EXAMPLE\\Domain Users" });
                if (await DirectorySignIn.OpenAsync(b, _ => null) is not { } why3 || !why3.Contains("required group")) throw new Exception("the required group wasn't enforced");
                // Offline: the provider fails; the proof from the successful sign-in opens it within the grace, not beyond.
                WindowsAccountSignIn.Identity = () => ("", "", "", false, new List<string>());
                if (await DirectorySignIn.OpenAsync(b, _ => null) != null) throw new Exception("the cached proof didn't open the profile");
                if (await DirectorySignIn.OpenAsync(new DirectoryBinding { Kind = "windows", Subject = b.Subject, OfflineGraceDays = 0 }, _ => null) == null) throw new Exception("grace 0 still opened the profile");
                var tampered = DirectorySignIn.ReadProof()!; tampered.Groups.Add("EXAMPLE\\Ops Admins"); SecureStore.Write(DirectorySignIn.ProofFile, tampered);
                if (DirectorySignIn.ReadProof() != null) throw new Exception("a tampered proof was accepted");
                DirectorySignIn.ForgetProof();
            }
            finally { WindowsAccountSignIn.Identity = was; Profiles.Role = ""; }

            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=ldap.example.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new SubjectAlternativeNameBuilder().Also(s => s.AddDnsName("ldap.example.test")).Build());
            using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(10));
            var pin = cert.GetCertHashString(HashAlgorithmName.SHA256);
            if (LdapSignIn.Validate(cert, "ldap.example.test", "")) throw new Exception("a self-signed LDAP certificate was accepted without a pin");
            if (!LdapSignIn.Validate(cert, "ldap.example.test", pin) || LdapSignIn.Validate(cert, "ldap.example.test", new string('0', 64))) throw new Exception("pin check");
            if (LdapSignIn.Escape("a*b(c)\\") != "a\\2ab\\28c\\29\\5c" || LdapSignIn.Cn("CN=Ops Admins,OU=Groups,DC=example,DC=test") != "Ops Admins") throw new Exception("escape / cn");
            ok("Windows-account sign-in: the bound SID opens the profile with role admin from its groups and leaves a sealed, MAC'd proof; another account and a missing required group are refused; with the directory away the proof opens it within the grace and not with grace 0; a tampered proof is ignored. LDAP: a self-signed certificate is refused without a pin and accepted only with the right pin; filters escaped");
        }
        catch (Exception ex) { fail("Windows / LDAP / proof", ex); }

        // ---- the setup window, off screen ----------------------------------------------------------------------------------
        try
        {
            var w = new SetupWindow { WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            // 0.5.1 #15: Quick Start covers everything; every page builds with the live settings, nothing is contacted.
            var want = new[] { "Welcome", "Protect the app", "Lock what matters", "Personal settings", "AI features", "Organisation sign-in", "BeyondTrust", "Cloud accounts", "Infrastructure", "Your SSH keys", "Import connections", "Teams & Pingdom", "Log sinks", "Backup", "Your team", "Done" };
            if (!w.PageTitlesForTest.SequenceEqual(want) || w.Page != 0) throw new Exception("pages: " + string.Join(", ", w.PageTitlesForTest));
            for (int i = 0; i < w.PageCount; i++) w.ShowForTest(i);
            if (w.ProtectedForTest.Count != 0) throw new Exception("pages asked for protected changes by being shown");
            w.Close();
            ok("Quick Start: sixteen optional pages (welcome, protect, category locks, personal, AI with one-click Foundry, organisation sign-in, BeyondTrust tested before kept, cloud, infrastructure, this PC's SSH keys, import, Teams & Pingdom, log sinks, backup, team, done) build with the live settings; protected values wait for one approval at Finish");
        }
        catch (Exception ex) { fail("setup window", ex); }
    }

    private static HttpResponseMessage Json(object o) => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };

    private sealed class FakeHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => f(request, ct);
    }
}

internal static class SanExt
{
    public static SubjectAlternativeNameBuilder Also(this SubjectAlternativeNameBuilder b, Action<SubjectAlternativeNameBuilder> a) { a(b); return b; }
}
