using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Views;
using DcxManager.Vultr;
using DcxManager.Documents;

namespace DcxManager;

// SelfTest: updates, workspace, log digest, reports, AI guards, help, TOTP and backup (moved unchanged out of RunFullAsync in 0.2.0 phase 6½; the run's shared state is SelfTestRun).
public static partial class SelfTest
{
    private static async Task UpdatesAndServicesAsync(SelfTestRun sharedRun)
    {
        var main = sharedRun.Main; var reportPath = sharedRun.ReportPath; Action<string> Ok = sharedRun.Ok; Action<string, Exception?> Fail = sharedRun.Fail; SelfTestRun.SettleFn Settle = sharedRun.Settle;

        // Updates (ADR-008), offline: strict manifest rules, the check against a fake server, the download pipeline
        // (size cap, SHA-256, Authenticode), real WinVerifyTrust on signed/tampered files, MSI UpgradeCode, and the
        // post-install version confirmation.
        try
        {
            var mu = new Uri("https://darkciphersystems.com/downloads/servermanagement/update.json");
            var installed = new Version(0, 1, 0);
            string M(string ver = "0.2.0", string? url = null, string? file = null, string sha = "0000000000000000000000000000000000000000000000000000000000000000", long bytes = 1000, string product = "DarkCipher ServerManagement") =>
                "{\"product\":\"" + product + "\",\"version\":\"" + ver + "\",\"released\":\"2026-11-01\",\"notes\":\"Fixes.\",\"packages\":[{\"kind\":\"msi\",\"arch\":\"x64\",\"file\":\"" + (file ?? $"DarkCipherServerManagement-{ver}-x64.msi") +
                "\",\"url\":\"" + (url ?? $"DarkCipherServerManagement-{ver}-x64.msi") + "\",\"bytes\":" + bytes + ",\"sha256\":\"" + sha + "\"}]}";
            var good = Updater.Parse(M(), mu, installed);
            if (good.Info is not { } gi || gi.Version != new Version(0, 2, 0) || gi.Url.Host != mu.Host || !gi.Url.AbsolutePath.EndsWith("/DarkCipherServerManagement-0.2.0-x64.msi")) throw new Exception("valid manifest: " + good.Message);
            if (Updater.Parse(M(product: "DarkCipher Server Management"), mu, installed).Info == null) throw new Exception("manifest with the pre-0.1.9 product name refused");
            if (Updater.Parse(M("0.1.0"), mu, installed).Info != null || !Updater.Parse(M("0.1.0"), mu, installed).Message.Contains("latest available")) throw new Exception("same version");
            if (Updater.Parse(M("0.0.9"), mu, installed).Info != null || !Updater.Parse(M("0.0.9"), mu, installed).Message.Contains("Downgrades are never offered")) throw new Exception("downgrade");
            foreach (var (bad, why) in new[]
            {
                (M(url: "https://evil.example/DarkCipherServerManagement-0.2.0-x64.msi"), "other host"),
                (M(url: "http://darkciphersystems.com/downloads/servermanagement/DarkCipherServerManagement-0.2.0-x64.msi"), "http"),
                (M(file: "../../evil.msi", url: "../../evil.msi"), "traversal"),
                (M(file: "DarkCipherServerManagement-0.3.0-x64.msi"), "file/version mismatch"),
                (M(url: "DarkCipherServerManagement-0.2.0-x64.msi?x=1"), "query string"),
                (M(sha: "abc"), "no checksum"), (M(bytes: 0), "no size"), (M(bytes: 5L << 30), "huge"),
                (M(product: "Something Else"), "other product"), (M("1.2"), "two-part version"), (M("1.2.3.4"), "four-part version"),
                ("not json", "malformed"),
            })
                if (Updater.Parse(bad, mu, installed).Info != null) throw new Exception("accepted bad manifest: " + why);

            // The check: 404 / redirect / OK through a fake server (no network).
            // An address of your own (UpdateSource "url"); no update.json.sig published there (404).
            var sourceBefore = AppSettings.Current.UpdateSource;
            AppSettings.Current.UpdateSource = "url";
            try
            {
                int code = 404; string body = "";
                var srv = new FakeHttpStatus(r => r.RequestUri!.AbsolutePath.EndsWith(".sig") ? (404, "") : (code, body));
                var (i404, m404) = await Updater.CheckAsync(CancellationToken.None, srv);
                code = 301; var (i301, m301) = await Updater.CheckAsync(CancellationToken.None, srv);
                if (i404 != null || !m404.Contains("No update information is published") || i301 != null || !m301.Contains("redirect")) throw new Exception("check statuses");
                code = 200; body = M("99.0.0");
                var (iOk, _) = await Updater.CheckAsync(CancellationToken.None, srv);
                if (iOk?.Version != new Version(99, 0, 0) || iOk.ManifestSigned) throw new Exception("check ok");
                // A signature beside it that isn't the release key's: refused, not quietly ignored.
                var badSig = new FakeHttpStatus(r => r.RequestUri!.AbsolutePath.EndsWith(".sig") ? (200, Convert.ToBase64String(new byte[64])) : (200, M("99.0.0")));
                var (iBad, mBad) = await Updater.CheckAsync(CancellationToken.None, badSig);
                if (iBad != null || !mBad.Contains("release key")) throw new Exception("a bad signature beside the manifest was not refused: " + mBad);
            }
            finally { AppSettings.Current.UpdateSource = sourceBefore; }

            // Download pipeline: an unsigned real PE (this app's own exe) → refused as NOT signed, nothing kept.
            var exeBytes = File.ReadAllBytes(Environment.ProcessPath!);
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(exeBytes)).ToLowerInvariant();
            var ui = Updater.Parse(M("99.0.0", sha: sha, bytes: exeBytes.Length), mu, installed).Info!;
            var dl = new FakeHttpStatus(_ => (200, ""), _ => exeBytes);
            var (p1, v1) = await Updater.DownloadAndVerifyAsync(ui, null, CancellationToken.None, dl);
            if (p1 != null || v1.Verdict != UpdateVerdict.NotSigned) throw new Exception("unsigned package: " + v1.Verdict + " " + v1.Message);
            var (p2, v2) = await Updater.DownloadAndVerifyAsync(ui with { Sha256 = new string('a', 64) }, null, CancellationToken.None, dl);
            if (p2 != null || v2.Verdict != UpdateVerdict.HashMismatch) throw new Exception("hash: " + v2.Verdict);
            var (p3, v3) = await Updater.DownloadAndVerifyAsync(ui with { Bytes = exeBytes.Length - 100 }, null, CancellationToken.None, dl);
            if (p3 != null || v3.Verdict != UpdateVerdict.SizeMismatch) throw new Exception("size: " + v3.Verdict);
            if (Directory.Exists(ShareService.TempDir) && Directory.GetFiles(ShareService.TempDir, "*.msi", SearchOption.AllDirectories).Length != 0) throw new Exception("refused download left behind");

            // 0.6.1 (DarkCipher0x: updates from the GitHub repo; ADR-079): the release key, GitHub as the source, the setup program.
            if (!Updater.ReleaseKeyPinned || Updater.Enabled == Packaging.IsPackaged) throw new Exception("the release key should switch updates on (outside the Store)");
            using (var testKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
            {
                var kp = testKey.ExportParameters(false);
                string kx = Convert.ToHexString(kp.Q.X!), ky = Convert.ToHexString(kp.Q.Y!);
                byte[] SigOf(byte[] d) => Encoding.ASCII.GetBytes(Convert.ToBase64String(testKey.SignData(d, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
                var mb = Encoding.UTF8.GetBytes(M("99.0.0"));
                if (!Updater.VerifyManifest(mb, SigOf(mb), kx, ky)) throw new Exception("a good signature (test key) failed");
                var tm = (byte[])mb.Clone(); tm[30] ^= 1;
                if (Updater.VerifyManifest(tm, SigOf(mb), kx, ky)) throw new Exception("a changed manifest passed its old signature");
                if (Updater.VerifyManifest(mb, SigOf(mb))) throw new Exception("another key's signature passed the pinned release key");
                if (Updater.VerifyManifest(mb, Encoding.ASCII.GetBytes("not base64!"), kx, ky)) throw new Exception("garbage signature passed");

                // 0.6.12 (ADR-090): the post-quantum release signature (ML-DSA-65, update.json.pq.sig) is required too. With
                // test keys in place of the pinned ones: both signatures → offered; the ECDSA one alone, or a post-quantum one
                // by another key or over other bytes → refused. The pinned ML-DSA key is a real 1,952-byte public key.
                if (!Updater.ReleasePqKeyPinned || Convert.FromBase64String(Updater.ReleasePqKey).Length != 1952) throw new Exception("the post-quantum release key isn't pinned");
                var (pqSeed, pqPub) = Pqc.SigGenerate();
                var (otherSeed, _) = Pqc.SigGenerate();
                byte[] PqSigOf(byte[] d, byte[] seed) => Encoding.ASCII.GetBytes(Convert.ToBase64String(Pqc.Sign(seed, d)));
                if (!Updater.VerifyManifestPq(mb, PqSigOf(mb, pqSeed), Convert.ToBase64String(pqPub)) || Updater.VerifyManifestPq(tm, PqSigOf(mb, pqSeed), Convert.ToBase64String(pqPub))
                    || Updater.VerifyManifestPq(mb, PqSigOf(mb, pqSeed)) || Updater.VerifyManifestPq(mb, Encoding.ASCII.GetBytes("not base64!"), Convert.ToBase64String(pqPub)))
                    throw new Exception("ML-DSA manifest signature checks");
                const string PqGh = "https://github.com/" + Updater.GitHubRepo;
                FakeRoutes Release(byte[]? pqSig) => new(new()
                {
                    [PqGh + "/releases/latest/download/update.json"] = (200, mb, null),
                    [PqGh + "/releases/download/v99.0.0/update.json.sig"] = (200, SigOf(mb), null),
                    [PqGh + "/releases/download/v99.0.0/update.json.pq.sig"] = pqSig == null ? (404, Array.Empty<byte>(), null) : (200, pqSig, null),
                });
                var pqSourceBefore = AppSettings.Current.UpdateSource;
                Updater.TestKeys = (kx, ky, Convert.ToBase64String(pqPub));
                AppSettings.Current.UpdateSource = "github";
                try
                {
                    var (pq1, pqm1) = await Updater.CheckAsync(CancellationToken.None, Release(PqSigOf(mb, pqSeed)));
                    if (pq1 == null || !pq1.ManifestSigned) throw new Exception("a release with both signatures wasn't offered: " + pqm1);
                    var (pq2, pqm2) = await Updater.CheckAsync(CancellationToken.None, Release(null));
                    if (pq2 != null || !pqm2.Contains("no post-quantum signature")) throw new Exception("a release without the post-quantum signature was offered: " + pqm2);
                    var (pq3, pqm3) = await Updater.CheckAsync(CancellationToken.None, Release(PqSigOf(mb, otherSeed)));
                    if (pq3 != null || !pqm3.Contains("post-quantum signature doesn't match")) throw new Exception("a post-quantum signature by another key was accepted: " + pqm3);
                }
                finally { Updater.TestKeys = null; AppSettings.Current.UpdateSource = pqSourceBefore; }
                Ok("0.6.12 updates: update.json needs BOTH release signatures (ECDSA P-256 and ML-DSA-65, update.json.pq.sig); the ML-DSA key is pinned (1,952 bytes); with test keys a fully signed release is offered, one without the post-quantum signature or with one by another key is refused");

                // A fake GitHub (no network): latest → redirect to the download host → update.json; then its signature.
                const string Gh = "https://github.com/" + Updater.GitHubRepo;
                const string Cdn = "https://objects.githubusercontent.com/github-production-release-asset/x";
                Dictionary<string, (int, byte[], string?)> Routes(byte[]? sig, string latestTo = Cdn + "/update.json") => new()
                {
                    [Gh + "/releases/latest/download/update.json"] = (302, Array.Empty<byte>(), latestTo),
                    [Cdn + "/update.json"] = (200, mb, null),
                    [Gh + "/releases/download/v99.0.0/update.json.sig"] = sig == null ? (404, Array.Empty<byte>(), null) : (302, Array.Empty<byte>(), Cdn + "/update.json.sig"),
                    [Cdn + "/update.json.sig"] = (200, sig ?? Array.Empty<byte>(), null),
                };
                AppSettings.Current.UpdateSource = "github";
                try
                {
                    var (g1, gm1) = await Updater.CheckAsync(CancellationToken.None, new FakeRoutes(Routes(SigOf(mb))));
                    if (g1 != null || !gm1.Contains("release key")) throw new Exception("GitHub: a signature by a key that isn't ours was not refused: " + gm1);
                    var (g2, gm2) = await Updater.CheckAsync(CancellationToken.None, new FakeRoutes(Routes(null)));
                    if (g2 != null || !gm2.Contains("has no signature")) throw new Exception("GitHub: an unsigned release was offered: " + gm2);
                    var (g3, gm3) = await Updater.CheckAsync(CancellationToken.None, new FakeRoutes(Routes(SigOf(mb), "https://evil.example/update.json")));
                    if (g3 != null || !gm3.Contains("isn't allowed")) throw new Exception("GitHub: a redirect off GitHub was followed: " + gm3);
                    var (g4, gm4) = await Updater.CheckAsync(CancellationToken.None, new FakeRoutes(new()));
                    if (g4 != null || !gm4.Contains("No release with update information")) throw new Exception("GitHub: no release: " + gm4);

                    // 0.6.2: a private repository through api.github.com with your token; the token never reaches the
                    // download host (and a signature by a key that isn't ours is still refused).
                    AppSettings.Current.UpdateGitHubToken = "github_pat_selftest_only";
                    const string Api = "https://api.github.com/repos/" + Updater.GitHubRepo;
                    var seen = new List<(string Host, bool Authorized)>();
                    var relJson = Encoding.UTF8.GetBytes("{\"tag_name\":\"v99.0.0\",\"assets\":[{\"name\":\"update.json\",\"id\":11},{\"name\":\"update.json.sig\",\"id\":12}]}");
                    var priv = new FakeRoutes(new()
                    {
                        [Api + "/releases/latest"] = (200, relJson, null),
                        [Api + "/releases/assets/11"] = (302, Array.Empty<byte>(), Cdn + "/m"),
                        [Cdn + "/m"] = (200, mb, null),
                        [Api + "/releases/assets/12"] = (302, Array.Empty<byte>(), Cdn + "/s"),
                        [Cdn + "/s"] = (200, SigOf(mb), null),
                    }, seen);
                    var (g5, gm5) = await Updater.CheckAsync(CancellationToken.None, priv);
                    if (g5 != null || !gm5.Contains("release key")) throw new Exception("private repo: a signature by a key that isn't ours was not refused: " + gm5);
                    if (!seen.Any(x => x.Host == "api.github.com" && x.Authorized) || seen.Any(x => x.Host != "api.github.com" && x.Authorized) || seen.Count(x => x.Host.EndsWith("githubusercontent.com")) != 2)
                        throw new Exception("private repo: the token went somewhere other than api.github.com: " + string.Join(", ", seen));
                    var (g6, gm6) = await Updater.CheckAsync(CancellationToken.None, new FakeRoutes(new() { [Api + "/releases/latest"] = (401, Array.Empty<byte>(), null) }));
                    if (g6 != null || !gm6.Contains("refused the access token")) throw new Exception("private repo: a bad token: " + gm6);
                }
                finally { AppSettings.Current.UpdateSource = sourceBefore; AppSettings.Current.UpdateGitHubToken = ""; }
            }
            // The setup program: chosen only when asked (signed information + installed with Setup.exe), its name tied to
            // the version, the download only via GitHub's hosts, and only accepted with signed information.
            var both = "{\"product\":\"DarkCipher ServerManagement\",\"version\":\"99.0.0\",\"packages\":[" +
                $"{{\"kind\":\"msi\",\"arch\":\"x64\",\"file\":\"DarkCipherServerManagement-99.0.0-x64.msi\",\"url\":\"DarkCipherServerManagement-99.0.0-x64.msi\",\"bytes\":{exeBytes.Length},\"sha256\":\"{sha}\"}}," +
                $"{{\"kind\":\"setup\",\"arch\":\"x64\",\"file\":\"DarkCipherServerManagement-99.0.0-Setup.exe\",\"url\":\"DarkCipherServerManagement-99.0.0-Setup.exe\",\"bytes\":{exeBytes.Length},\"sha256\":\"{sha}\"}}]}}";
            var rel = new Uri("https://github.com/" + Updater.GitHubRepo + "/releases/download/v99.0.0/update.json");
            var pSetup = Updater.Parse(both, rel, installed, preferSetup: true).Info ?? throw new Exception("setup package not parsed");
            var pMsi = Updater.Parse(both, rel, installed).Info ?? throw new Exception("msi package not parsed");
            if (!pSetup.IsSetup || !pSetup.Url.AbsoluteUri.EndsWith("/v99.0.0/DarkCipherServerManagement-99.0.0-Setup.exe") || pMsi.IsSetup) throw new Exception("package choice: " + pSetup.Url + " / " + pMsi.Url);
            var setupUrl = pSetup.Url.AbsoluteUri;
            var dlRoutes = new FakeRoutes(new() { [setupUrl] = (302, Array.Empty<byte>(), "https://objects.githubusercontent.com/x/setup"), ["https://objects.githubusercontent.com/x/setup"] = (200, exeBytes, null) });
            var (sp, sv) = await Updater.DownloadAndVerifyAsync(pSetup with { ManifestSigned = true }, null, CancellationToken.None, dlRoutes);
            if (sp == null || !sv.Ok) throw new Exception("signed setup program via GitHub's download host: " + sv.Message);
            File.Delete(sp);
            var (sp2, sv2) = await Updater.DownloadAndVerifyAsync(pSetup with { ManifestSigned = true }, null, CancellationToken.None,
                new FakeRoutes(new() { [setupUrl] = (302, Array.Empty<byte>(), "https://evil.example/setup.exe") }));
            if (sp2 != null || sv2.Ok) throw new Exception("a download redirected off GitHub was accepted");
            var (sp3, sv3) = await Updater.DownloadAndVerifyAsync(pSetup, null, CancellationToken.None, dlRoutes);
            if (sp3 != null || sv3.Ok) throw new Exception("an unsigned setup program was accepted: " + sv3.Message);
            var (sp4, sv4) = await Updater.DownloadAndVerifyAsync(pMsi with { ManifestSigned = true, Url = new Uri(setupUrl) }, null, CancellationToken.None, dlRoutes);
            if (sp4 != null || sv4.Verdict != UpdateVerdict.WrongProduct) throw new Exception("a signed manifest let a non-MSI through as the MSI: " + sv4.Verdict);

            // Real Authenticode: a Microsoft-signed file, an unpinned publisher, then pinned; a tampered copy.
            string sigNote;
            var signedRef = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (File.Exists(signedRef))
            {
                var unpinned = Updater.CheckSignature(signedRef, Array.Empty<string>(), onlineRevocation: false);
                if (unpinned.Message.Contains("revoked (offline"))
                    sigNote = "signature chain valid; revocation not cached offline (pin test skipped)";
                else
                {
                    if (unpinned.Verdict != UpdateVerdict.UntrustedPublisher) throw new Exception("unpinned publisher: " + unpinned.Verdict + " " + unpinned.Message);
#pragma warning disable SYSLIB0057 // no loader replacement for reading an Authenticode signer
                    var thumb = new System.Security.Cryptography.X509Certificates.X509Certificate2(System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(signedRef)).Thumbprint;
#pragma warning restore SYSLIB0057
                    if (!Updater.CheckSignature(signedRef, new[] { thumb }, onlineRevocation: false).Ok) throw new Exception("pinned publisher refused");
                    sigNote = $"signed reference refused as unpinned (“{unpinned.Signer}”), accepted when pinned";
                }
                var tampered = Path.Combine(Path.GetDirectoryName(reportPath)!, "tampered-" + Guid.NewGuid().ToString("N")[..6] + ".exe");
                var tb = File.ReadAllBytes(signedRef); tb[tb.Length / 2] ^= 0xFF; File.WriteAllBytes(tampered, tb);
                var tv = Updater.CheckSignature(tampered, Array.Empty<string>(), onlineRevocation: false);
                File.Delete(tampered);
                if (tv.Verdict != UpdateVerdict.BadSignature) throw new Exception("tampered file: " + tv.Verdict + " " + tv.Message);
                sigNote += "; tampered copy rejected (bad digest)";
            }
            else sigNote = "no signed reference file found (signature tests skipped)";

            // Our own MSI (if a previous build exists): UpgradeCode read from the package itself.
            string msiNote = "no previous MSI to inspect";
            var dist = new DirectoryInfo(AppContext.BaseDirectory);
            while (dist != null && !Directory.Exists(Path.Combine(dist.FullName, "dist"))) dist = dist.Parent;
            var msi = dist == null ? null : Directory.GetFiles(Path.Combine(dist.FullName, "dist"), "DarkCipherServerManagement-*-x64.msi").FirstOrDefault();
            if (msi != null)
            {
                var (upg, pv) = Updater.MsiProps(msi);
                if (!string.Equals(upg, Updater.UpgradeCode, StringComparison.OrdinalIgnoreCase) || !Version.TryParse(pv, out _)) throw new Exception($"msi props: {upg} {pv}");
                msiNote = $"MSI UpgradeCode + ProductVersion {pv} read";
            }

            // After an update: only the running version can confirm it.
            var st = AppSettings.Current;
            st.PendingUpdateVersion = App.Version; st.PendingUpdateFrom = "0.0.1";
            if (Updater.ConfirmPendingUpdate()?.StartsWith("Update verified") != true || st.PendingUpdateVersion != "") throw new Exception("confirm ok");
            st.PendingUpdateVersion = "9.9.9"; st.PendingUpdateFrom = App.Version;
            if (Updater.ConfirmPendingUpdate()?.Contains("did not complete") != true) throw new Exception("confirm failed update");
            if (Updater.ConfirmPendingUpdate() != null) throw new Exception("pending not cleared");
            if (!PrivacyReport.Endpoints().Any(e => e.Service == "Update service" && e.Class == NetClass.External && e.State.Contains(Updater.Enabled ? "only when you click" : "DISABLED"))) throw new Exception("privacy listing");
            Ok($"updates: manifest rules (newer only, same https host, file=version, no traversal/query, checksum + size, strict schema; 12 bad manifests refused), 404/redirect/OK via fake server, unsigned package refused as NOT signed + hash/size mismatches, nothing left on disk; {sigNote}; {msiNote}; post-install confirmation (verified / did not complete); privacy listing");
        }
        catch (Exception ex) { Fail("updates", ex); }

        // Stabilisation: workspace persistence round-trip.
        try
        {
            Workspace.Save(new[] { new Workspace.Entry("terminal", "abc"), new Workspace.Entry("server", "def") });
            var back = Workspace.Load();
            if (back.Count != 2 || back[0].Kind != "terminal" || back[1].HostId != "def") throw new Exception("round-trip mismatch");
            Ok("workspace persistence round-trip");
        }
        catch (Exception ex) { Fail("workspace persistence", ex); }

        // ---- session 3 features ------------------------------------------------------------------------------------

        // Log digest: repeated lines fold into patterns, severities rank, render is bounded.
        try
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 300; i++) sb.AppendLine($"2026-10-18T02:{i % 60:00}:00 web-01 sshd[{1000 + i}]: Failed password for invalid user admin from 203.0.113.{i % 250} port {40000 + i} ssh2");
            for (int i = 0; i < 20; i++) sb.AppendLine($"2026-10-18T02:{i:00}:00 web-01 kernel: Out of memory: Killed process {500 + i} (java)");
            var pats = Reports.LogDigest.Digest(sb.ToString());
            if (pats.Count < 2 || pats.Count > 20) throw new Exception($"{pats.Count} patterns from 320 lines");
            var flood = pats.FirstOrDefault(p => p.Count >= 250) ?? throw new Exception("sshd flood not folded: counts " + string.Join(",", pats.Select(p => p.Count)));
            if (pats[0].Severity != "critical") throw new Exception("critical (OOM) should rank first, got " + pats[0].Severity);   // severity first, then volume
            var rendered = Reports.LogDigest.Render(pats, 2000);
            if (rendered.Length == 0 || rendered.Length > 2200) throw new Exception("render size " + rendered.Length);
            Ok($"log digest: 320 lines → {pats.Count} patterns (flood ×{flood.Count}, critical ranked first), bounded render");
        }
        catch (Exception ex) { Fail("log digest", ex); }

        // Report rules on canned evidence + Markdown/HTML render.
        try
        {
            var h = new HostEntry { Name = "web-01", Address = "203.0.113.10", Username = "alex" };
            var ev = new List<Reports.EvidenceSection>
            {
                new() { Host = h.Display, Id = "auth", Title = "auth", Digest = "[error] ×350  Failed password for invalid user admin from 203.0.113.5 port 4242 ssh2\n[notice] ×3  Accepted publickey for alex" },
                new() { Host = h.Display, Id = "kernel", Title = "kernel", Digest = "[critical] ×2  Out of memory: Killed process 1234 (java)" },
            };
            var findings = Reports.ReportEngine.Rules(h, null, ev);
            if (!findings.Any(f => f.Title.Contains("failed SSH logins") && f.Severity == "medium")) throw new Exception("brute-force rule missed");
            if (!findings.Any(f => f.Title.Contains("OOM"))) throw new Exception("OOM rule missed");
            var r = new Reports.ServerReport { Title = "selftest report", Hosts = { h.Display }, Findings = findings, Evidence = ev, Health = "degraded", Summary = "canned" };
            var md = Reports.ReportEngine.Markdown(r); var html = Reports.ReportEngine.Html(r);
            if (!md.Contains("failed SSH logins") || !html.Contains("<html") || !html.Contains("OOM")) throw new Exception("render missing content");
            Ok($"report rules on canned evidence ({findings.Count} findings) + Markdown/HTML render");
        }
        catch (Exception ex) { Fail("report rules/render", ex); }

        // AI client guards: redaction, local-endpoint detection, JSON extraction.
        try
        {
            var red = Ai.AiClient.Redact("password=hunter2 token: abcdef Authorization: Bearer AbCdEfGhIjKlMnOpQrStUv https://user:pa55@host/x AKIAABCDEFGHIJKLMNOP -----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----"); // secret-scan: example
            foreach (var leak in new[] { "hunter2", "abcdef", "AbCdEfGhIjKlMnOpQrStUv", "pa55@", "AKIAABCDEFGHIJKLMNOP", "\nabc\n" }) // secret-scan: example
                if (red.Contains(leak)) throw new Exception("leaked: " + leak);
            var json = Ai.AiClient.Redact("{\"label\":\"web\",\"default_password\": \"Xy9#secretpw\", \"api_key\":\"K-123456\", \"main_ip\":\"203.0.113.4\"}");
            if (json.Contains("Xy9#secretpw") || json.Contains("K-123456") || !json.Contains("203.0.113.4") || !json.Contains("\"label\":\"web\"")) throw new Exception("JSON redaction: " + json);
            foreach (var u in new[] { "http://localhost:11434", "http://127.0.0.1:1234/v1", "http://10.0.0.5:11434", "http://192.168.1.20:11434", "http://ollama.lan:11434", "http://[::1]:11434" })
                if (!Ai.AiClient.IsLocal(u)) throw new Exception("should be local: " + u);
            foreach (var u in new[] { "https://api.openai.com/v1", "https://api.anthropic.com", "http://8.8.8.8:11434", "not a url" })
                if (Ai.AiClient.IsLocal(u)) throw new Exception("should not be local: " + u);
            var j = Ai.AiClient.ExtractJson("Sure! Here you go:\n```json\n{\"health\":\"ok\",\"n\":2}\n```\nAnything else?");
            if (j?["health"]?.GetValue<string>() != "ok") throw new Exception("ExtractJson");
            Ok("AI guards: secret redaction, IsLocal (RFC1918/loopback/.lan vs public), ExtractJson from fenced text");
        }
        catch (Exception ex) { Fail("AI guards", ex); }

        // Documents: billing list → printable HTML with totals, escaped content, AI text; generated pages run no script.
        try
        {
            var rows = new[]
            {
                new JRow(JsonDocument.Parse("{\"id\":1,\"date\":\"2026-09-01T00:00:00+00:00\",\"type\":\"invoice\",\"description\":\"Invoice <script>alert(1)</script>\",\"amount\":12.5,\"balance\":-12.5}").RootElement),
                new JRow(JsonDocument.Parse("{\"id\":2,\"date\":\"2026-10-01T00:00:00+00:00\",\"type\":\"invoice\",\"description\":\"Invoice Oct\",\"amount\":7.25,\"balance\":-19.75}").RootElement),
            };
            var doc = CloudDocuments.ListDocument("Vultr", Vultr.VultrCatalog.BillingHistory, rows, "selftest-account", "Billing History");
            var html = Encoding.UTF8.GetString(doc.Bytes);
            if (!html.Contains("19.75") || !html.Contains("2026-10-01") || !html.Contains("<tfoot>") || !html.Contains("Total")) throw new Exception("table/total missing");
            if (html.Contains("<script>alert")) throw new Exception("description not HTML-escaped");
            if (doc.AiText?.Contains("Invoice Oct") != true || !doc.IsHtml) throw new Exception("AI text / mime");
            var txt = DocumentSource.Text("t", "t.txt", "a < b & c");
            if (!Encoding.UTF8.GetString(txt.Bytes).Contains("a &lt; b &amp; c")) throw new Exception("text doc not escaped");
            Ok("documents: billing list → printable HTML (dates, totals, escaping), AI text, text documents escaped");
        }
        catch (Exception ex) { Fail("documents", ex); }

        // AI context builders: configuration and metadata only — no secrets, no free-text notes.
        try
        {
            var cred = new Credential { Name = "ctx-key", Kind = CredentialKind.PrivateKey, Secret = "-----BEGIN OPENSSH PRIVATE KEY-----\nCTXSECRETKEY\n-----END OPENSSH PRIVATE KEY-----", Passphrase = "ctx-passphrase-1", Notes = "root pw is ctx-note-pw", KeyType = "ssh-ed25519", Fingerprint = "SHA256:abc" }; // secret-scan: example
            var host = new HostEntry { Name = "ctx-host", Address = "203.0.113.55", Username = "ops", CredentialId = cred.Id, Notes = "ctx-host-note-secret", Tags = "web" };
            AppData.Current.Credentials.Add(cred);
            try
            {
                var all = SafeMetadata.Host(host) + SafeMetadata.Credentials(new[] { cred });
                foreach (var leak in new[] { "CTXSECRETKEY", "ctx-passphrase-1", "ctx-note-pw", "ctx-host-note-secret" })
                    if (all.Contains(leak)) throw new Exception("AI context leaked: " + leak);
                if (!all.Contains("ctx-host") || !all.Contains("ssh-ed25519") || !all.Contains("has passphrase")) throw new Exception("context missing metadata");
            }
            finally { AppData.Current.Credentials.Remove(cred); }
            Ok("AI context: hosts/credentials expose metadata only (no key, passphrase or notes)");
        }
        catch (Exception ex) { Fail("AI context", ex); }

        // Help knowledge base: offline search finds the right article.
        try
        {
            if (HelpKb.Articles.Count < 20) throw new Exception("only " + HelpKb.Articles.Count + " articles");
            string Top(string q) => HelpKb.Search(q, 1).FirstOrDefault().Article?.Title ?? "";
            if (!Top("split panes side by side").Contains("split", StringComparison.OrdinalIgnoreCase)) throw new Exception("split → " + Top("split panes side by side"));
            if (!Top("screenshot of rdp screen").Contains("Screenshot", StringComparison.OrdinalIgnoreCase)) throw new Exception("screenshot → " + Top("screenshot of rdp screen"));
            if (!Top("2fa authenticator lock").Contains("Security", StringComparison.OrdinalIgnoreCase) && !Top("2fa authenticator lock").Contains("lock", StringComparison.OrdinalIgnoreCase)) throw new Exception("2fa → " + Top("2fa authenticator lock"));
            Ok($"help KB: {HelpKb.Articles.Count} articles, BM25 top hits sensible");
        }
        catch (Exception ex) { Fail("help KB", ex); }

        // TOTP: RFC 6238 test vector, then the store's second factor with replay protection and recovery codes.
        try
        {
            var rfc = Encoding.ASCII.GetBytes("12345678901234567890");
            if (Totp.Code(rfc, 59 / 30) != "287082") throw new Exception("RFC vector T=59 gave " + Totp.Code(rfc, 1));
            if (Totp.Code(rfc, 1111111109L / 30) != "081804") throw new Exception("RFC vector T=1111111109");
            var secret = Totp.NewSecret();
            var codes = Totp.RecoveryCodes();
            SecureStore.SetTotp(secret, codes);
            var now = Totp.Now(secret);
            if (!SecureStore.VerifySecondFactor(now)) throw new Exception("valid code rejected");
            if (SecureStore.VerifySecondFactor(now)) throw new Exception("replayed code accepted");
            if (SecureStore.VerifySecondFactor("000000") && SecureStore.VerifySecondFactor("000000")) throw new Exception("junk accepted");
            if (!SecureStore.VerifySecondFactor(codes[3])) throw new Exception("recovery code rejected");
            if (SecureStore.VerifySecondFactor(codes[3])) throw new Exception("recovery code reused");
            var ring = File.ReadAllText(Path.Combine(SecureStore.DataDir, "keyring.json"));
            if (ring.Contains(Totp.Base32(secret)) || ring.Contains(codes[0])) throw new Exception("TOTP secret or recovery code stored in clear");
            SecureStore.RemoveTotp();
            Ok("TOTP: RFC 6238 vectors, verify, replay rejected, recovery code single-use, nothing in clear");
        }
        catch (Exception ex) { Fail("TOTP", ex); }

        // Encrypted backup: selection → write → wrong password rejected → read → merge (skip duplicates).
        try
        {
            var hosts = AppData.Current.Hosts.Where(h => h.Name == "selftest-host").ToList();
            var file = Backup.Selection(hosts, new Backup.Include(), "selftest");
            if (file.Data?.Credentials.Count != 1) throw new Exception("referenced credential not included");
            var path = Path.Combine(Path.GetDirectoryName(reportPath)!, "selftest.dcxbackup");
            Backup.Write(path, "backup-pw-1-selftest", file);
            var raw = File.ReadAllText(path);
            if (raw.Contains("selftest-host") || raw.Contains("s3cret-value-xyz")) throw new Exception("plaintext in backup");
            try { Backup.Read(path, "wrong"); throw new Exception("wrong password accepted"); } catch (System.Security.Cryptography.CryptographicException) { }
            var back = Backup.Read(path, "backup-pw-1-selftest");
            int before = AppData.Current.Hosts.Count;
            var res = Backup.Merge(back, Backup.Conflict.Skip);
            if (res.HostsSkipped != 1 || AppData.Current.Hosts.Count != before) throw new Exception($"merge skip: {res}");
            var kept = Backup.Merge(back, Backup.Conflict.KeepBoth, "Imported");
            if (kept.HostsAdded != 1 || AppData.Current.Hosts.Count != before + 1) throw new Exception($"merge keep-both: {kept}");
            AppData.Current.Hosts.RemoveAll(h => h.Group == "Imported");
            AppData.Current.Folders.RemoveAll(f => f.Equals("Imported", StringComparison.OrdinalIgnoreCase));
            AppData.Save();
            File.Delete(path);
            Ok("backup: selection pulls credentials, encrypted file, wrong password rejected, merge skip/keep-both");
        }
        catch (Exception ex) { Fail("backup", ex); }
    }
}
