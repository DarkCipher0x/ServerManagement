using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Views;
using Microsoft.Win32;

namespace DcxManager;

/// <summary>
/// 0.3.0 preview.2 (ADR-040): Group Policy for every setting and the uninstaller's clean-up. Policies come from a made-up
/// source (the real HKLM / HKCU policy keys are never read in a test run); the clean-up runs against scratch folders and a
/// fake registry. No window, prompt or dialog.
/// </summary>
public static class PolicyTests
{
    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail, Action<Window, string> snap)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        var s = AppSettings.Current;

        // 1. The catalog accounts for every setting.
        try
        {
            if (PolicySettings.Unaccounted.Count > 0) throw new Exception("settings neither policy nor excluded: " + string.Join(", ", PolicySettings.Unaccounted));
            var props = typeof(AppSettings).GetProperties().Select(p => p.Name).ToHashSet();
            var stale = PolicySettings.Excluded.Keys.Where(k => !props.Contains(k)).ToList();
            if (stale.Count > 0) throw new Exception("excluded but no longer settings: " + string.Join(", ", stale));
            var secretish = PolicySettings.Catalog.Where(e => Regex.IsMatch(e.Name, "(Key|Keys|Token|Tokens|Password|Secret|Accounts)$")).Select(e => e.Name).ToList();
            if (secretish.Count > 0) throw new Exception("secret-looking settings are policies: " + string.Join(", ", secretish));
            if (PolicySettings.Catalog.Select(e => e.Name).Distinct().Count() != PolicySettings.Catalog.Count) throw new Exception("duplicate names");
            ok($"policy catalog: {PolicySettings.Catalog.Count} settings are policies in {PolicySettings.CategoryNames.Count()} categories, {PolicySettings.Excluded.Count} are not (each with its reason: accounts, secrets, per-PC state); no setting is unaccounted for and none that looks like a secret is a policy");
        }
        catch (Exception ex) { fail("policy catalog", ex); }

        // 2. Value rules.
        try
        {
            PolicySettings.Entry E(string n) => PolicySettings.Catalog.First(e => e.Name == n);
            string? C(string n, object d, RegistryValueKind k) => PolicySettings.Convert(E(n), d, k, out _);
            if (C("OfflineMode", 1, RegistryValueKind.DWord) != null || C("OfflineMode", 2, RegistryValueKind.DWord) == null || C("OfflineMode", "1", RegistryValueKind.String) == null) throw new Exception("on/off");
            if (C("LocalApiPort", 47321, RegistryValueKind.DWord) != null || C("LocalApiPort", 80, RegistryValueKind.DWord) == null) throw new Exception("number range");
            if (C("UiScale", "1.25", RegistryValueKind.String) != null || C("UiScale", "1,25", RegistryValueKind.String) == null || C("UiScale", "9", RegistryValueKind.String) == null) throw new Exception("decimal");
            if (C("ThemePreset", "graphite", RegistryValueKind.String) != null || C("ThemePreset", "neon", RegistryValueKind.String) == null) throw new Exception("choice");
            if (C("DefaultUsername", "ops", RegistryValueKind.String) != null || C("DefaultUsername", "a\u0007b", RegistryValueKind.String) == null) throw new Exception("text");
            if (C("OfflineAllowed", new[] { "api.example.com", " ", "api.example.com" }, RegistryValueKind.MultiString) != null || C("OfflineAllowed", "x", RegistryValueKind.String) == null) throw new Exception("list");
            PolicySettings.Convert(E("OfflineAllowed"), new[] { "api.example.com", " ", "api.example.com" }, RegistryValueKind.MultiString, out var listVal);
            if (listVal is not List<string> { Count: 1 }) throw new Exception("list not cleaned");
            if (C("ScreenshotDir", @"%USERPROFILE%\Shots", RegistryValueKind.ExpandString) != null) throw new Exception("REG_EXPAND_SZ path");
            ok("policy values: on/off only as DWORD 0 / 1, numbers inside their range, decimals as invariant text (1.25, not 1,25), choices from their list, text without control characters, lists as REG_MULTI_SZ (blanks and duplicates dropped), paths may be REG_EXPAND_SZ — anything else is refused");
        }
        catch (Exception ex) { fail("policy values", ex); }

        // 3. Enforcement: computer beats user, load and save put managed values back, problems listed, the switches and card.
        var saved = (s.Scrollback, s.AccentColor, s.TelnetEnabled, s.AiFeatures, s.UiScale);
        try
        {
            var hklm = new Dictionary<string, (object, RegistryValueKind)>
            {
                ["AccentColor"] = ("36D2CB", RegistryValueKind.String), ["TelnetEnabled"] = (0, RegistryValueKind.DWord),
                ["AiFeatures"] = (1, RegistryValueKind.DWord), ["Theme"] = ("Purple", RegistryValueKind.String), ["OpenAiKey"] = ("sk-test", RegistryValueKind.String),
            };
            var hkcu = new Dictionary<string, (object, RegistryValueKind)>
            {
                ["AccentColor"] = ("FF8800", RegistryValueKind.String), ["Scrollback"] = (5000, RegistryValueKind.DWord),
                ["UiScale"] = ("1.25", RegistryValueKind.String), ["NotASetting"] = (1, RegistryValueKind.DWord),
            };
            PolicySettings.SourceForTest = hive => hive == "HKLM" ? hklm : hkcu;
            PolicySettings.Reload();
            if (PolicySettings.Get("AccentColor") is not { Scope: "computer", Data: "36D2CB" }) throw new Exception("the computer value didn't win");
            if (PolicySettings.Get("Scrollback")?.Scope != "user" || PolicySettings.IsManaged("Theme") || PolicySettings.IsManaged("OpenAiKey")) throw new Exception("scopes / refusals");
            var probs = string.Join(" | ", PolicySettings.Problems);
            if (!probs.Contains("Theme") || !probs.Contains("OpenAiKey") || !probs.Contains("can't be set by policy") || !probs.Contains("NotASetting")) throw new Exception("problems: " + probs);
            if (AiFeatures.ReadPolicy() != 1) throw new Exception("the AI features policy isn't read through the catalog");
            PolicySettings.ApplyTo(s);
            if (s.Scrollback != 5000 || s.AccentColor != "36D2CB" || s.TelnetEnabled || Math.Abs(s.UiScale - 1.25) > 1e-9) throw new Exception("not applied");
            IReadOnlyList<string>? reverted = null;
            Action<IReadOnlyList<string>> onRevert = r => reverted = r;
            AppSettings.Reverted += onRevert;
            try { s.Scrollback = 1000; AppSettings.Save(); }
            finally { AppSettings.Reverted -= onRevert; }
            if (s.Scrollback != 5000 || reverted?.Contains("Scrollback") != true) throw new Exception("a managed value survived a save");

            if (main.Panes.AllTabs.FirstOrDefault(t => t.Content is SettingsView) is { } st0) main.CloseTab(st0);
            main.Navigate("settings");
            await Settle(400);
            var sv = SettingsView.Current ?? throw new Exception("no settings view");
            sv.RevealCard("Managed by your organisation");
            await Settle(300);
            var card = sv.CardForTest("Managed by your organisation") ?? throw new Exception("no policy card");
            var text = string.Join(" ", Menus.Descendants<TextBlock>(card).Select(t => t.Text));
            if (!text.Contains("set by your organisation") || !text.Contains("Accent") || !text.Contains("IGNORED") && !text.Contains("Theme")) throw new Exception("card: " + text[..Math.Min(240, text.Length)]);
            sv.RevealCategory("Security & privacy");
            await Settle(300);
            snap(main, Path.Combine(reportDir, "shots", "settings-policy.png"));
            ok("policy enforcement: the computer value beats the user's, invalid values / secrets / unknown names are ignored and listed, the AI features policy is read through the same catalog, values are put back on load and before every save (with a notice), Settings lists them under “Managed by your organisation”; rendered settings-policy.png");
        }
        catch (Exception ex) { fail("policy enforcement", ex); }
        finally
        {
            PolicySettings.SourceForTest = _ => null;
            PolicySettings.Reload();
            (s.Scrollback, s.AccentColor, s.TelnetEnabled, s.AiFeatures, s.UiScale) = saved;
            AppSettings.Save();
            if (main.Panes.AllTabs.FirstOrDefault(t => t.Content is SettingsView) is { } st1) main.CloseTab(st1);
        }

        // 4. The templates: one ADMX policy per setting, every string and presentation they name exists, docs are current.
        try
        {
            var admx = PolicyTemplates.Admx().ToString();
            var adml = PolicyTemplates.Adml().ToString();
            int count = Regex.Matches(admx, "<policy ").Count;
            if (count != PolicySettings.Catalog.Count + PolicyTemplates.MachineFlags.Length) throw new Exception($"{count} ADMX policies for {PolicySettings.Catalog.Count} settings + {PolicyTemplates.MachineFlags.Length} computer-only");
            var strings = Regex.Matches(adml, "<string id=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
            var presentations = Regex.Matches(adml, "<presentation id=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
            var missing = Regex.Matches(admx, @"\$\(string\.([^)]+)\)").Select(m => m.Groups[1].Value).Where(x => !strings.Contains(x))
                .Concat(Regex.Matches(admx, @"\$\(presentation\.([^)]+)\)").Select(m => "presentation " + m.Groups[1].Value).Where(x => !presentations.Contains(x["presentation ".Length..]))).Distinct().ToList();
            if (missing.Count > 0) throw new Exception("ADMX names without ADML text: " + string.Join(", ", missing.Take(8)));
            string? docs = null;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && docs == null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "docs", "POLICIES.md")) && File.Exists(Path.Combine(d.FullName, "build.ps1"))) docs = Path.Combine(d.FullName, "docs", "POLICIES.md");
            string docsNote;
            if (docs == null) docsNote = "docs/POLICIES.md not checked (source tree not next to the exe)";
            else if (File.ReadAllText(docs).Replace("\r\n", "\n") != PolicyTemplates.Markdown()) throw new Exception("docs/POLICIES.md is out of date: run the exe with --export-policy-templates <dir> and copy POLICIES.md");
            else docsNote = "docs/POLICIES.md matches the catalog";
            ok($"policy templates: the ADMX has one policy per setting ({count}), every string and presentation it names is in the en-US ADML; {docsNote}");
        }
        catch (Exception ex) { fail("policy templates", ex); }

        // 5. Uninstall clean-up on scratch folders and a fake registry.
        var root = Path.Combine(SecureStore.DataDir, "uninstall-test");
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            var store = Path.Combine(root, "Roaming", AppInfo.Company, "ServerManagement");
            var cache = Path.Combine(root, "Local", AppInfo.Company, "ServerManagement");
            var legacyStore = Path.Combine(root, "Roaming", AppInfo.LegacyVendorFolder, "ServerManagement");   // a pre-0.4.0 folder left behind
            var temp = Path.Combine(root, "Temp");
            var moved = Path.Combine(root, "MovedStore");
            foreach (var d in new[] { store, cache, legacyStore, temp, moved, Path.Combine(temp, AppInfo.RequestsTempFolder) }) Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(legacyStore, "settings.dcx"), "x");
            File.WriteAllText(Path.Combine(store, "keyring.json"), "{\"Version\":1,\"Hello\":{\"KeyName\":\"DarkCipher ServerManagement TEST\",\"Challenge\":\"\",\"Salt\":\"\",\"Blob\":\"\"}}");
            File.WriteAllText(Path.Combine(store, "location.json"), System.Text.Json.JsonSerializer.Serialize(new { DataDir = moved }));
            File.WriteAllText(Path.Combine(store, "library-abc.dcx"), "x");
            File.WriteAllText(Path.Combine(store, "settings.dcx"), "x");
            File.WriteAllText(Path.Combine(moved, "settings.dcx"), "x");
            File.WriteAllText(Path.Combine(moved, "library-def.dcx"), "x");
            File.WriteAllText(Path.Combine(moved, "my-own-notes.txt"), "not ours");
            File.WriteAllText(Path.Combine(cache, "webview.bin"), "x");
            File.WriteAllText(Path.Combine(temp, AppInfo.RequestsTempFolder, "r.json"), "x");
            File.WriteAllText(Path.Combine(temp, "dcx-addon-1.exe"), "x");
            File.WriteAllText(Path.Combine(temp, "someone-else.tmp"), "x");
            var p = new Uninstall.Profile(store, cache, temp);
            var hello = new List<string>();

            // Nothing ticked: only the app's own temp files go.
            Uninstall.CleanUser(p, Uninstall.Options.Parse(new[] { "data=", "cache=0" }), hello.Add);
            if (Directory.Exists(Path.Combine(temp, AppInfo.RequestsTempFolder)) || File.Exists(Path.Combine(temp, "dcx-addon-1.exe")) || !File.Exists(Path.Combine(temp, "someone-else.tmp"))) throw new Exception("temp clean-up");
            if (!File.Exists(Path.Combine(store, "settings.dcx")) || !File.Exists(Path.Combine(legacyStore, "settings.dcx")) || !Directory.Exists(cache) || hello.Count > 0) throw new Exception("removed something not ticked");
            // Library only: the membership files in both stores, nothing else.
            Uninstall.CleanUser(p, Uninstall.Options.Parse(new[] { "library=1" }), hello.Add);
            if (File.Exists(Path.Combine(store, "library-abc.dcx")) || File.Exists(Path.Combine(moved, "library-def.dcx")) || !File.Exists(Path.Combine(store, "settings.dcx"))) throw new Exception("library clean-up");
            // Data: the store, the moved store's files (not the user's own file or folder), and the Hello key.
            Uninstall.CleanUser(p, Uninstall.Options.Parse(new[] { "data=1" }), hello.Add);
            if (Directory.Exists(store) || File.Exists(Path.Combine(moved, "settings.dcx")) || !File.Exists(Path.Combine(moved, "my-own-notes.txt"))) throw new Exception("data clean-up");
            if (Directory.Exists(legacyStore) || Directory.Exists(Path.GetDirectoryName(legacyStore))) throw new Exception("the pre-0.4.0 store folder (and its empty DarkCipher parent) was not removed with data");
            if (!hello.SequenceEqual(new[] { "DarkCipher ServerManagement TEST" })) throw new Exception("Windows Hello key not removed: " + string.Join(", ", hello));
            if (!Directory.Exists(cache)) throw new Exception("cache removed without being ticked");
            Uninstall.CleanUser(p, Uninstall.Options.Parse(new[] { "cache=1" }), hello.Add);
            if (Directory.Exists(cache) || Directory.Exists(Path.GetDirectoryName(cache))) throw new Exception("cache (and its empty DarkCipher Systems folder) not removed");

            // Machine: the app's keys always (the 0.4.0 one and the darkcipher0x one of older releases); settings and every user only when asked.
            var fake = new FakeMachine(Path.Combine(root, "Users"));
            var other = Path.Combine(root, "Users", "alice");
            Directory.CreateDirectory(Path.Combine(other, "AppData", "Roaming", AppInfo.Company, "ServerManagement"));
            Uninstall.CleanMachine(Uninstall.Options.Parse(new[] { "settings=0" }), fake);
            if (fake.Deleted.Count != 2 || !fake.Deleted[0].EndsWith(@"DarkCipher Systems\ServerManagement\Install", StringComparison.Ordinal) || !fake.Deleted[1].EndsWith(@"darkcipher0x\ServerManagement", StringComparison.Ordinal)) throw new Exception("machine (nothing ticked): " + string.Join(", ", fake.Deleted));
            fake.Deleted.Clear();
            Uninstall.CleanMachine(Uninstall.Options.Parse(new[] { "everything=1" }), fake);
            if (!fake.Deleted.Any(d => d.Contains("Protected")) || !fake.Deleted.Any(d => d.StartsWith("LocalMachine", StringComparison.Ordinal) && d.Contains(@"Policies\DarkCipher Systems")) || !fake.Deleted.Any(d => d.StartsWith("Users\\S-1-5-21-1", StringComparison.Ordinal)))
                throw new Exception("machine (everything): " + string.Join(", ", fake.Deleted));
            if (Directory.Exists(Path.Combine(other, "AppData", "Roaming", AppInfo.Company))) throw new Exception("another user's store was not removed with allusers");
            if (Uninstall.Options.Parse(new[] { "everything=1" }) is not { Data: true, Cache: true, Library: true, Settings: true, AllUsers: true }) throw new Exception("REMOVE_EVERYTHING");
            ok("uninstall clean-up (scratch folders, fake registry): with nothing ticked only the app's temp files go; the library option removes only membership files (in a moved store too); data removes the store (the pre-0.4.0 DarkCipher folder too), a moved store's files but not the user's own files or folder, and the Windows Hello key; cache removes the cache and its empty parent; the machine step always removes the app's HKLM keys (DarkCipher Systems\\ServerManagement\\Install and the old darkcipher0x one), and with settings / every user the protected records, policy keys (computer and signed-in users) and other users' data; REMOVE_EVERYTHING ticks all");
        }
        catch (Exception ex) { fail("uninstall clean-up", ex); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private sealed class FakeMachine(string usersDir) : Uninstall.IMachine
    {
        public readonly List<string> Deleted = new();
        public void DeleteKey(RegistryHive hive, string path) => Deleted.Add($"{hive}\\{path}");
        public IEnumerable<string> LoadedUserSids() => new[] { "S-1-5-21-1-2-3-1001" };
        public IEnumerable<string> ProfileFolders() => Directory.Exists(usersDir) ? Directory.GetDirectories(usersDir) : Array.Empty<string>();
    }
}
