using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using DcxManager.Addons;
using DcxManager.Browser;
using DcxManager.Infra;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// ADR-032 (0.1.15). The portal browser add-on against a local test web server only (plain HTTP and a self-signed HTTPS
/// server on 127.0.0.1; nothing external is contacted — the one TEST-NET address is refused by offline mode before any
/// connection). Covers the package (and malformed ones), bookmarks in the encrypted store, profiles per environment,
/// TOFU certificate pinning (trust, change, forget), downloads that ask, pop-ups as tabs, Fill sign-in on its own origin
/// only, address redaction, offline mode and the Privacy report. No window or dialog is shown.
/// </summary>
public static class PortalTests
{
    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail, Action<Window, string> snap)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        async Task<bool> Until(Func<bool> cond, int ms = 15000)
        {
            for (int i = 0; i < ms / 100; i++) { if (cond()) return true; await Settle(100); }
            return cond();
        }
        async Task<bool> UntilAsync(Func<Task<bool>> cond, int ms = 15000)
        {
            for (int i = 0; i < ms / 100; i++) { if (await cond()) return true; await Settle(100); }
            return await cond();
        }
        var shots = Directory.CreateDirectory(Path.Combine(reportDir, "shots")).FullName;
        var work = Directory.CreateDirectory(Path.Combine(reportDir, "portals")).FullName;
        var s = AppSettings.Current;
        var (openInWas, folderWas, confirmWas, offlineWas) = (s.PortalOpenIn, s.PortalDownloadFolder, Dialogs.ConfirmForTests, Offline.On);
        var builtInWas = s.PortalBrowserBuiltIn;
        var addedBookmarks = new List<PortalBookmark>();
        HostEntry? host = null; Credential? cred = null;
        InstalledAddon? addon = null;
        TestWeb? http = null, https = null;
        X509Certificate2? certA = null, certB = null;

        string? sampleDir = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && sampleDir == null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "samples", "addons", "portal-browser"))) sampleDir = Path.Combine(d.FullName, "samples", "addons", "portal-browser");
        string Pack(string name, object manifest, params (string Name, string Text)[] files)
        {
            var path = Path.Combine(work, name + ".dcxaddon");
            if (File.Exists(path)) File.Delete(path);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var w = new StreamWriter(zip.CreateEntry("addon.json").Open())) w.Write(JsonSerializer.Serialize(manifest));
            foreach (var (n, t) in files) using (var w = new StreamWriter(zip.CreateEntry(n).Open(), new UTF8Encoding(false))) w.Write(t);
            return path;
        }
        object Manifest(string id, string browser = "browser.json", string? entry = null, string[]? permissions = null, string? theme = null) =>
            new { api = 2, id, name = "Self-test portals", version = "1.0.0", publisher = "Self-test", description = "portal browser", browser, entry = entry ?? "", theme = theme ?? "", permissions = permissions ?? Array.Empty<string>() };

        try
        {
            // ---- 1. the package: sample installs as data only; malformed ones are refused -----------------------------
            try
            {
                string pkg;
                if (sampleDir != null) { pkg = Path.Combine(work, "portal-browser.dcxaddon"); if (File.Exists(pkg)) File.Delete(pkg); ZipFile.CreateFromDirectory(sampleDir, pkg); }
                else pkg = Pack("portal-browser", new { api = 2, id = "com.darkcipher.portal-browser", name = "Portal browser", version = "1.0.0", publisher = "DarkCipher Systems", description = "x", browser = "browser.json" },
                    ("browser.json", "{\"name\":\"Portal browser\",\"bookmarks\":[]}"));
                if (AddonPackages.Find("com.darkcipher.portal-browser") is { } old) AddonPackages.Remove(old);
                // Built in since 0.1.17: on by default with no add-on; the switch shows / hides Portals.
                PortalBrowser.SetBuiltIn(true); await Settle(300);
                if (!PortalBrowser.Enabled || !main.NavKeys.Contains("portals")) throw new Exception("built-in portal browser not on without the add-on");
                PortalBrowser.SetBuiltIn(false); await Settle(300);
                // The add-on is still a switch of its own (existing installs): checked with the built-in one off.
                if (PortalBrowser.Enabled) throw new Exception("enabled before install");
                if (main.NavKeys.Contains("portals")) throw new Exception("Portals in the sidebar before install");
                var info = AddonPackages.Inspect(pkg);
                if (info.Refusal != null) throw new Exception("sample refused: " + info.Refusal);
                if (!info.Signature.Contains("no program")) throw new Exception("signature line: " + info.Signature);
                addon = AddonPackages.Install(info, Array.Empty<string>());
                await Settle(300);
                if (!PortalBrowser.Enabled || PortalBrowser.Pack(addon) is not { } pack) throw new Exception("not enabled / pack unreadable after install");
                if (!main.NavKeys.Contains("portals")) throw new Exception("Portals not in the sidebar after install");
                AddonPackages.SetEnabled(addon, false); await Settle(300);
                if (PortalBrowser.Enabled || main.NavKeys.Contains("portals")) throw new Exception("still on after disable");
                AddonPackages.SetEnabled(addon, true); await Settle(300);
                if (!PortalBrowser.Enabled || !main.NavKeys.Contains("portals")) throw new Exception("not back after enable");
                AddonPackages.SetEnabled(addon, false); PortalBrowser.SetBuiltIn(true); await Settle(300);
                if (!PortalBrowser.Enabled || !main.NavKeys.Contains("portals")) throw new Exception("built-in switch must work with the add-on disabled");
                PortalBrowser.SetBuiltIn(false); AddonPackages.SetEnabled(addon, true); await Settle(300);

                var cases = new (string Why, string Pkg)[]
                {
                    ("with an entry program", Pack("m1", Manifest("com.selftest.p1", entry: "run.ps1"), ("browser.json", "{\"name\":\"x\"}"), ("run.ps1", "exit 0"))),
                    ("with permissions", Pack("m2", Manifest("com.selftest.p2", permissions: new[] { "hosts.read" }), ("browser.json", "{\"name\":\"x\"}"))),
                    ("with a theme too", Pack("m3", Manifest("com.selftest.p3", theme: "browser.json"), ("browser.json", "{\"name\":\"x\"}"))),
                    ("browser file missing", Pack("m4", Manifest("com.selftest.p4"))),
                    ("browser file not .json", Pack("m5", Manifest("com.selftest.p5", browser: "browser.txt"), ("browser.txt", "{\"name\":\"x\"}"))),
                    ("not JSON", Pack("m6", Manifest("com.selftest.p6"), ("browser.json", "{name"))),
                    ("unknown field", Pack("m7", Manifest("com.selftest.p7"), ("browser.json", "{\"name\":\"x\",\"script\":\"alert(1)\"}"))),
                    ("javascript: starter", Pack("m8", Manifest("com.selftest.p8"), ("browser.json", "{\"name\":\"x\",\"bookmarks\":[{\"title\":\"a\",\"url\":\"javascript:alert(1)\"}]}"))),
                    ("file: starter", Pack("m9", Manifest("com.selftest.p9"), ("browser.json", "{\"name\":\"x\",\"bookmarks\":[{\"title\":\"a\",\"url\":\"file:///C:/Windows/win.ini\"}]}"))),
                    ("password in a starter address", Pack("m10", Manifest("com.selftest.p10"), ("browser.json", "{\"name\":\"x\",\"bookmarks\":[{\"title\":\"a\",\"url\":\"https://root:pw@192.0.2.1/\"}]}"))),
                    ("51 starters", Pack("m11", Manifest("com.selftest.p11"), ("browser.json", "{\"name\":\"x\",\"bookmarks\":[" + string.Join(",", Enumerable.Range(0, 51).Select(i => $"{{\"title\":\"t{i}\",\"url\":\"https://192.0.2.{i % 250 + 1}/\"}}")) + "]}"))),
                    ("starter with an extra field", Pack("m12", Manifest("com.selftest.p12"), ("browser.json", "{\"name\":\"x\",\"bookmarks\":[{\"title\":\"a\",\"url\":\"https://192.0.2.1/\",\"onclick\":\"x\"}]}"))),
                    ("no name", Pack("m13", Manifest("com.selftest.p13"), ("browser.json", "{\"bookmarks\":[]}"))),
                };
                var accepted = cases.Where(c => AddonPackages.Inspect(c.Pkg).Refusal == null).Select(c => c.Why).ToList();
                if (accepted.Count > 0) throw new Exception("accepted: " + string.Join(", ", accepted));

                // Starter bookmarks: parsed from a valid package, added once, duplicates skipped.
                var starters = Pack("starters", Manifest("com.selftest.starters"), ("browser.json",
                    "{\"name\":\"Lab portals\",\"bookmarks\":[{\"title\":\"PVE (self-test)\",\"url\":\"https://192.0.2.10:8006/\",\"folder\":\"Lab\"},{\"title\":\"NAS (self-test)\",\"url\":\"https://192.0.2.11:5001/\"}]}"));
                var si = AddonPackages.Inspect(starters);
                if (si.Refusal != null) throw new Exception("starter package refused: " + si.Refusal);
                using (var zip = ZipFile.OpenRead(starters)) using (var r = new StreamReader(zip.GetEntry("browser.json")!.Open()))
                {
                    var sp = BrowserPack.Parse(r.ReadToEnd());
                    int n1 = PortalsView.AddStarters(sp.Starters), n2 = PortalsView.AddStarters(sp.Starters);
                    addedBookmarks.AddRange(Portals.All.Where(b => b.Title.EndsWith("(self-test)")));
                    if (n1 != 2 || n2 != 0 || addedBookmarks.Count != 2 || addedBookmarks.First(b => b.Title.StartsWith("PVE")).Folder != "Lab") throw new Exception($"starters added {n1}/{n2}");
                }
                ok($"portal browser built in (0.1.17): on with no add-on, the Settings switch shows / hides Portals; the add-on still works on its own: the sample installs as data only (“{info.Signature}”), Portals appears in the sidebar and follows disable / enable; {cases.Length} malformed packages refused (program, permissions, theme, missing / non-JSON file, unknown field, javascript: / file: / user:password@ starters, > 50 starters, extra field, no name); starter bookmarks added once");
            }
            catch (Exception ex) { fail("portal browser add-on package", ex); }

            // ---- 2. pure rules: addresses, profiles, redaction, downloads, navigation ----------------------------------
            try
            {
                string[] bad = { "", "javascript:alert(1)", "file:///C:/Windows/win.ini", "ftp://192.0.2.1/", "https://root:secret@192.0.2.1/", "not a url", "https://192.0.2.1/" + new string('a', 2100), "https://192.0.2.1/a b" }; // secret-scan: example
                if (bad.FirstOrDefault(u => Portals.ValidateUrl(u) == null) is { } wrong) throw new Exception("accepted " + wrong[..Math.Min(40, wrong.Length)]);
                if (Portals.ValidateUrl("https://192.0.2.10:8006/#v1:0") != null || Portals.ValidateUrl("http://198.51.100.1/") != null) throw new Exception("refused a valid address");
                if (Portals.Normalize("192.0.2.5:8443") != "https://192.0.2.5:8443") throw new Exception("normalize: " + Portals.Normalize("192.0.2.5:8443"));
                var shown = Portals.Display("https://192.0.2.1:8006/novnc/?console=kvm&vncticket=PVEVNC%3ASECRETX&node=pve&token=abc123#v1");
                if (shown.Contains("SECRETX") || shown.Contains("abc123") || !shown.Contains("node=pve") || !shown.Contains("console=kvm") || !shown.Contains("#v1")) throw new Exception("display: " + shown);
                if (Portals.Display("https://admin:pw@192.0.2.1/").Contains("pw")) throw new Exception("user info shown");
                foreach (var (u, allowed) in new[] { ("https://192.0.2.1/", true), ("http://192.0.2.1/", true), ("about:blank", true), ("data:text/html,x", true), ("file:///C:/", false), ("ms-settings:privacy", false), ("mailto:x@example.com", false), ("javascript:alert(1)", false), ("search-ms:query=x", false) })
                    if (Portals.NavigationAllowed(u, out _) != allowed) throw new Exception($"navigation {u} → {!allowed}");
                var dl = Directory.CreateDirectory(Path.Combine(work, "dl-rules")).FullName;
                var t1 = Portals.SafeTarget(dl, "..\\..\\evil.exe");
                var t2 = Portals.SafeTarget(dl, "CON.txt");
                File.WriteAllText(Path.Combine(dl, "report.txt"), "x");
                var t3 = Portals.SafeTarget(dl, "report.txt");
                if (Path.GetDirectoryName(t1) != dl || Path.GetFileName(t1) != "evil.exe" || Path.GetFileName(t2) != "_CON.txt" || Path.GetFileName(t3) != "report (2).txt") throw new Exception($"safe target: {t1} | {t2} | {t3}");
                if (!Portals.IsRisky("setup.EXE") || !Portals.IsRisky("x.ps1") || Portals.IsRisky("backup.tar.gz")) throw new Exception("risky extensions");

                host = new HostEntry { Name = "selftest-portal-host", Address = "192.0.2.50", Username = "admin", Environment = Envs.Production };
                AppData.Current.Hosts.Add(host);
                var gb = new PortalBookmark { Title = "g", Url = "https://192.0.2.1/" };
                var hb = new PortalBookmark { Title = "h", Url = "https://192.0.2.50/", HostId = host.Id };
                var eb = new PortalBookmark { Title = "e", Url = "https://192.0.2.1/", HostId = host.Id, Environment = Envs.Lab };
                var nb = new PortalBookmark { Title = "n", Url = "https://192.0.2.1/", HostId = host.Id, Profile = "Home lab" };
                if (Portals.ProfileFor(gb) != "dcx-general" || Portals.ProfileFor(hb) != "dcx-production" || Portals.ProfileFor(eb) != "dcx-lab" || Portals.ProfileFor(nb) != "dcx-p-home_lab")
                    throw new Exception($"profiles: {Portals.ProfileFor(gb)} {Portals.ProfileFor(hb)} {Portals.ProfileFor(eb)} {Portals.ProfileFor(nb)}");
                if (Portals.ProfilesInUse().Any(p => !System.Text.RegularExpressions.Regex.IsMatch(p, "^[a-z0-9_-]+$"))) throw new Exception("profile name not WebView2-safe");
                if (Portals.Validate(new PortalBookmark { Title = "x", Url = "https://192.0.2.1/", Profile = "../evil" }) == null) throw new Exception("bad profile name accepted");
                if (PortalUi.HostMenu(host) == null) throw new Exception("no Open web UI in the host menu");
                // TOFU decision table (the same rule as TlsPin): nothing pinned → trust needed; pinned → accept only that one.
                if (Portals.Decide("192.0.2.99:443", "AA") != TlsDecision.NeedsTrust) throw new Exception("unpinned decision");
                ok("portal rules: only http(s) addresses without user:password (javascript:, file:, ftp:, spaces, > 2048 refused); token / ticket query values masked in the address; file:, ms-settings:, mailto:, javascript:, search-ms: navigations refused; download names kept in the folder (no ..\\, no CON, (2) on collision), risky types flagged; profiles per environment (general / production from the host / lab override / named “Home lab”) with WebView2-safe names; Open web UI in the host menu");
            }
            catch (Exception ex) { fail("portal rules", ex); }

            // ---- 3. bookmarks are encrypted in data.dcx ---------------------------------------------------------------
            try
            {
                const string marker = "selftest-portal-marker-7Q2";
                var b = new PortalBookmark { Title = "Marker " + marker, Url = $"https://198.51.100.23:8443/{marker}/", Folder = "Self-test" };
                Portals.Upsert(b); addedBookmarks.Add(b);
                var raw = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(SecureStore.DataDir, "data.dcx")));
                if (raw.Contains(marker)) throw new Exception("bookmark in plaintext in data.dcx");
                AppData.Load();
                if (Portals.All.FirstOrDefault(x => x.Id == b.Id)?.Url != b.Url) throw new Exception("bookmark not read back");
                addedBookmarks = Portals.All.Where(x => addedBookmarks.Any(a => a.Id == x.Id)).ToList();
                host = AppData.Current.FindHost(host?.Id) ?? host;
                ok("portal bookmarks: saved in data.dcx (AES-256-GCM, no plaintext address or title), read back after a reload");
            }
            catch (Exception ex) { fail("portal bookmarks encrypted", ex); }

            // ---- 4. live: a local HTTP page in a tab ------------------------------------------------------------------
            http = TestWeb.Start(null);
            BrowserView? view = null;
            async Task<bool> Nav(BrowserView v, Action start, int ms = 15000)
            {
                var tcs = new TaskCompletionSource<bool>();
                void Done(bool ok2) => tcs.TrySetResult(ok2);
                v.NavigationDone += Done;
                try { start(); return await Task.WhenAny(tcs.Task, Task.Delay(ms)) == tcs.Task && await tcs.Task; }
                finally { v.NavigationDone -= Done; }
            }
            try
            {
                var b = new PortalBookmark { Title = "Local test page", Url = $"http://127.0.0.1:{http.Port}/", Environment = Envs.Lab };
                Portals.Upsert(b); addedBookmarks.Add(b);
                s.PortalOpenIn = "tab";
                view = PortalUi.Open(b) ?? throw new Exception("not opened");
                if (!await Until(() => view.Core != null || view.InitError != null)) throw new Exception("browser did not start");
                if (view.InitError != null) throw new Exception("init: " + view.InitError);
                await Until(() => view.Core!.DocumentTitle == "DCX portal test");
                if (view.Core!.DocumentTitle != "DCX portal test") throw new Exception("page not loaded: " + view.Core.Source);
                if (view.Core.Profile.ProfileName != "dcx-lab" || view.ProfileName != "dcx-lab") throw new Exception("profile " + view.Core.Profile.ProfileName);
                if (view.Core.Profile.IsPasswordAutosaveEnabled || view.Core.Profile.IsGeneralAutofillEnabled || view.Core.Settings.IsReputationCheckingRequired) throw new Exception("password save / autofill / SmartScreen on");
                if (view.Security != WebSecurity.NotSecure) throw new Exception("http shown as " + view.Security);
                if (view.Tab == null || main.Panes.IsFloating(view.Tab) || EnvUi.ForView(view) != Envs.Lab) throw new Exception("tab / environment label");
                if (!(view.Tab.Header as string ?? "").Contains("DCX portal test")) throw new Exception("tab title " + view.Tab.Header);

                // Address redaction on a live page.
                if (!await Nav(view, () => view.Go($"http://127.0.0.1:{http.Port}/tok?token=SECRET123&x=1"))) throw new Exception("token page");
                if (view.AddressText.Contains("SECRET123") || !view.AddressText.Contains("x=1")) throw new Exception("address shows " + view.AddressText);

                // Navigation policy on a live page: script can't reach this PC's files.
                await view.Core.ExecuteScriptAsync("location.href='file:///C:/Windows/win.ini'");
                await Settle(800);
                if ((view.Core.Source ?? "").StartsWith("file:")) throw new Exception("navigated to file:");

                // Pop-up: a tab in the same profile, closed by the page.
                int before = BrowserView.Live.Count;
                await view.Core.ExecuteScriptAsync("window.open('/popup', '_blank')");
                BrowserView? pop = null;
                if (!await Until(() => (pop = BrowserView.Live.FirstOrDefault(v => v.IsPopup && v.Core?.DocumentTitle == "DCX popup")) != null)) throw new Exception($"no pop-up tab (live {BrowserView.Live.Count}, was {before})");
                if (pop!.Core!.Profile.ProfileName != "dcx-lab" || pop.Tab == null || !main.Panes.AllTabs.Contains(pop.Tab)) throw new Exception("pop-up profile / tab");
                await pop.Core.ExecuteScriptAsync("window.close()");
                if (!await Until(() => !BrowserView.Live.Contains(pop))) throw new Exception("pop-up tab not closed by window.close()");

                // Downloads: refused when you say no (nothing written), saved to the chosen folder when you say yes.
                var folder = Directory.CreateDirectory(Path.Combine(work, "downloads")).FullName;
                foreach (var f in Directory.GetFiles(folder)) File.Delete(f);
                s.PortalDownloadFolder = folder;
                Dialogs.ConfirmForTests = false;
                view.Go($"http://127.0.0.1:{http.Port}/file.txt");
                if (!await Until(() => view.DownloadsRefused == 1)) throw new Exception("download was not asked about");
                await Settle(500);
                if (Directory.GetFiles(folder).Length > 0) throw new Exception("a refused download was written");
                Dialogs.ConfirmForTests = true;
                view.Go($"http://127.0.0.1:{http.Port}/file.txt");
                if (!await Until(() => view.LastDownloadPath != null && File.Exists(view.LastDownloadPath))) throw new Exception("download not saved");
                if (Path.GetDirectoryName(view.LastDownloadPath) != folder || File.ReadAllText(view.LastDownloadPath!) != "dcx-download-ok") throw new Exception("download path / content " + view.LastDownloadPath);
                lock (Dialogs.SelfTestConfirms) if (!Dialogs.SelfTestConfirms.Any(c => c.Title == "Download" && c.Message.Contains("report.txt") && c.Message.Contains(folder))) throw new Exception("download confirmation text");

                // Fill sign-in: vault password on its own origin (plain HTTP asks first); refused on another origin.
                const string pw = "p@ss\"';</script><b>x\\n";
                cred = new Credential { Name = "selftest-portal-cred", Kind = CredentialKind.Password, Username = "portal-admin", Secret = pw };
                AppData.Current.Credentials.Add(cred);
                b.Url = $"http://127.0.0.1:{http.Port}/login"; b.CredentialId = cred.Id; Portals.Upsert(b);
                if (!await Nav(view, () => view.Go(b.Url))) throw new Exception("login page");
                var r1 = await view.FillSignInAsync();
                await Settle(200);
                var (u1, p1) = (await view.Core.ExecuteScriptAsync("document.getElementById('u').value"), await view.Core.ExecuteScriptAsync("document.getElementById('p').value"));
                if (r1 != "filled" || JsonSerializer.Deserialize<string>(u1) != "portal-admin" || JsonSerializer.Deserialize<string>(p1) != pw) throw new Exception($"fill: {r1} {u1}");
                lock (Dialogs.SelfTestConfirms) if (!Dialogs.SelfTestConfirms.Any(c => c.Title == "Fill sign-in" && c.Danger)) throw new Exception("plain-HTTP fill didn't warn");
                if (!await Nav(view, () => view.Go($"http://localhost:{http.Port}/login"))) throw new Exception("login page (localhost)");
                var r2 = await view.FillSignInAsync();
                var p2 = await view.Core.ExecuteScriptAsync("document.getElementById('p').value");
                if (!r2.StartsWith("refused") || JsonSerializer.Deserialize<string>(p2) != "") throw new Exception($"filled on another origin: {r2}");

                // 0.6.20 (DarkCipher0x: "the "Fill sign-in" isnt working on the webpage … there should be an option to copy
                // password and username and TOTP"): a sign-in in steps gets the user name, then the password when its box
                // appears, then the TOTP code; a form in a shadow root is found; a page with no form offers the copies.
                cred.Totp = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";   // RFC 6238's test secret
                async Task<string> Val(string js) => JsonSerializer.Deserialize<string>(await view.Core!.ExecuteScriptAsync(js)) ?? "";
                if (!await Nav(view, () => view.Go($"http://127.0.0.1:{http.Port}/login2"))) throw new Exception("two-step page");
                var r3 = await view.FillSignInAsync();
                if (r3 != "filled-user" || await Val("document.getElementById('un').value") != "portal-admin" || await Val("document.getElementById('p2').value") != "") throw new Exception($"step 1: {r3}");
                if (!view.BannerText.Contains("password is filled when its box appears")) throw new Exception("step 1 banner: " + view.BannerText);
                await view.Core.ExecuteScriptAsync("document.getElementById('next').click()");
                if (!await UntilAsync(async () => await Val("document.getElementById('p2').value") == pw, 5000)) throw new Exception("the password wasn't filled when its box appeared");
                await view.Core.ExecuteScriptAsync("document.getElementById('go').click()");
                var spec = Totp.Parse(cred.Totp)!;
                if (!await UntilAsync(async () => await Val("document.getElementById('otp').value") is { Length: 6 } code && (code == Totp.Code(spec, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30) || code == Totp.Code(spec, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 - 1)), 5000))
                    throw new Exception("the TOTP code wasn't filled: " + await Val("document.getElementById('otp').value"));
                if (!await Nav(view, () => view.Go($"http://127.0.0.1:{http.Port}/shadow"))) throw new Exception("shadow page");
                var r4 = await view.FillSignInAsync();
                if (r4 != "filled" || await Val("document.getElementById('host').shadowRoot.getElementById('sp').value") != pw || await Val("document.getElementById('host').shadowRoot.getElementById('su').value") != "portal-admin") throw new Exception("shadow root: " + r4);
                if (!await Nav(view, () => view.Go($"http://127.0.0.1:{http.Port}/noform"))) throw new Exception("no-form page");
                var r5 = await view.FillSignInAsync();
                if (r5 != "no-password-field" || !view.BannerText.Contains("Copy from")) throw new Exception($"no form: {r5} / {view.BannerText}");
                foreach (var (part, want) in new[] { ("user", "copied user"), ("password", "copied password"), ("totp", "copied totp") })
                    if (await view.CopyFromVaultAsync(part) != want) throw new Exception("copy " + part);
                cred.Totp = "";
                if (await view.CopyFromVaultAsync("totp") != "no totp") throw new Exception("TOTP copied without a secret");
                // 0.6.20 (DarkCipher0x's screenshot: the ▾ beside Fill sign-in was cut off): no toolbar button is cut off,
                // wide or narrow; narrow tabs show the sign-in buttons as icons.
                foreach (var tabW in new[] { 1400.0, 900.0, 700.0, 560.0 })
                {
                    view.Width = tabW; await Settle(250);
                    if (view.ClippedToolbarForTest() is { Count: > 0 } cut) throw new Exception($"toolbar cut off at {tabW} px: " + string.Join("; ", cut));
                }
                view.Width = double.NaN; await Settle(200);

                // Window mode: the same tab, popped out (pop-out windows are off-screen in the self-test).
                s.PortalOpenIn = "window";
                var w = PortalUi.Open(b) ?? throw new Exception("window mode not opened");
                await Until(() => w.Core != null);
                if (w.Tab == null || !main.Panes.IsFloating(w.Tab)) throw new Exception("not in a window");
                main.Panes.DockBack(w.Tab); await Settle(300);
                main.CloseTab(w.Tab);
                s.PortalOpenIn = "tab";
                main.Panes.Select(view.Tab!); await Settle(300);
                snap(main, Path.Combine(shots, "portal-tab.png"));
                ok("portal tab (local HTTP page): profile “dcx-lab” per environment, password saving / autofill / SmartScreen off, “Not secure” chip, LAB tab label and page title; token masked in the address; file: refused; window.open → a tab in the same profile, closed by window.close(); download refused when declined (nothing written) and saved to the chosen folder when accepted; Fill sign-in fills user + a quote/script-laden password on its own origin after the plain-HTTP warning and refuses another origin; 0.6.20: a sign-in in steps gets the user name, then the password when its box appears, then the TOTP code; a form in a shadow root is filled; a page with no form offers ⧉ user name / password / TOTP code (no TOTP secret, none copied); window mode pops the tab out; rendered portal-tab.png");
            }
            catch (Exception ex) { fail("portal tab (local HTTP)", ex); }
            finally { Dialogs.ConfirmForTests = confirmWas; }

            // ---- 5. live: self-signed HTTPS — refuse, trust (pin), change → refuse, forget → ask again ------------------
            try
            {
                certA = TestWeb.MakeCert("dcx-selftest-a");
                certB = TestWeb.MakeCert("dcx-selftest-b");
                https = TestWeb.Start(certA);
                int port = https.Port;
                var hp = $"127.0.0.1:{port}";
                var b = new PortalBookmark { Title = "Local TLS page", Url = $"https://127.0.0.1:{port}/" };
                Portals.Upsert(b); addedBookmarks.Add(b);
                var v = PortalUi.Open(b) ?? throw new Exception("not opened");
                if (!await Until(() => v.RefusedCert != null)) throw new Exception("self-signed certificate not refused");
                await Until(() => v.Security == WebSecurity.Untrusted, 3000);
                var shaA = TlsPin.Sha256(certA);
                if (v.RefusedCert!.Value.Sha256 != shaA || v.Security != WebSecurity.Untrusted || v.Core!.DocumentTitle == "DCX portal test") throw new Exception($"refusal: {v.Security} {v.RefusedCert?.Sha256[..8]}");
                if (!v.BannerText.Contains(TlsPin.Pretty(shaA))) throw new Exception("banner lacks the fingerprint");
                Dialogs.ConfirmForTests = true;
                if (!v.TrustRefused()) throw new Exception("trust refused");
                if (Portals.Pin(hp)?.Sha256 != shaA) throw new Exception("not pinned");
                if (!await Until(() => v.Core.DocumentTitle == "DCX portal test" && v.Security == WebSecurity.Pinned)) throw new Exception($"pinned page not loaded: {v.Security} “{v.Core.DocumentTitle}”");

                // The server now presents another certificate on the same host:port: refused as CHANGED.
                https.Dispose(); https = null;
                https = TestWeb.Start(certB, port);
                await Settle(300);
                v.Go(b.Url);
                if (!await Until(() => v.RefusedCert is { Decision: TlsDecision.Mismatch })) throw new Exception("changed certificate not refused");
                await Until(() => v.Security == WebSecurity.Changed, 3000);
                if (v.Security != WebSecurity.Changed || !v.BannerText.Contains("CERTIFICATE CHANGED") || Portals.Pin(hp)?.Sha256 != shaA) throw new Exception($"mismatch: {v.Security}");
                lock (Dialogs.SelfTestConfirms) Dialogs.SelfTestConfirms.Clear();
                if (!v.TrustRefused()) throw new Exception("replace refused");
                lock (Dialogs.SelfTestConfirms) if (!Dialogs.SelfTestConfirms.Any(c => c.Title == "Replace trusted certificate" && c.TypeToConfirm == hp)) throw new Exception("replacing didn't ask to type the host:port");
                if (Portals.Pin(hp)?.Sha256 != TlsPin.Sha256(certB)) throw new Exception("not re-pinned");
                if (!await Until(() => v.Security == WebSecurity.Pinned && v.Core.DocumentTitle == "DCX portal test")) throw new Exception("re-pinned page not loaded");

                // Forget: the next visit asks again (the browser session's acceptance is cleared too).
                Portals.Forget(hp);
                await BrowserView.ClearAcceptedAsync();
                v.Go(b.Url + "again");
                if (!await Until(() => v.RefusedCert is { Decision: TlsDecision.NeedsTrust })) throw new Exception("forgotten certificate still accepted");
                var raw = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(SecureStore.DataDir, "data.dcx")));
                if (raw.Contains(hp)) throw new Exception("pins in plaintext");
                snap(main, Path.Combine(shots, "portal-untrusted.png"));
                main.CloseTab(v.Tab!);
                ok("self-signed HTTPS (local test server): refused with its SHA-256 shown, “Trust this certificate” pins it per host:port and the page loads as “pinned”; a different certificate on the same host:port is refused as CERTIFICATE CHANGED (replacing asks to type host:port); Forget makes the next visit ask again; pins encrypted in data.dcx");
            }
            catch (Exception ex) { fail("portal certificates (TOFU)", ex); }
            finally { Dialogs.ConfirmForTests = confirmWas; }

            // ---- 6. offline mode: an external portal is refused before any connection ---------------------------------
            try
            {
                Offline.Set(true);
                int blockedBefore = Offline.BlockedTotal;
                var ext = new PortalBookmark { Title = "External (TEST-NET)", Url = "https://203.0.113.10/" };
                int tabs = main.Panes.AllTabs.Count();
                if (PortalUi.Open(ext) != null) throw new Exception("external portal opened while offline");
                await Settle(600);
                var v = view ?? throw new Exception("no open tab");
                v.Go("https://203.0.113.11/");
                await Settle(800);
                if ((v.Core?.Source ?? "").Contains("203.0.113")) throw new Exception("navigated while offline");
                if (Offline.BlockedTotal < blockedBefore + 2) throw new Exception($"blocks {Offline.BlockedTotal - blockedBefore}");
                if (main.Panes.AllTabs.Count() != tabs) throw new Exception("a tab opened for the refused portal");
                Offline.Set(false);
                ok("portal browser in offline mode: opening an external (TEST-NET) portal and navigating a tab to one are both refused before connecting, and listed as blocked");
            }
            catch (Exception ex) { fail("portal browser offline", ex); }
            finally { Offline.Set(offlineWas); }

            // ---- 7. Privacy report and the screens ----------------------------------------------------------------
            try
            {
                if (!PrivacyReport.PostureItems().Any(p => p.Name == "Portal browser" && p.Value.StartsWith("ON"))) throw new Exception("no posture row");
                if (!PrivacyReport.Endpoints().Any(e => e.Service.StartsWith("Portal browser"))) throw new Exception("no endpoint row");
                main.Navigate("portals"); await Settle(600);
                if (PortalsView.Current == null) throw new Exception("Portals screen not open");
                snap(main, Path.Combine(shots, "portals.png"));
                main.Navigate("addons"); await Settle(400);
                snap(main, Path.Combine(shots, "addons-portal-browser.png"));
                ok("portal browser in Privacy & network (posture + saved portals by network class); Portals and Add-ons screens rendered (portals.png, addons-portal-browser.png)");
            }
            catch (Exception ex) { fail("portal browser screens", ex); }

            // ---- 8. a changed add-on file turns the browser off -----------------------------------------------------
            try
            {
                if (addon == null) throw new Exception("no add-on");
                File.AppendAllText(Path.Combine(addon.Folder, "browser.json"), " ");
                if (PortalBrowser.Pack(addon) != null || addon.Enabled || PortalBrowser.Enabled) throw new Exception("tampered add-on still on");
                await Settle(300);
                if (main.NavKeys.Contains("portals") || PortalUi.HostMenu(host!) != null) throw new Exception("Portals still offered");
                ok("portal browser add-on: a changed file disables it (“files changed after install”), Portals leaves the sidebar and hosts lose Open web UI");
            }
            catch (Exception ex) { fail("portal browser tamper", ex); }
        }
        finally
        {
            foreach (var v in BrowserView.Live.ToList()) { try { if (v.Tab != null) main.CloseTab(v.Tab); else v.Dispose(); } catch { } }
            foreach (var b in addedBookmarks) Portals.All.RemoveAll(x => x.Id == b.Id);
            Portals.All.RemoveAll(x => x.Title.EndsWith("(self-test)"));
            AppData.Current.WebPins.RemoveAll(p => p.HostPort.StartsWith("127.0.0.1:"));
            if (host != null) AppData.Current.Hosts.RemoveAll(h => h.Id == host.Id);
            if (cred != null) AppData.Current.Credentials.RemoveAll(c => c.Id == cred.Id);
            AppData.Save();
            if (AddonPackages.Find("com.darkcipher.portal-browser") is { } a) AddonPackages.Remove(a);
            s.PortalOpenIn = openInWas; s.PortalDownloadFolder = folderWas; AppSettings.Save();
            PortalBrowser.SetBuiltIn(builtInWas);
            Dialogs.ConfirmForTests = confirmWas;
            http?.Dispose(); https?.Dispose(); certA?.Dispose(); certB?.Dispose();
            try { main.Navigate("dashboard"); } catch { }
        }
    }
}

/// <summary>A tiny loopback web server for the portal tests (HTTP, or HTTPS with a given certificate). 127.0.0.1 only.</summary>
internal sealed class TestWeb : IDisposable
{
    private readonly TcpListener _l;
    private readonly X509Certificate2? _cert;
    private readonly CancellationTokenSource _cts = new();
    public int Port { get; }

    private TestWeb(X509Certificate2? cert, int port)
    {
        _cert = cert;
        _l = new TcpListener(IPAddress.Loopback, port);
        _l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _l.Start();
        Port = ((IPEndPoint)_l.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public static TestWeb Start(X509Certificate2? cert, int port = 0) => new(cert, port);

    public static X509Certificate2 MakeCert(string cn)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var c = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(7));
        // SChannel needs a key it can open: round-trip through PFX (the temporary key goes with the certificate's Dispose).
        return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private async Task Serve(TcpClient c)
    {
        using (c)
        {
            try
            {
                Stream st = c.GetStream();
                if (_cert != null)
                {
                    var ssl = new SslStream(st, false);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _cert, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, _cts.Token);
                    st = ssl;
                }
                var buf = new byte[16384]; int n = 0;
                while (n < buf.Length)
                {
                    int r = await st.ReadAsync(buf.AsMemory(n), _cts.Token);
                    if (r == 0) return;
                    n += r;
                    if (Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n")) break;
                }
                var line = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0].Split(' ');
                var path = line.Length > 1 ? line[1] : "/";
                var (type, body, extra) = Route(path.Split('?')[0]);
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = $"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n{extra}\r\n";
                await st.WriteAsync(Encoding.ASCII.GetBytes(head), _cts.Token);
                await st.WriteAsync(bytes, _cts.Token);
                await st.FlushAsync(_cts.Token);
            }
            catch { /* refused handshakes and aborted requests are expected */ }
        }
    }

    private static (string Type, string Body, string Extra) Route(string path) => path switch
    {
        "/popup" => ("text/html; charset=utf-8", "<!doctype html><title>DCX popup</title><p>popup</p>", ""),
        "/login" => ("text/html; charset=utf-8", "<!doctype html><title>DCX login</title><form><input type=text id=u name=u><input type=password id=p name=p><button>Sign in</button></form>", ""),
        // 0.6.20: a sign-in in steps (user name, then password, then a code, as Synology DSM asks), one inside a shadow
        // root, and a page with no form at all.
        "/login2" => ("text/html; charset=utf-8", """
            <!doctype html><title>DCX login 2</title>
            <div id=s1><input type=text name=username id=un placeholder="Username"><button id=next type=button onclick="s1.style.display='none';s2.style.display='block'">Next</button></div>
            <div id=s2 style="display:none"><input type=password id=p2><button id=go type=button onclick="s2.style.display='none';s3.style.display='block'">Sign in</button></div>
            <div id=s3 style="display:none"><input type=text id=otp autocomplete=one-time-code inputmode=numeric placeholder="Enter verification code"></div>
            """, ""),
        "/shadow" => ("text/html; charset=utf-8", "<!doctype html><title>DCX shadow</title><div id=host></div><script>const r=document.getElementById('host').attachShadow({mode:'open'});r.innerHTML='<input type=email id=su><input type=password id=sp>';</script>", ""),
        "/noform" => ("text/html; charset=utf-8", "<!doctype html><title>DCX no form</title><p>Sign in with the app.</p>", ""),
        "/file.txt" => ("text/plain", "dcx-download-ok", "Content-Disposition: attachment; filename=\"report.txt\"\r\n"),
        "/favicon.ico" => ("text/plain", "", ""),
        _ => ("text/html; charset=utf-8", "<!doctype html><title>DCX portal test</title><h1>ok</h1>", ""),
    };

    public void Dispose()
    {
        _cts.Cancel();
        try { _l.Stop(); } catch { }
    }
}
