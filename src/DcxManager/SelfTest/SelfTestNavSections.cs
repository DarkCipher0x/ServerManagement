using System.Windows.Controls;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// ADR-121 (0.6.43): a module's own sidebar section (IAppHost.NavSection; CipherMail's EMAIL). A throwaway module adds two
/// sections (one after the other, registered in the "wrong" order), one without items, and refused ones; the sidebar shows
/// them after the section each named, with their glyph, like any built-in section (hide, reorder); they are remembered and
/// keep their saved order and hidden state while the module isn't loaded; and they disappear with their items. No window,
/// no dialog: the main window's sidebar is rebuilt in place and every setting is put back.
/// </summary>
public static class NavSectionTests
{
    private const string ModuleId = "selftest-navsections";
    private const string Mail = "SELFTEST MAIL", Late = "SELFTEST LATE", Empty = "SELFTEST EMPTY";

    private sealed class TestModule : IAppModule
    {
        public string Id => ModuleId;
        public void Register(IAppHost host)
        {
            host.NavSection(Late, "✦", after: Mail);      // follows a module section registered after it
            host.NavSection(Mail, "✉", after: "VAULT");
            host.NavSection(Empty, "□");                   // no items: never shown
            host.NavSection("LOGS", "x");                  // a built-in name: refused
            host.NavSection(Mail, "y");                    // twice: refused
            host.NavSection("lower case", "z");            // not upper case: refused
            host.Icon("⟟", '\ue715');                    // a module's own glyph as a Fluent icon
            host.Icon("⚙", '\ue716');                    // the app's own glyph: refused
            host.Icon("⟠", 'x');                          // not an icon code point: refused
            host.NavItem(Mail, "selftest-navsec-inbox", "✉  Inbox", "self-test", open: () => { });
            host.NavItem(Late, "selftest-navsec-later", "✦  Later", "self-test", open: () => { });
        }
    }

    public static async Task RunAsync(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        Sections(main, ok, fail);
        await LiveAsync(main, ok, fail);
        MinAppVersion(ok, fail);
    }

    private static void Sections(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        var s = AppSettings.Current;
        var saved = (Order: s.NavSectionOrder.ToList(), Hidden: s.HiddenNavSections.ToList(), Expanded: s.ExpandedNavSections.ToList(), Addon: s.AddonNavSections.ToList());
        var nav = (StackPanel)main.FindName("Nav");
        List<string> Heads() => nav.Children.OfType<TextBlock>().Select(t => t.Tag as string ?? "").Where(t => t.Length > 0).ToList();
        try
        {
            foreach (var (key, glyph) in new[] { ("CHAT", "x"), ("PINNED", "x"), ("Email", "x"), ("E", ""), ("TOO LONG FOR A SIDEBAR HEADER", "x"), ("E\nMAIL", "x") })
                if (AppModules.NavSectionProblem(key, glyph) == null) throw new Exception($"section key \"{key}\" accepted");
            // A key no shipping add-on takes (CipherMail owns EMAIL when it is installed, so EMAIL itself would be a duplicate).
            if (AppModules.NavSectionProblem("SELFTEST OK & 2-X", "✉") is { } why) throw new Exception("a valid key refused: " + why);

            AppModules.Add(new TestModule());
            var mine = AppModules.NavSectionItems.Where(c => c.Module == ModuleId).Select(c => c.Key).ToList();
            if (!mine.SequenceEqual(new[] { Late, Mail, Empty })) throw new Exception("registered: " + string.Join(", ", mine));
            if (AppModules.FindNavSection(Mail)?.Glyph != "✉") throw new Exception("the duplicate replaced the first registration");
            var order = MainWindow.NavSections.ToList();
            int vault = order.IndexOf("VAULT"), mail = order.IndexOf(Mail), late = order.IndexOf(Late);
            if (vault < 0 || mail != vault + 1 || late != mail + 1 || order.Contains(Empty) || order.Count(n => n == "LOGS") != 1)
                throw new Exception("default order: " + string.Join(",", order));
            if (MainWindow.SectionGlyph(Mail) != "✉" || MainWindow.SectionGlyph(Late) != "✦") throw new Exception("glyphs");
            if (Icons.For("⟟") != '\ue715' || Icons.For("⚙") != '\ue713' || Icons.For("⟠") != null || Icons.For("✉") != '\ue715') throw new Exception("module icons");

            // Drawn like a built-in: a header and its item, then hidden and moved like one; remembered.
            s.NavSectionOrder = new(); s.HiddenNavSections.RemoveAll(n => n is Mail or Late);
            if (MainWindow.MigrateNavSettings(s) && !(s.AddonNavSections.Contains(Mail) && s.AddonNavSections.Contains(Late))) throw new Exception("not remembered");
            // Every registered section is remembered (an empty one too: it may get items later).
            if (!new[] { Mail, Late, Empty }.All(s.AddonNavSections.Contains)) throw new Exception("remembered: " + string.Join(",", s.AddonNavSections));
            main.BuildNav();
            if (!Heads().Contains(Mail) || main.NavSectionsForTest.GetValueOrDefault(Mail)?.SequenceEqual(new[] { "selftest-navsec-inbox" }) != true) throw new Exception("sidebar: " + string.Join(",", Heads()));
            main.SetNavSectionHidden(Mail, true);
            if (Heads().Contains(Mail) || !s.HiddenNavSections.Contains(Mail)) throw new Exception("not hidden");
            main.SetNavSectionHidden(Mail, false);
            if (!Heads().Contains(Mail)) throw new Exception("not shown again");
            main.MoveNavSection(Mail, -1);
            if (main.NavOrder.IndexOf(Mail) != main.NavOrder.IndexOf("VAULT") - 1) throw new Exception("not moved: " + string.Join(",", main.NavOrder));
            var heads = Heads();
            if (heads.IndexOf(Mail) != heads.IndexOf("VAULT") - 1) throw new Exception("sidebar order: " + string.Join(",", heads));
            main.SetNavSectionHidden(Late, true);

            // The module gone (disabled, refused, not registered yet): its sections leave the sidebar, their saved state stays.
            AppModules.RemoveModuleForTest(ModuleId);
            if (MainWindow.NavSections.Contains(Mail) || MainWindow.NavSections.Contains(Late)) throw new Exception("sections without items still exist");
            MainWindow.MigrateNavSettings(s);
            if (!s.NavSectionOrder.Contains(Mail) || !s.HiddenNavSections.Contains(Late)) throw new Exception("saved state tidied away while the module wasn't loaded: order " + string.Join(",", s.NavSectionOrder) + "; hidden " + string.Join(",", s.HiddenNavSections));
            main.MoveNavSection("SYSTEM", -1);   // a reorder while it isn't loaded keeps its place
            int m = s.NavSectionOrder.IndexOf(Mail);
            if (m < 0 || s.NavSectionOrder.ElementAtOrDefault(m + 1) != "VAULT") throw new Exception("place lost in a reorder: " + string.Join(",", s.NavSectionOrder));
            if (Heads().Contains(Mail) || Heads().Contains(Late)) throw new Exception("sidebar still shows them: " + string.Join(",", Heads()));
            ok("ADR-121 sidebar sections of a module's own: refused keys (built-in, PINNED, lower case, too long, duplicate); placed after the section each named (a module section waiting for another), with its glyph, only while it has items; hidden, shown and moved like a built-in; a module's glyph mapped to a Fluent icon (the app's own and non-icon code points refused); remembered, with order and hidden state kept while the module isn't loaded");
        }
        catch (Exception ex) { fail("ADR-121 sidebar sections of a module's own", ex); }
        finally
        {
            AppModules.RemoveModuleForTest(ModuleId);
            Icons.ResetModulesForTest();
            s.NavSectionOrder = saved.Order; s.HiddenNavSections = saved.Hidden; s.ExpandedNavSections = saved.Expanded; s.AddonNavSections = saved.Addon;
            try { AppSettings.Save(); } catch { }
            main.BuildNav();
        }
    }

    // ---- ADR-121a: live items (IAppHost.NavItems / RefreshNav) ------------------------------------------------------

    private const string LiveId = "selftest-navlive", Live = "SELFTEST LIVE";
    private static readonly List<ModuleNavEntry> LiveList = new();
    private static int _opened;

    private sealed class LiveModule : IAppModule
    {
        public string Id => LiveId;
        public static IAppHost? Host;
        public void Register(IAppHost host)
        {
            Host = host;
            host.NavSection(Live, "✉", after: "VAULT");
            host.NavItems(Live, () => LiveList.ToList());
            host.NavItems(Live, () => throw new InvalidOperationException("a provider that fails"));   // logged, skipped
            host.NavItems(Live, () => new[]
            {
                new ModuleNavEntry("settings", "⚙  Not the app's settings", null, Open: () => { }),     // an app page's key: skipped
                new ModuleNavEntry("selftest-live-none", "✉  Nothing to open", null),                   // nothing to open: skipped
            }.Concat(LiveList.Take(1).Select(f => new ModuleNavEntry(f.Key, "✉  Twice", null, Open: () => { }))));   // the first list's key again: skipped
        }
    }

    private static async Task LiveAsync(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        var s = AppSettings.Current;
        var saved = (Order: s.NavSectionOrder.ToList(), Hidden: s.HiddenNavSections.ToList(), Addon: s.AddonNavSections.ToList(), Pins: s.PinnedNavItems.ToList());
        var nav = (StackPanel)main.FindName("Nav");
        async Task Idle() => await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        try
        {
            LiveList.Clear(); _opened = 0;
            LiveList.Add(new ModuleNavEntry("selftest-live-a", "✉  demo@example.com", "Account A · unlocked", Open: () => _opened++, Badge: 3));
            AppModules.Add(new LiveModule());
            main.BuildNav();
            var items = main.NavSectionsForTest.GetValueOrDefault(Live);
            if (items == null || !items.SequenceEqual(new[] { "selftest-live-a" })) throw new Exception("a section of live items only: " + string.Join(",", items ?? new()));
            if (!MainWindow.NavSections.Contains(Live)) throw new Exception("the live section isn't a section");
            var badge = Descendants(nav).OfType<TextBlock>().FirstOrDefault(t => t.Tag as string == "nav-badge");
            if (badge?.Text != "3") throw new Exception("badge: " + badge?.Text);
            main.Navigate("selftest-live-a");
            if (_opened != 1) throw new Exception("a live item didn't open through its key");

            // The list changes at run time: RefreshNav from another thread rebuilds once, on the UI thread.
            LiveList.Clear();
            LiveList.Add(new ModuleNavEntry("selftest-live-b", "✉  ops@example.org", "Account B · locked", Open: () => _opened++, Badge: 150));
            LiveList.Add(new ModuleNavEntry("selftest-live-c", "✉  All inboxes", null, Open: () => _opened++));
            await Task.Run(() => { LiveModule.Host!.RefreshNav(); LiveModule.Host!.RefreshNav(); });
            await Idle();
            items = main.NavSectionsForTest.GetValueOrDefault(Live);
            if (items == null || !items.SequenceEqual(new[] { "selftest-live-b", "selftest-live-c" })) throw new Exception("after RefreshNav: " + string.Join(",", items ?? new()));
            if (Descendants(nav).OfType<TextBlock>().FirstOrDefault(t => t.Tag as string == "nav-badge")?.Text != "99+") throw new Exception("a count above 99");
            if (AppModules.FindNav("selftest-live-a") != null) throw new Exception("a removed live item still opens");

            // 0.6.45: only the counts changed: redrawn in place, the sidebar isn't rebuilt (a rebuild while a WebView2 shows
            // blacked out the document area in CipherMail).
            RadioButton Item(string key) => Descendants(nav).OfType<RadioButton>().First(r => r.Tag as string == key);
            string? Badge(string key) => Descendants(Item(key)).OfType<TextBlock>().FirstOrDefault(t => t.Tag as string == "nav-badge")?.Text;
            var itemB = Item("selftest-live-b"); var itemC = Item("selftest-live-c"); var firstChild = nav.Children[0];
            LiveList[0] = LiveList[0] with { Badge = 7 };
            LiveList[1] = LiveList[1] with { Badge = 2 };
            await Task.Run(() => LiveModule.Host!.RefreshNav());
            await Idle();
            if (!ReferenceEquals(Item("selftest-live-b"), itemB) || !ReferenceEquals(Item("selftest-live-c"), itemC) || !ReferenceEquals(nav.Children[0], firstChild))
                throw new Exception("a count-only change rebuilt the sidebar");
            if (Badge("selftest-live-b") != "7" || Badge("selftest-live-c") != "2") throw new Exception($"counts in place: {Badge("selftest-live-b")}, {Badge("selftest-live-c")}");
            LiveList[0] = LiveList[0] with { Badge = 0 };
            await Task.Run(() => LiveModule.Host!.RefreshNav());
            await Idle();
            if (!ReferenceEquals(Item("selftest-live-b"), itemB) || Badge("selftest-live-b") != null) throw new Exception("a count going to 0 wasn't removed in place");
            LiveList[1] = LiveList[1] with { Text = "✉  All inboxes (renamed)" };
            await Task.Run(() => LiveModule.Host!.RefreshNav());
            await Idle();
            if (ReferenceEquals(Item("selftest-live-c"), itemC)) throw new Exception("a renamed live item didn't rebuild the sidebar");
            LiveList[0] = LiveList[0] with { Badge = 150 };

            // Pinned like any page; the list empty: the section goes away.
            main.SetNavPinned("selftest-live-c", true);
            if (!s.PinnedNavItems.Contains("selftest-live-c")) throw new Exception("a live item can't be pinned");
            LiveList.Clear();
            main.BuildNav();
            if (MainWindow.NavSections.Contains(Live) || main.NavSectionsForTest.ContainsKey(Live)) throw new Exception("an empty live section still shows");
            ok("ADR-121a live sidebar items: a section of a module's live list only; entries open through their key, with their count (99+); an app page's key, a key used twice, an entry with nothing to open and a failing provider are skipped; RefreshNav from another thread rebuilds on the UI thread; 0.6.45: counts alone are redrawn in place without a rebuild, other changes rebuild; pinned like any page; the section goes when the list is empty");
        }
        catch (Exception ex) { fail("ADR-121a live sidebar items", ex); }
        finally
        {
            AppModules.RemoveModuleForTest(LiveId);
            LiveList.Clear(); LiveModule.Host = null;
            s.NavSectionOrder = saved.Order; s.HiddenNavSections = saved.Hidden; s.AddonNavSections = saved.Addon; s.PinnedNavItems = saved.Pins;
            try { AppSettings.Save(); } catch { }
            main.BuildNav();
        }
    }

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject d)
    {
        foreach (var c in System.Windows.LogicalTreeHelper.GetChildren(d).OfType<System.Windows.DependencyObject>())
        {
            yield return c;
            foreach (var x in Descendants(c)) yield return x;
        }
    }

    // ---- 0.6.43: addon.json minAppVersion -----------------------------------------------------------------------------

    private static void MinAppVersion(Action<string> ok, Action<string, Exception?> fail)
    {
        try
        {
            if (Addons.AddonPackages.AppTooOld(null, "0.6.43") != null || Addons.AddonPackages.AppTooOld("", "0.6.43") != null) throw new Exception("no minimum refused");
            if (Addons.AddonPackages.AppTooOld("0.6.43", "0.6.43") != null || Addons.AddonPackages.AppTooOld("0.6.9", "0.6.43") != null) throw new Exception("a new enough app refused (0.6.9 < 0.6.43 numerically)");
            if (Addons.AddonPackages.AppTooOld("0.6.43", "0.6.42") is not { } old || !old.Contains("0.6.43 or newer")) throw new Exception("an older app accepted");
            if (Addons.AddonPackages.AppTooOld("0.7", "0.6.43") == null || Addons.AddonPackages.AppTooOld("v1.0.0", "0.6.43") == null) throw new Exception("a malformed minimum accepted");
            ok("0.6.43 addon.json minAppVersion: none or an older one accepted (compared as numbers), a newer one refused with \"Update the app first\", a malformed one refused");
        }
        catch (Exception ex) { fail("addon.json minAppVersion", ex); }
    }
}
