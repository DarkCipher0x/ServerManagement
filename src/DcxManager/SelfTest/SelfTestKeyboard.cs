using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Services;
using DcxManager.Services.Keys;
using DcxManager.Views;
using Microsoft.Win32;

namespace DcxManager;

/// <summary>
/// 0.6.36 (ADR-115): keyboard control. Key text is read against the allow-list only (every key round-trips, malformed and
/// reserved keys are refused, a fuzz pass never throws), the stored list is bounded and skips what it can't read, every
/// command's defaults are valid and clash-free, the old shortcuts keep their keys, terminals keep plain Ctrl+letter keys,
/// clashes are refused or moved on purpose, Group Policy locks the page, the list is stored encrypted, and Settings →
/// Keyboard is driven through its own rows, capture box and buttons (no window opens).
/// </summary>
public static class KeyboardTests
{
    private sealed class FakeRemote : Border, IRemoteKeyboard { }
    private sealed class FakeOwner(Func<KeyChord, bool> owns) : Border, IOwnKeys { public bool OwnsKey(KeyChord chord) => owns(chord); }

    public static Task RunAsync(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        var s = AppSettings.Current;
        var saved = (s.KeyBindings.ToList(), s.KeyBindingsAllowChange);
        int runs = 0;
        try
        {
            Chords();
            StoredList();
            Defaults(main);

            // Dispatch: a probe command, with a key terminals keep (Alt+F12) and one they don't (Ctrl+Alt+F12).
            KeyCommands.Register(new KeyCommand("selftest.probe", "Self-test probe", "Self-test", KeyScopes.App, KeyCommands.Keys("Alt+F12")) { Run = () => runs++ });
            KeyCommands.Register(new KeyCommand("selftest.remote", "Self-test probe (remote)", "Self-test", KeyScopes.App, KeyCommands.Keys("Ctrl+Alt+F12")) { Run = () => runs += 10 });
            KeyCommands.Register(new KeyCommand("selftest.off", "Self-test probe (can't run)", "Self-test", KeyScopes.App, KeyCommands.Keys("Ctrl+Alt+F11")) { Run = () => runs += 100, CanRun = () => false });
            KeyCommands.Register(new KeyCommand("selftest.throws", "Self-test probe (throws)", "Self-test", KeyScopes.App, KeyCommands.Keys("Ctrl+Alt+F10")) { Run = () => throw new InvalidOperationException("probe") });
            var altF12 = KeyChord.Parse("Alt+F12")!.Value;
            if (KeyCommands.TryRun(altF12, remoteHasKeyboard: true) || runs != 0) throw new Exception("a key a terminal keeps ran while a terminal had the keyboard");
            if (!KeyCommands.TryRun(altF12, remoteHasKeyboard: false) || runs != 1) throw new Exception("Alt+F12 didn't run its command");
            if (!KeyCommands.TryRun(KeyChord.Parse("Ctrl+Alt+F12")!.Value, remoteHasKeyboard: true) || runs != 11) throw new Exception("Ctrl+Alt+F12 didn't run in a terminal");
            if (KeyCommands.TryRun(KeyChord.Parse("Ctrl+Alt+F11")!.Value, false) || runs != 11) throw new Exception("a command that can't run took its key");
            if (!KeyCommands.TryRun(KeyChord.Parse("Ctrl+Alt+F10")!.Value, false)) throw new Exception("a command that threw wasn't handled");
            if (KeyCommands.TryRun(KeyChord.Parse("Ctrl+Alt+Shift+F9")!.Value, false)) throw new Exception("an unbound key was handled");
            var remote = new FakeRemote(); var inside = new TextBox(); remote.Child = inside;
            if (!RemoteKeyboard.Inside(inside) || RemoteKeyboard.Inside(new TextBox())) throw new Exception("a terminal's keyboard isn't recognised");
            ok("keyboard: dispatch (terminals keep plain Ctrl / Alt / F-keys, Ctrl+Shift and Ctrl+Alt reach the app, a command that can't run leaves its key, one that throws is contained, unbound keys pass)");

            ScreenKeyRules();
            Changes();
            Policy(main);
            Page(main);
            Transfer(main);
            Window(main);
            Encrypted();
            ok($"keyboard (0.6.36): {KeyCommands.All.Count} commands, {KeyCommands.All.Count(c => c.Scope == KeyScopes.App)} app-wide, every default valid and clash-free, the commander and Docs keep their own keys, Settings → Keyboard changes, moves, removes and resets keys through its own controls, policy locks it, stored encrypted");
        }
        catch (Exception ex) { fail("keyboard (0.6.36)", ex); }
        finally
        {
            PolicySettings.SourceForTest = _ => null;
            PolicySettings.Reload();
            KeyCommands.RemoveWhere(c => c.Id.StartsWith("selftest.", StringComparison.Ordinal));
            (s.KeyBindings, s.KeyBindingsAllowChange) = saved;
            try { AppSettings.Save(); } catch { }
            KeyCommands.NotifyChanged();
            if (main.Panes.AllTabs.FirstOrDefault(t => t.Content is SettingsView) is { } st) main.CloseTab(st);
        }
        return Task.CompletedTask;
    }

    private static void Expect(bool condition, string what) { if (!condition) throw new Exception(what); }

    /// <summary>A screen's own keys win while it has the keyboard; an owner that throws is contained; every app command a
    /// screen takes a default key from is still reachable there (another default key, or the command palette).</summary>
    private static void ScreenKeyRules()
    {
        var ctrlB = KeyChord.Parse("Ctrl+B")!.Value;
        var owner = new FakeOwner(c => c == ctrlB); var inner = new TextBox(); owner.Child = inner;
        Expect(ScreenKeys.Claimed(inner, ctrlB) && !ScreenKeys.Claimed(inner, KeyChord.Parse("Ctrl+Shift+B")!.Value), "a screen's own key isn't recognised");
        Expect(!ScreenKeys.Claimed(new TextBox(), ctrlB), "a key was claimed outside the screen");
        var bad = new FakeOwner(_ => throw new InvalidOperationException("probe")); var inBad = new TextBox(); bad.Child = inBad;
        Expect(!ScreenKeys.Claimed(inBad, ctrlB), "a screen that threw kept the key");
        foreach (var (screen, keys) in new[] { ("Docs", DocsView.EditorKeys), ("File commander", CommanderView.OwnKeys) })
            foreach (var c in KeyCommands.All.Where(c => c.Scope == KeyScopes.App && c.Defaults.Any(keys.Contains)))
                Expect(c.Defaults.Any(d => !keys.Contains(d)) || c.InPalette, $"{screen} takes every key of {c.Id}");
        Expect(DocsView.EditorKeys.Contains(ctrlB) && CommanderView.OwnKeys.Contains(KeyChord.Parse("F6")!.Value), "the Docs / commander key sets are missing their clashes");
    }

    // ---- key text ---------------------------------------------------------------------------------------------------

    private static void Chords()
    {
        var mods = new[] { ModifierKeys.None, ModifierKeys.Control, ModifierKeys.Alt, ModifierKeys.Shift, ModifierKeys.Control | ModifierKeys.Shift, ModifierKeys.Control | ModifierKeys.Alt, ModifierKeys.Alt | ModifierKeys.Shift, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift };
        int n = 0;
        foreach (var k in KeyChord.AllowedKeys)
            foreach (var m in mods)
            {
                var c = new KeyChord(k, m);
                Expect(KeyChord.Parse(c.Name) == c, $"{c.Name} doesn't read back");
                Expect(KeyChord.Parse(c.Name.ToLowerInvariant()) == c, $"{c.Name} in lower case doesn't read back");
                if (k != Key.OemBackslash) Expect(KeyChord.Parse(c.Shown) == c, $"{c.Shown} (shown) doesn't read back");
                n++;
            }
        Expect(KeyChord.Parse("shift+ctrl+p") == new KeyChord(Key.P, ModifierKeys.Control | ModifierKeys.Shift), "modifier order or case matters");
        Expect(KeyChord.Parse("Control+Alt+Del") == new KeyChord(Key.Delete, ModifierKeys.Control | ModifierKeys.Alt), "aliases");
        Expect(KeyChord.Parse("Ctrl++") == new KeyChord(Key.OemPlus, ModifierKeys.Control), "Ctrl++");
        Expect(KeyChord.Parse("Ctrl+Shift+,") == new KeyChord(Key.OemComma, ModifierKeys.Control | ModifierKeys.Shift), "Ctrl+Shift+,");
        foreach (var bad in new[] { null, "", "   ", "Win+A", "Ctrl+Ctrl+A", "Ctrl+", "Ctrl+Shift", "Ctrl+Foo", "Ctrl+A+B", "Hyper+A", "Ctrl+\u0007A", new string('A', 41), "Ctrl+Alt+Shift+Ctrl+A", "PrintScreen", "Ctrl+NumPad1", "Ctrl+LWin" })
            Expect(!KeyChord.TryParse(bad, out _, out var err) && err != null, $"“{bad}” was read as a key");
        foreach (var (text, allowed) in new[] { ("A", false), ("Shift+A", false), ("Enter", false), ("Space", false), ("Ctrl+C", false), ("Ctrl+V", false), ("Alt+F4", false), ("Ctrl+Shift+C", false), ("Shift+Tab", false), ("Shift+F10", false),
                     ("F5", true), ("Shift+F3", true), ("Ctrl+Shift+P", true), ("Alt+Left", true), ("Ctrl+Comma", true), ("Pause", true), ("Ctrl+W", true) })
            Expect((KeyChord.Parse(text)!.Value.Problem() == null) == allowed, $"{text}: {(allowed ? "refused" : "allowed")}");
        foreach (var (text, remote) in new[] { ("Ctrl+W", false), ("Ctrl+Shift+W", true), ("Ctrl+Tab", true), ("Ctrl+F1", true), ("F1", false), ("Alt+Left", false), ("Ctrl+Alt+T", true), ("Ctrl+1", false), ("Ctrl+Shift+Tab", true) })
            Expect(KeyChord.Parse(text)!.Value.WorksInRemoteSessions == remote, $"{text} in a terminal: {(remote ? "kept from the app" : "taken from the program")}");
        Expect(KeyChord.From(Key.LeftCtrl, ModifierKeys.Control) == null && KeyChord.From(Key.A, ModifierKeys.Windows) == null && KeyChord.From(Key.PrintScreen, ModifierKeys.None) == null, "a lone modifier, the Windows key or Print Screen became a key");

        // Fuzz: random text (letters, digits, separators, symbols, non-Latin and control characters) never throws, and
        // anything accepted reads back to itself.
        var rnd = new Random(1536);
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+++;;==,.-/\\[]'`  ←→↑↓\u0000\u0007\u001bé中CtrlAltShiftWinF";
        for (int i = 0; i < 4000; i++)
        {
            var len = rnd.Next(0, 48);
            var t = new string(Enumerable.Range(0, len).Select(_ => alphabet[rnd.Next(alphabet.Length)]).ToArray());
            if (KeyChord.TryParse(t, out var c, out _)) Expect(KeyChord.Parse(c.Name) == c, $"fuzz: “{t}” read as {c.Name}, which doesn't read back");
            KeyBindingList.Parse(new[] { t, "a.b=" + t, t + "=" + t });
        }
    }

    private static void StoredList()
    {
        var p = KeyBindingList.Parse(new[]
        {
            "tab.close=Ctrl+Shift+Q", "tab.close=Ctrl+Shift+Z", "Tab.Close=F2", "x=Ctrl+Shift+X", "pane.tile=Ctrl+C", "app.lock=",
            "pane.merge=F1;F2;F3;F4", "pane.next=F7;Ctrl+C", "no equals sign", "a.b=" + new string('F', 260), "c.d=Ctrl+\u0001",
        });
        Expect(p.Overrides.TryGetValue("tab.close", out var tc) && tc.SequenceEqual(new[] { KeyChord.Parse("Ctrl+Shift+Q")!.Value }), "the first of a repeated line wasn't kept");
        Expect(!p.Overrides.ContainsKey("Tab.Close") && !p.Overrides.ContainsKey("x"), "a malformed id was kept");
        Expect(!p.Overrides.ContainsKey("pane.tile"), "a line that was all wrong turned a command's keys off");
        Expect(p.Overrides.TryGetValue("app.lock", out var al) && al.Length == 0, "id= didn't mean no keys");
        Expect(p.Overrides["pane.merge"].Length == KeyBindingList.MaxChordsPerCommand, "more than three keys were kept");
        Expect(p.Overrides["pane.next"].SequenceEqual(new[] { KeyChord.Parse("F7")!.Value }), "the good key of a half-wrong line was lost");
        Expect(!p.Overrides.ContainsKey("a.b") && !p.Overrides.ContainsKey("c.d"), "an overlong or control-character line was kept");
        Expect(p.Problems.Count >= 8, "problems weren't listed: " + p.Problems.Count);
        var many = KeyBindingList.Parse(Enumerable.Range(0, KeyBindingList.MaxEntries + 50).Select(i => $"x.n{i}=F{i % 12 + 1}"));
        Expect(many.Overrides.Count == KeyBindingList.MaxEntries && many.Problems.Any(x => x.Contains("more than")), "the line limit");
        var lines = KeyBindingList.Lines(p.Overrides);
        Expect(KeyBindingList.Parse(lines).Overrides.Count == p.Overrides.Count && lines.SequenceEqual(lines.OrderBy(x => x, StringComparer.Ordinal)), "stored lines don't read back, or aren't sorted");
    }

    private static void Defaults(MainWindow main)
    {
        AppSettings.Current.KeyBindings = new List<string>();
        Expect(KeyCommands.Problems.Count == 0, "the defaults clash: " + string.Join(" | ", KeyCommands.Problems));
        // The keys before 0.6.36 keep working.
        foreach (var (id, keys) in new[]
        {
            ("tab.close", "Ctrl+W|Ctrl+Shift+W"), ("app.quick-connect", "Ctrl+K|Ctrl+Shift+K"), ("nav.sidebar", "Ctrl+B|Ctrl+Shift+B"), ("app.help", "F1|Ctrl+F1"),
            ("pane.next", "F6|Ctrl+F6"), ("tab.next", "Ctrl+Tab"), ("tab.previous", "Ctrl+Shift+Tab"), ("pane.split-right", "Ctrl+Alt+Right"),
            ("pane.split-down", "Ctrl+Alt+Down"), ("app.lock", "Ctrl+Shift+L"), ("app.palette", "Ctrl+Shift+P"), ("app.screenshot", "Ctrl+Shift+S"), ("pane.tile", "Ctrl+Alt+T"),
            ("pane.merge", "Ctrl+Alt+U"), ("pane.pop-out", "Ctrl+Alt+O"), ("window.new", "Ctrl+Shift+N"), ("go.settings", "Ctrl+Comma"),
            (ViewCommands.PortalReload, "F5"), (ViewCommands.PortalAddress, "Ctrl+L"), (ViewCommands.NotesSaveVersion, "Ctrl+S"), (ViewCommands.EditorFindPrevious, "Shift+F3"), (ViewCommands.PageFind, "Ctrl+F"),
        })
            Expect(KeyCommands.Effective(id).Select(k => k.Name).SequenceEqual(keys.Split('|').Select(k => KeyChord.Parse(k)!.Value.Name)), $"{id} is {KeyCommands.TextAll(id)}, not {keys.Replace("|", " or ")}");
        // Every page has a command; the AI module's command is there when the module is.
        foreach (var key in main.AllNavKeys) Expect(KeyCommands.Find(MainWindow.PageCommandId(key)) is { Run: not null }, "no command for page " + key);
        Expect(KeyCommands.All.All(c => c.Scope != KeyScopes.App || c.Run != null) && KeyCommands.All.All(c => c.Scope == KeyScopes.App || c.Run == null), "app commands run here, view commands in their screen");
        Expect(KeyCommands.All.Select(c => c.Id).Distinct().Count() == KeyCommands.All.Count, "a command id is registered twice");
        // Overlapping screens: F5 is both the portal's reload and the file browser's refresh, which never share the keyboard.
        Expect(KeyCommands.Match(KeyChord.Parse("F5")!.Value, KeyScopes.Portal)?.Id == ViewCommands.PortalReload && KeyCommands.Match(KeyChord.Parse("F5")!.Value, KeyScopes.Page)?.Id == ViewCommands.PageRefresh, "screen keys");
        Expect(KeyCommands.Match(KeyChord.Parse("Ctrl+S")!.Value, KeyScopes.Editor)?.Id == ViewCommands.EditorSave && KeyCommands.Match(KeyChord.Parse("Ctrl+S")!.Value, KeyScopes.Notes)?.Id == ViewCommands.NotesSaveVersion, "Ctrl+S in the editors and notes");
        // The palette lists the app's commands with their keys.
        var palette = CommandPalette.Items(main);
        Expect(palette.FirstOrDefault(i => i.Key == "action:pane.tile") is { } tile && tile.Detail.Contains("Ctrl+Alt+T"), "the palette doesn't list Tile all tabs with its key");
        Expect(!palette.Any(i => i.Key.StartsWith("action:go.", StringComparison.Ordinal) || i.Key.StartsWith("action:" + ViewCommands.PortalReload, StringComparison.Ordinal)), "pages or screen keys listed as palette actions");
    }

    // ---- changing keys --------------------------------------------------------------------------------------------

    private static void Changes()
    {
        var s = AppSettings.Current;
        s.KeyBindings = new List<string>();
        var ctrlShiftP = KeyChord.Parse("Ctrl+Shift+P")!.Value;
        Expect(KeyCommands.Set("selftest.probe", new[] { ctrlShiftP }) is { } refused && refused.Contains("Command palette"), "a clash wasn't refused");
        Expect(KeyCommands.Set("selftest.probe", new[] { ctrlShiftP }, takeFromOthers: true) == null, "a clash couldn't be moved on purpose");
        Expect(KeyCommands.Effective("selftest.probe").SequenceEqual(new[] { ctrlShiftP }) && KeyCommands.Effective("app.palette").Count == 0 && KeyCommands.IsChanged("app.palette"), "the moved key");
        Expect(s.KeyBindings.Contains("app.palette=") && s.KeyBindings.Contains("selftest.probe=Ctrl+Shift+P"), "stored lines: " + string.Join(", ", s.KeyBindings));
        Expect(KeyCommands.Reset("app.palette") is { } r2 && r2.Contains("Self-test probe"), "a reset took a key back from another command");
        Expect(KeyCommands.Set("selftest.probe", KeyCommands.Keys("Alt+F12")) == null && !s.KeyBindings.Any(l => l.StartsWith("selftest.probe", StringComparison.Ordinal)), "keys back on the defaults kept a line");
        Expect(KeyCommands.Reset("app.palette") == null && KeyCommands.Effective("app.palette").SequenceEqual(new[] { ctrlShiftP }) && s.KeyBindings.Count == 0, "reset");
        Expect(KeyCommands.Set("selftest.probe", KeyCommands.Keys("F1", "F2", "F3").Append(KeyChord.Parse("F4")!.Value).ToList()) != null, "four keys were accepted");
        Expect(KeyCommands.Set("selftest.probe", new[] { new KeyChord(Key.C, ModifierKeys.Control) }) != null, "Ctrl+C was accepted");
        Expect(KeyCommands.Set("no.such-command", Array.Empty<KeyChord>()) != null, "an unknown command was changed");
        // App keys and portal keys share the keyboard, editor keys don't.
        Expect(KeyCommands.Conflicts("selftest.probe", KeyChord.Parse("F5")!.Value).Any(c => c.Id == ViewCommands.PortalReload), "an app key on F5 doesn't clash with the portal's reload");
        Expect(!KeyCommands.Conflicts("selftest.probe", KeyChord.Parse("Ctrl+H")!.Value).Any(c => c.Id == ViewCommands.EditorReplace), "an app key clashes with the editor window's key");
        // A clash in the stored list (or a policy's): the first command keeps the key, and it is listed.
        s.KeyBindings = new List<string> { "selftest.probe=Ctrl+Shift+P" };
        Expect(KeyCommands.Effective("app.palette").SequenceEqual(new[] { ctrlShiftP }) && KeyCommands.Effective("selftest.probe").Count == 0 && KeyCommands.Problems.Any(p => p.Contains("bound to both")), "a stored clash");
        s.KeyBindings = new List<string>();
        Expect(KeyCommands.ResetAll() == null && KeyCommands.Problems.Count == 0, "reset all");
    }

    private static void Policy(MainWindow main)
    {
        var s = AppSettings.Current;
        // The organisation sets the list: it is in force, the page and every change say so.
        PolicySettings.SourceForTest = hive => hive == "HKLM"
            ? new Dictionary<string, (object, RegistryValueKind)> { [nameof(AppSettings.KeyBindings)] = (new[] { "pane.tile=Ctrl+Alt+Shift+T", "bad line" }, RegistryValueKind.MultiString) }
            : null;
        PolicySettings.Reload();
        AppSettings.Save();   // managed values are put back on save
        Expect(PolicySettings.IsManaged(nameof(AppSettings.KeyBindings)) && KeyCommands.Text("pane.tile") == "Ctrl+Alt+Shift+T", "the policy's list isn't in force: " + KeyCommands.TextAll("pane.tile"));
        Expect(!KeyCommands.CanEdit && KeyCommands.Set("pane.tile", KeyCommands.Keys("Ctrl+Alt+Y")) is { } why && why.Contains("organisation") && KeyCommands.ResetAll() != null, "a change was allowed under policy");
        Expect(KeyCommands.Problems.Any(p => p.Contains("bad line")), "the policy's bad line isn't listed");
        // Or keeps the defaults.
        PolicySettings.SourceForTest = hive => hive == "HKCU" ? new Dictionary<string, (object, RegistryValueKind)> { [nameof(AppSettings.KeyBindingsAllowChange)] = (0, RegistryValueKind.DWord) } : null;
        PolicySettings.Reload();
        s.KeyBindings = new List<string>();
        AppSettings.Save();
        Expect(!s.KeyBindingsAllowChange && KeyCommands.WhyLocked is { } w && w.Contains("default") && KeyCommands.Set("pane.tile", KeyCommands.Keys("Ctrl+Alt+Y")) != null, "changes allowed with KeyBindingsAllowChange off");
        main.Navigate("settings");
        var page = SettingsView.Current?.Keys ?? throw new Exception("the Keyboard page wasn't built");
        page.Render();
        Expect(page.Rows.Count > 20 && page.Rows.Values.All(r => !r.ChangeEnabled), "the page let a locked shortcut be changed");
        PolicySettings.SourceForTest = _ => null;
        PolicySettings.Reload();
        s.KeyBindingsAllowChange = true;
        s.KeyBindings = new List<string>();
        AppSettings.Save();
        Expect(KeyCommands.CanEdit, "still locked after the policy went");
    }

    // ---- Settings → Keyboard, through its controls ----------------------------------------------------------------

    private static void Page(MainWindow main)
    {
        main.Navigate("settings");
        var sv = SettingsView.Current ?? throw new Exception("Settings didn't open");
        sv.RevealCategory(SettingsView.KeyboardCategory);
        Expect(sv.ShownCategory == SettingsView.KeyboardCategory, "the Keyboard tab didn't open: " + sv.ShownCategory);
        var page = sv.Keys ?? throw new Exception("no Keyboard page");
        page.Filter = "split right";
        Expect(page.Rows.ContainsKey("pane.split-right") && page.Rows.Count < 5, "search by name: " + string.Join(", ", page.Rows.Keys));
        page.Filter = "ctrl+shift+p";
        Expect(page.Rows.ContainsKey("app.palette"), "search by key");
        page.Filter = "";

        // Change… → a free key → Use these keys.
        var row = page.Rows["pane.split-right"];
        row.Open();
        Expect(row.Editing && !row.CanUse, "the editor didn't open empty");
        row.Capture.Press(Key.LeftCtrl, ModifierKeys.Control);
        Expect(row.Capture.Chord == null && !row.CanUse, "a lone modifier was recorded");
        row.Capture.Press(Key.C, ModifierKeys.Control);
        Expect(row.Saying.StartsWith("⚠", StringComparison.Ordinal) && !row.CanUse, "Ctrl+C could be used: " + row.Saying);
        row.Capture.Press(Key.Q, ModifierKeys.Control | ModifierKeys.Alt);
        Expect(row.Saying.Contains("free") && row.CanUse && row.CanAdd, "a free key: " + row.Saying);
        Expect(row.Commit(add: false) && KeyCommands.TextAll("pane.split-right") == "Ctrl+Alt+Q" && page.ResultText.StartsWith("✓", StringComparison.Ordinal), "Use these keys: " + page.ResultText);
        Expect(AppSettings.Current.KeyBindings.Contains("pane.split-right=Ctrl+Alt+Q"), "not stored");

        // A key another command has: said first, then moved on purpose ("Add as another key").
        row = page.Rows["tab.close"];
        row.Open();
        row.Capture.Press(Key.P, ModifierKeys.Control | ModifierKeys.Shift);
        Expect(row.Saying.Contains("Command palette") && row.Saying.Contains("takes it from there") && row.CanAdd, "the clash wasn't said: " + row.Saying);
        Expect(row.Commit(add: true) && KeyCommands.Effective("tab.close").Count == 3 && KeyCommands.Effective("app.palette").Count == 0, "Add as another key");
        // A key a terminal keeps: said.
        row = page.Rows["app.screenshot"];
        row.Open();
        row.Capture.Press(Key.F9, ModifierKeys.None);
        Expect(row.Saying.Contains("remote program") && row.CanUse, "a key terminals keep wasn't said: " + row.Saying);
        row.Close();
        Expect(!row.Editing, "Cancel");

        // Remove, then Default.
        page.Rows["pane.tile"].Remove();
        Expect(KeyCommands.Effective("pane.tile").Count == 0 && page.Rows["pane.tile"].ResetEnabled, "Remove");
        page.Rows["pane.tile"].Reset();
        Expect(KeyCommands.TextAll("pane.tile") == "Ctrl+Alt+T" && !KeyCommands.IsChanged("pane.tile"), "Default");
        Expect(KeyboardPageText().Contains("| Tile all tabs | Ctrl+Alt+T | everywhere in the app | pane.tile |"), "the copied list");
        page.ResetAll();
        Expect(AppSettings.Current.KeyBindings.Count == 0 && KeyCommands.TextAll("app.palette") == "Ctrl+Shift+P" && KeyCommands.TextAll("pane.split-right") == "Ctrl+Alt+→", "Reset all");
        var audit = AuditLog.Read(DateTime.Now.ToString("yyyy-MM")).Where(e => e.Target == "keyboard").ToList();
        Expect(audit.Any(e => e.Action == "shortcut changed" && e.Result.Contains("pane.split-right")) && audit.Any(e => e.Action == "shortcuts reset"), "changes aren't in the audit log");
    }

    private static string KeyboardPageText() => SettingsView.KeyboardPage.ListText();

    private static void Transfer(MainWindow main)
    {
        var s = AppSettings.Current;
        s.KeyBindings = new List<string> { "pane.tile=Ctrl+Alt+Shift+T" };
        var json = AppearanceTransfer.Export(s);
        Expect(JsonNode.Parse(json)?["shortcuts"] is JsonArray { Count: 1 }, "the export has no keyboard shortcuts");
        s.KeyBindings = new List<string>();
        var plan = AppearanceTransfer.Read(json, s, main.AllNavKeys, MainWindow.NavSections);
        Expect(plan.Changes.Any(c => c.Name == "Keyboard shortcuts"), "the import doesn't carry the shortcuts");
        var tampered = JsonNode.Parse(json)!.AsObject();
        tampered["shortcuts"] = new JsonArray("pane.tile=Ctrl+C", "x=y", "pane.merge=Ctrl+Alt+Shift+U");
        var p2 = AppearanceTransfer.Read(tampered.ToJsonString(), s, main.AllNavKeys, MainWindow.NavSections);
        Expect(p2.Skipped.Count(x => x.StartsWith("shortcuts:", StringComparison.Ordinal)) >= 2 && p2.Changes.FirstOrDefault(c => c.Name == "Keyboard shortcuts")?.To == "pane.merge=Ctrl+Alt+Shift+U", "tampered shortcut lines: " + AppearanceTransfer.Describe(p2));
        tampered["shortcuts"] = new JsonArray(Enumerable.Range(0, KeyBindingList.MaxEntries + 1).Select(i => (JsonNode)$"x.n{i}=F1").ToArray());
        Expect(AppearanceTransfer.Read(tampered.ToJsonString(), s).Skipped.Any(x => x.Contains("not a list of shortcut lines")), "an oversized shortcut list was read");
    }

    private static void Window(MainWindow main)
    {
        Expect(MainWindow.PageCommandId("settings") == "go.settings" && MainWindow.PageCommandId("Cloud:Vultr") == "go.cloud-vultr", "page command ids");
        main.Navigate("settings");
        main.Navigate("hosts");
        var tabs = main.Panes.ActivePane.Items.OfType<TabItem>().ToList();
        if (tabs.Count >= 2)
        {
            main.SelectTab(int.MaxValue);
            Expect(main.Panes.ActiveTab == tabs[^1], "Go to the last tab");
            main.SelectTab(1);
            Expect(main.Panes.ActiveTab == tabs[0], "Go to tab 1");
            main.MoveActiveTab(+1);
            Expect(main.Panes.ActivePane.Items.IndexOf(tabs[0]) == 1, "Move the tab right");
            main.MoveActiveTab(-1);
            Expect(main.Panes.ActivePane.Items.IndexOf(tabs[0]) == 0, "Move the tab left");
        }
        int before;
        lock (Menus.ShownForTests) before = Menus.ShownForTests.Count;
        main.OpenTabMenu();
        lock (Menus.ShownForTests) Expect(Menus.ShownForTests.Count == before + 1 && Menus.ShownForTests[^1].Any(h => h == "Close" || h.StartsWith("Rename", StringComparison.Ordinal)), "the tab's menu didn't open");
        main.FocusSidebar();
        main.FocusActiveTab();
        main.ShowKeyboardSettings();
        Expect(SettingsView.Current?.ShownCategory == SettingsView.KeyboardCategory, "Keyboard shortcuts didn't open Settings → Keyboard");
    }

    /// <summary>The changed shortcuts are in the encrypted settings file, never in plain text.</summary>
    private static void Encrypted()
    {
        var s = AppSettings.Current;
        s.KeyBindings = new List<string> { "pane.tile=Ctrl+Alt+Shift+T" };
        AppSettings.Save();
        var file = Path.Combine(SecureStore.DataDir, "settings.dcx");
        Expect(File.Exists(file), "no settings file at " + file);
        var bytes = File.ReadAllBytes(file);
        foreach (var probe in new[] { "pane.tile", "Ctrl+Alt+Shift+T", "KeyBindings" })
        {
            Expect(!Contains(bytes, System.Text.Encoding.UTF8.GetBytes(probe)) && !Contains(bytes, System.Text.Encoding.Unicode.GetBytes(probe)), $"“{probe}” is readable in the settings file");
        }
        s.KeyBindings = new List<string>();
        AppSettings.Save();
    }

    private static bool Contains(byte[] hay, byte[] needle)
    {
        for (int i = 0; i <= hay.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }
}
