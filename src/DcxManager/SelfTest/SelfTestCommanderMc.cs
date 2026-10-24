using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text;
using DcxManager.Files;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.3.0 §15: the File commander's Midnight Commander tools. The pure parts (shell patterns, user-menu macros and their
/// quoting, the remote find / du / chown / ln commands, the user-menu text format, archives with unsafe names) and a real
/// local pane off screen in a scratch folder: + / − / * marking, folder sizes, Find file into the pane, entering a .zip and
/// copying out of it (unsafe names never written), leaving it, new file, hard link, history, hotlist, quick view, the F9
/// menu bar. No window is shown.
/// </summary>
public static class CommanderMcTests
{
    public static async Task RunAsync(Action<string> ok, Action<string, Exception?> fail)
    {
        // ---- pure logic --------------------------------------------------------------------------------------------
        try
        {
            var rx = McTools.Glob("*.log;*.[ch]")!;
            if (!rx.IsMatch("app.LOG") || !rx.IsMatch("main.c") || rx.IsMatch("main.cpp") || McTools.Glob("  ") != null) throw new Exception("glob");
            var cur = new FsItem { Name = "it's here.txt", FullPath = "/srv/it's here.txt" };
            var cmd = McTools.Expand("wc -l %f %s && cd %D; echo 100%%", "/srv", cur, [], "/tmp", null, out _);
            if (cmd != "wc -l 'it'\\''s here.txt' 'it'\\''s here.txt' && cd '/tmp'; echo 100%") throw new Exception("expand: " + cmd);
            if (McTools.Expand("cat %f", "/srv", new FsItem { Name = "a\nrm -rf /", FullPath = "/x" }, [], "/", null, out var why) != null || why == null) throw new Exception("a line break in a name wasn't refused");
            if (McTools.Expand("diff %f %F", "/srv", cur, [], "/", null, out _) != null) throw new Exception("%F without a file");
            var find = McTools.FindCommand("/var/log", "*.log", "error; rm -rf /");
            if (!find.Contains("-iname '*.log'") || !find.Contains("-- 'error; rm -rf /'") || !find.StartsWith("find '/var/log'")) throw new Exception("find: " + find);
            if (McTools.ParseDu("4096\t/srv/a\n123\t/srv/b c\n").GetValueOrDefault("/srv/b c") != 123) throw new Exception("du parse");
            if (!McTools.ValidOwner("www-data:www-data") || !McTools.ValidOwner(":wheel") || !McTools.ValidOwner("1000:1000") || McTools.ValidOwner("root;id") || McTools.ValidOwner(":")) throw new Exception("owner check");
            try { McTools.ChownCommand("x y", ["/a"], false); throw new Exception("bad owner accepted"); } catch (ArgumentException) { }
            if (McTools.LinkCommand("/a b", "/c", true) != "ln -s -- '/a b' '/c'") throw new Exception("ln");
            var menu = McTools.ParseUserMenu(McTools.FormatUserMenu(UserMenuEntry.Defaults()), out var errs);
            if (errs.Count > 0 || menu.Count != UserMenuEntry.Defaults().Count || menu[0].For != "file") throw new Exception("user menu round trip: " + string.Join(" | ", errs));
            McTools.ParseUserMenu("echo orphan\n# Good [any]\n# Broken title\n", out var errs2);   // orphan command, bad title, entry without command
            if (errs2.Count != 3) throw new Exception("user menu errors: " + string.Join(" | ", errs2));
            ok("File commander tools: shell patterns (*.log;*.[ch]); user-menu macros shell-quote every name (it's → 'it'\\''s') and refuse names with line breaks; find / du / chown / ln built from quoted values with --; owner specs checked; the user menu's text format round-trips and reports mistakes");
        }
        catch (Exception ex) { fail("File commander tools", ex); }

        try
        {
            var zip = Zip(("a/b.txt", "hello"), ("../evil.txt", "nope"), ("c.bin", "\0\x01"));
            var z = new ArchiveReader(zip, "zip");
            if (ArchiveReader.KindOf("x.TAR.GZ") != "tgz" || ArchiveReader.KindOf("x.txt") != null) throw new Exception("kind");
            if (!z.Entries.Any(e => e.Path == "a" && e.IsDir) || z.Entries.Single(e => e.Path == "evil.txt").Unsafe != true) throw new Exception("entries: " + string.Join(",", z.Entries.Select(e => e.Path)));
            if (Encoding.UTF8.GetString(z.Read("a/b.txt")) != "hello") throw new Exception("read");
            try { z.Read("evil.txt"); throw new Exception("an unsafe entry was readable"); } catch (InvalidDataException) { }
            if (z.Children("").Any(e => e.Path.Contains(".."))) throw new Exception("'..' shown");
            // A crafted zip with the same shown name twice: the unsafe entry's bytes must never come back for the safe name.
            var twin = new ArchiveReader(Zip(("../a/b.txt", "EVIL"), ("a/b.txt", "good")), "zip");
            if (Encoding.UTF8.GetString(twin.Read("a/b.txt")) != "good") throw new Exception("an unsafe twin entry was read for a safe name");
            var tgz = Tgz(("etc/app.conf", "port=1"), ("/abs/x", "y"));
            var t = new ArchiveReader(tgz, "tgz");
            if (Encoding.UTF8.GetString(t.Read("etc/app.conf")) != "port=1" || !t.Entries.Single(e => e.Path == "abs/x").Unsafe) throw new Exception("tgz");
            ok("archives: zip and tar.gz listed with implied folders; entries named ../ or /absolute are marked unsafe and can't be read or extracted (zip-slip); contents read back exactly");
        }
        catch (Exception ex) { fail("File commander archives", ex); }

        // ---- a real local pane ---------------------------------------------------------------------------------------
        var dir = Path.Combine(Path.GetTempPath(), "dcx-selftest-mc-" + Guid.NewGuid().ToString("N")[..8]);
        var s = AppSettings.Current;
        var (wasL, wasR) = (s.CommanderLeft, s.CommanderRight);
        var wasRestore = s.CommanderRestoreLast;
        s.CommanderRestoreLast = true;   // these checks open Commander on two prepared folders
        var hotWas = AppData.Current.CommanderHotlist.ToList();
        CommanderView? view = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            Directory.CreateDirectory(Path.Combine(dir, "out"));
            File.WriteAllText(Path.Combine(dir, "one.log"), "1");
            File.WriteAllText(Path.Combine(dir, "two.log"), "22");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "quick view text");
            File.WriteAllText(Path.Combine(dir, "sub", "inner.log"), "needle");
            File.WriteAllBytes(Path.Combine(dir, "pack.zip"), Zip(("a/b.txt", "hello"), ("../evil.txt", "nope")));
            s.CommanderLeft = "local|" + dir; s.CommanderRight = "local|" + Path.Combine(dir, "out");
            view = new CommanderView();
            var left = view.LeftForTest; var right = view.RightForTest;
            await left.GoAsync(dir, record: false);
            await right.GoAsync(Path.Combine(dir, "out"), record: false);

            if (left.SelectGroup("*.log", true) != 2 || left.Marked.Count != 2) throw new Exception("+ *.log: " + left.Marked.Count);
            left.Invert();
            if (string.Join(",", left.Marked.Select(i => i.Name).OrderBy(n => n)) != "notes.txt,pack.zip") throw new Exception("* invert: " + string.Join(",", left.Marked.Select(i => i.Name)));
            left.SelectGroup("*", false);
            if (left.Marked.Count != 0) throw new Exception("− *");

            await left.MeasureAsync(left.ListingForTest.Where(i => i.Name == "sub"));
            if (left.ListingForTest.Single(i => i.Name == "sub").DirSize != 6) throw new Exception("folder size");

            await left.FindAsync("*.log", "NEEDLE");
            if (left.Panel is not { Count: 1 } p || p[0].Name != "inner.log" || p[0].Display != Path.Combine("sub", "inner.log")) throw new Exception("find: " + string.Join(",", left.Panel?.Select(i => i.Shown) ?? []));
            await left.GoAsync(dir, record: false);
            if (left.Panel != null) throw new Exception("find results not left");

            left.SelectForTest("pack.zip");
            await left.EnterArchiveAsync(left.ListingForTest.Single(i => i.Name == "pack.zip"));
            if (!left.InArchive || !left.ListingForTest.Any(i => i.Name == "a" && i.IsDir) || left.ListingForTest.Single(i => i.Name == "evil.txt").Perms != "⚠ unsafe name") throw new Exception("archive listing: " + string.Join(",", left.ListingForTest.Select(i => i.Name)));
            var n = await left.ExtractAsync(right, left.ListingForTest.Where(i => !i.IsParent).ToList(), ask: false);
            if (n != 1 || File.ReadAllText(Path.Combine(dir, "out", "a", "b.txt")) != "hello" || File.Exists(Path.Combine(dir, "evil.txt")) || File.Exists(Path.Combine(dir, "out", "evil.txt")))
                throw new Exception("copy out of the archive: " + n);
            left.SelectForTest("..");
            await left.GoAsync(dir, record: false);
            if (left.InArchive) throw new Exception("archive not left");

            await left.NewFileAsync("new.txt", open: false);
            if (!File.Exists(Path.Combine(dir, "new.txt"))) throw new Exception("new file");
            McTools.LinkLocal(Path.Combine(dir, "notes.txt"), Path.Combine(dir, "out", "notes-link.txt"), symbolic: false);
            if (File.ReadAllText(Path.Combine(dir, "out", "notes-link.txt")) != "quick view text") throw new Exception("hard link");

            if (left.History.FirstOrDefault() != dir) throw new Exception("history: " + string.Join(",", left.History));
            left.AddToHotlist("selftest-mc");
            if (!AppData.Current.CommanderHotlist.Any(h => h.Name == "selftest-mc" && h.Place == "local" && h.Path == dir)) throw new Exception("hotlist");

            var preview = await left.PreviewAsync(left.ListingForTest.Single(i => i.Name == "notes.txt"));
            var zipPreview = await left.PreviewAsync(left.ListingForTest.Single(i => i.Name == "pack.zip"));
            if (preview != "quick view text" || !zipPreview.Contains("a/b.txt") || !zipPreview.Contains("⚠ evil.txt")) throw new Exception("quick view: " + preview + " | " + zipPreview);

            // 0.3.0 §15 review gaps: brief listing mode and MC's quick search (jump, not filter).
            left.SetBrief(true);
            if (!left.Brief || left.ListingForTest.Count == 0) throw new Exception("brief mode lost the listing");
            left.SetBrief(false);
            if (left.Brief) throw new Exception("full mode");
            left.StartQuickSearch();
            if (!left.QuickSearching || !left.QuickSearchKey("t") || !left.QuickSearchKey("w") || left.Current?.Name != "two.log") throw new Exception("quick search: " + left.Current?.Name);
            if (left.QuickSearchTo("zzz")) throw new Exception("quick search found nothing yet said so");
            left.EndQuickSearch();
            if (left.QuickSearching || left.ListingForTest.Count < 5) throw new Exception("quick search didn't end cleanly or filtered the folder");

            int shown; lock (Menus.ShownForTests) shown = Menus.ShownForTests.Count;
            view.OpenMenuBar();
            string[]? bar; lock (Menus.ShownForTests) bar = Menus.ShownForTests.Count == shown + 1 ? Menus.ShownForTests[^1] : null;
            if (bar == null || !bar.Contains("View") || !bar.Contains("Permissions & links") || bar.Length > Menus.GroupThreshold) throw new Exception("F9 menu bar: " + string.Join(" | ", bar ?? []));
            ok("File commander (Midnight Commander tools, real local pane off screen): + *.log marks 2, * inverts files, − * clears; Ctrl+Space measures a folder; Find file (name + text) fills the pane and leaving it restores the folder; a .zip opens as a folder, copying out writes only the safe entry and the ../ entry lands nowhere; new file, hard link, directory history, hotlist (encrypted store), quick view of text and of an archive; F9 opens the menu bar");
        }
        catch (Exception ex) { fail("File commander (Midnight Commander tools)", ex); }
        finally
        {
            view?.Dispose();
            s.CommanderLeft = wasL; s.CommanderRight = wasR; s.CommanderRestoreLast = wasRestore;
            AppData.Current.CommanderHotlist = hotWas; AppData.Save();
            try { AppSettings.Save(); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, text) in files)
            {
                using var w = z.CreateEntry(name).Open();
                w.Write(Encoding.UTF8.GetBytes(text));
            }
        return ms.ToArray();
    }

    private static byte[] Tgz(params (string Name, string Text)[] files)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true))
        using (var tw = new TarWriter(gz, TarEntryFormat.Pax, true))
            foreach (var (name, text) in files)
                tw.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
        return ms.ToArray();
    }
}
