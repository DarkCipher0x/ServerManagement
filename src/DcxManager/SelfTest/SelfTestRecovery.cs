using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DcxManager.Files;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// ADR-022 crash recovery: the encrypted journal (drafts, transfers, caps, no idle writes), the editor's draft life
/// cycle and conflict check, workspace v2 (panes, pop-outs, groups, active tab) reopened disconnected, the recovery
/// screen (rendered off-screen) and its choices — and a REAL crash: a child process journals work and is killed, a
/// second one must find all of it. Nothing is ever shown on screen.
/// </summary>
public static class RecoveryTests
{
    private const string Marker = "draft-7c1e-RECOVERME";

    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        var journalPath = Path.Combine(SecureStore.DataDir, Recovery.FileName);
        var work = Path.Combine(reportDir, "recovery-files-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        // 1. The journal: encrypted, round trip, caps, deleted when empty, nothing written while idle, bursts coalesced.
        try
        {
            Recovery.ResetForTests();
            for (int i = 0; i < 40 && TransferQueue.Jobs.Any(j => !j.Finished); i++) await Task.Delay(250);   // earlier tests' jobs
            await Task.Delay(Recovery.Debounce + TimeSpan.FromMilliseconds(600));
            int w0 = Recovery.Writes;
            await Task.Delay(Recovery.Debounce + TimeSpan.FromSeconds(1));
            if (Recovery.Writes != w0) throw new Exception("the journal was written while nothing changed");

            var text = "server {\n    listen 80;\n}  # " + Marker + "\n";
            using (Recovery.TrackDraft(() => new Recovery.Draft { Title = "probe", Source = "local", Path = Path.Combine(work, "a.conf"), Text = text, OriginalHash = Recovery.Hash("old"), Updated = DateTime.Now }))
            {
                for (int i = 0; i < 5; i++) Recovery.Changed();   // a burst
                await Task.Delay(Recovery.Debounce + TimeSpan.FromMilliseconds(900));
                if (Recovery.Writes != w0 + 1) throw new Exception($"a burst of 5 changes made {Recovery.Writes - w0} writes (expected 1)");
                if (!File.Exists(journalPath)) throw new Exception("no journal file");
                if (Encoding.UTF8.GetString(File.ReadAllBytes(journalPath)).Contains(Marker)) throw new Exception("the draft is on disk in plaintext");
                var back = SecureStore.Read<Recovery.Journal>(Recovery.FileName);
                if (back.Drafts.Count != 1 || back.Drafts[0].Text != text) throw new Exception("round trip");
                StoreSchema.ParseCheck(Recovery.FileName, Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(back, SecureStore.Json)));
                // Caps: an over-size draft is left out (and named), the rest still journaled.
                using (Recovery.TrackDraft(() => new Recovery.Draft { Title = "huge", Text = new string('x', Recovery.MaxDraftChars + 1) }))
                {
                    var j = Recovery.Build();
                    if (j.Drafts.Count != 1 || !Recovery.Skipped.Contains("huge")) throw new Exception("size cap");
                }
            }
            Recovery.FlushNow();
            if (File.Exists(journalPath)) throw new Exception("an empty journal wasn't deleted");
            ok("recovery journal: drafts sealed on disk (no plaintext), round trip + format check, over-size drafts left out by name, deleted when empty; nothing written while idle, a burst of changes = one write after the debounce");
        }
        catch (Exception ex) { fail("recovery journal", ex); }

        // 2. The remote-file editor: unsaved text is journaled, Save / Discard clear it, a recovered draft checks for changes.
        Dialogs.ConfirmForTests = null;
        try
        {
            var file = Path.Combine(work, "nginx.conf");
            File.WriteAllText(file, "original\n");
            var w = TextEditorWindow.Open("This PC:" + file, "original\n", async t => await File.WriteAllTextAsync(file, t), FsRef.Local, file);
            await Settle(300);
            if (w.Left > -5000) throw new Exception("the editor isn't off-screen in the self-test");
            var ed = TextEditorWindow.Open_.Single(o => o.Window == w);
            ed.Box.Text = "edited " + Marker + "\n";
            await Task.Delay(Recovery.Debounce + TimeSpan.FromMilliseconds(900));
            var j1 = SecureStore.Read<Recovery.Journal>(Recovery.FileName);
            if (j1.Drafts.SingleOrDefault() is not { } d1 || !d1.Text.Contains(Marker) || d1.Path != file || d1.Source != "local" || d1.OriginalHash != Recovery.Hash("original\n"))
                throw new Exception("typing didn't journal the draft");
            await ed.Save();
            if (File.ReadAllText(file) != "edited " + Marker + "\n") throw new Exception("save");
            await Task.Delay(Recovery.Debounce + TimeSpan.FromMilliseconds(900));
            if (File.Exists(journalPath)) throw new Exception("a saved draft stayed in the journal");
            ed.Box.Text = "changed again\n";
            Dialogs.ConfirmForTests = true;   // "Close without saving?" → Discard
            w.Close();
            await Task.Delay(Recovery.Debounce + TimeSpan.FromMilliseconds(900));
            if (File.Exists(journalPath)) throw new Exception("a discarded draft stayed in the journal");

            // Recovered: the file changed since the draft began → asks; "no" leaves the file alone, "yes" writes it.
            var draft = new Recovery.Draft { Title = "x", Source = "local", Path = file, Text = "recovered " + Marker + "\n", OriginalHash = Recovery.Hash("something else\n"), Updated = DateTime.Now.AddMinutes(-3) };
            var rw = TextEditorWindow.OpenRecovered(draft);
            await Settle(300);
            var red = TextEditorWindow.Open_.Single(o => o.Window == rw);
            if (!rw.Title.Contains("(recovered)") || !red.Status.Text.Contains("not saved yet") || red.Box.Text != draft.Text) throw new Exception("recovered editor: " + red.Status.Text);
            await Task.Delay(Recovery.Debounce + TimeSpan.FromMilliseconds(900));
            if (SecureStore.Read<Recovery.Journal>(Recovery.FileName).Drafts.Count != 1) throw new Exception("an open recovered draft isn't journaled");
            int confirms = Dialogs.SelfTestConfirms.Count;
            Dialogs.ConfirmForTests = false;
            await red.Save();
            if (File.ReadAllText(file) != "edited " + Marker + "\n" || Dialogs.SelfTestConfirms.Count != confirms + 1 || !Dialogs.SelfTestConfirms[^1].Title.Contains("changed"))
                throw new Exception("a changed file was overwritten without asking");
            Dialogs.ConfirmForTests = true;
            await red.Save();
            if (File.ReadAllText(file) != draft.Text) throw new Exception("confirmed recovered save");
            rw.Close();
            await Task.Delay(Recovery.Debounce + TimeSpan.FromMilliseconds(900));
            if (File.Exists(journalPath)) throw new Exception("journal left after the recovered draft was saved");
            ok("editor drafts: typing journals the unsaved text (source, path, hash as loaded), Save and Discard clear it; a recovered draft opens marked \"not saved yet\", stays journaled while open, and asks before overwriting a file that changed since");
        }
        catch (Exception ex) { fail("editor drafts", ex); }
        finally { Dialogs.ConfirmForTests = null; foreach (var o in TextEditorWindow.Open_.ToList()) { try { Dialogs.ConfirmForTests = true; o.Window.Close(); } catch { } } Dialogs.ConfirmForTests = null; }

        // 3. Transfers: an unfinished job is journaled and comes back as an equivalent job; mirror deletions are not repeated.
        try
        {
            var src = Path.Combine(work, "big.bin");
            File.WriteAllBytes(src, new byte[256 * 1024]);
            var dest = Path.Combine(work, "dest");
            Directory.CreateDirectory(dest);
            var item = new FsItem { Name = "big.bin", FullPath = src, Length = 256 * 1024, Modified = File.GetLastWriteTime(src) };
            var job = new TransferJob { From = FsRef.Local, To = FsRef.Local, Items = new[] { item }, DestDir = dest, Label = "recovery probe", DeleteOnTarget = new[] { item } };
            job.Pause();   // held before its first file, so it stays unfinished
            TransferQueue.Enqueue(job);
            await Task.Delay(Recovery.Debounce + TimeSpan.FromMilliseconds(900));
            var jt = SecureStore.Read<Recovery.Journal>(Recovery.FileName).Transfers.SingleOrDefault(t => t.Label == "recovery probe")
                ?? throw new Exception("the unfinished job isn't journaled");
            if (jt.Items.Single().FullPath != src || jt.DestDir != dest || jt.DeleteCount != 1 || jt.From != "local") throw new Exception("journaled job fields");
            var again = Recovery.ToJob(jt);
            if (again.DeleteOnTarget != null || !again.Warnings.Any(x => x.Contains("not repeated")) || again.Items[0].FullPath != src || again.Label != "recovery probe") throw new Exception("rebuilt job");
            job.Cts.Cancel(); job.Resume();
            await job.Done.Task.WaitAsync(TimeSpan.FromSeconds(20));
            bool InJournal() => File.Exists(journalPath) && SecureStore.Read<Recovery.Journal>(Recovery.FileName).Transfers.Any(t => t.Label == "recovery probe");
            for (int i = 0; i < 30 && InJournal(); i++) await Task.Delay(250);
            if (InJournal())
                throw new Exception($"a finished job stayed in the journal (finished={job.Finished}, status={job.Status}, live unfinished={TransferQueue.Jobs.Count(j => !j.Finished)}, built={Recovery.Build().Transfers.Count}, writes={Recovery.Writes})");
            ok("unfinished transfers: journaled while queued/paused (both ends, items, destination), rebuilt as the same job, a mirror's deletions dropped with a warning, gone from the journal once finished");
        }
        catch (Exception ex) { fail("transfer recovery", ex); }

        // 4. Workspace v2: panes, a pop-out, a group and the active tab come back — disconnected.
        var mine = new List<TabItem>();
        try
        {
            var cmd = Sessions.LocalShells.Find("cmd") ?? throw new Exception("cmd not discovered");
            for (int i = 0; i < 3; i++)
            {
                var before = main.Panes.AllTabs.ToHashSet();
                main.OpenLocal(cmd, connectOnOpen: false);
                mine.Add(main.Panes.AllTabs.Single(t => !before.Contains(t)));
            }
            foreach (var t in mine) TabGroups.Assign(t, "rtest");
            if (!main.Panes.Split(mine[1], Orientation.Horizontal)) throw new Exception("split");
            main.Panes.PopOut(mine[2]);
            await Settle(300);
            main.Panes.Select(mine[1]);
            await Settle(200);
            var entries = main.WorkspaceEntries().Where(e => e.Group == "rtest").ToList();
            if (entries.Count != 3) throw new Exception($"{entries.Count} entries for the test tabs");
            if (entries.Count(e => e.Floating) != 1 || entries.Where(e => !e.Floating).Select(e => e.Pane).Distinct().Count() != 2 || entries.Count(e => e.Active) != 1)
                throw new Exception("layout not recorded: " + string.Join(", ", entries.Select(e => $"{e.Pane}/{e.Floating}/{e.Active}")));
            main.SaveWorkspaceNow();
            var saved = Workspace.Load().Where(e => e.Group == "rtest").ToList();
            if (saved.Count != 3 || saved.Count(e => e.Floating) != 1) throw new Exception("workspace file doesn't carry the layout");
            foreach (var t in mine) main.CloseTab(t);
            mine.Clear();
            await Settle(300);

            main.RestoreTabs(MainWindow.Reopenable(saved));
            await Settle(500);
            mine.AddRange(main.Panes.AllTabs.Where(t => TabGroups.GetName(t) == "rtest"));
            if (mine.Count != 3) throw new Exception($"{mine.Count} tabs reopened");
            if (mine.Count(main.Panes.IsFloating) != 1) throw new Exception("pop-out not restored");
            if (mine.Where(t => !main.Panes.IsFloating(t)).Select(t => main.Panes.PaneOf(t)).Distinct().Count() != 2) throw new Exception("panes not restored");
            if (mine.Any(t => t.Content is TerminalView { IsConnected: true })) throw new Exception("a restored terminal connected by itself");
            var act = main.Panes.ActiveTab;
            if (act == null || !mine.Contains(act) || main.Panes.IsFloating(act)) throw new Exception("active tab not restored");
            ok("workspace v2: tabs come back in their panes, pop-out window and tab group, the active tab selected, every terminal disconnected (never auto-connected); old entries without layout still read");
        }
        catch (Exception ex) { fail("workspace layout recovery", ex); }
        finally { foreach (var t in mine) { try { main.CloseTab(t); } catch { } } }

        // 5. The recovery screen: rendered off-screen; ticked items come back, the rest is discarded.
        try
        {
            var file = Path.Combine(work, "hosts.txt");
            File.WriteAllText(file, "198.51.100.10 lab\n");
            var src = Path.Combine(work, "small.bin");
            File.WriteAllBytes(src, new byte[4096]);
            var dest = Path.Combine(work, "dest2");
            Directory.CreateDirectory(dest);
            var keep = new Recovery.Draft { Source = "local", Path = file, Text = "198.51.100.10 lab\n198.51.100.11 lab2 # " + Marker + "\n", OriginalHash = Recovery.Hash("198.51.100.10 lab\n"), Updated = DateTime.Now.AddMinutes(-5) };
            var drop = new Recovery.Draft { Source = "host:no-such-host", Path = "/etc/motd", Text = "gone\n", Updated = DateTime.Now.AddMinutes(-9) };
            var xfer = new Recovery.Transfer { Title = "Copy small.bin", From = "local", To = "local", DestDir = dest, Status = "copying", Started = DateTime.Now.AddMinutes(-6),
                Items = { new Recovery.ItemRef("small.bin", src, false, 4096, File.GetLastWriteTime(src), 0) } };
            var dead = new Recovery.Transfer { Title = "Copy x", From = "host:no-such-host", To = "local", DestDir = dest, Status = "copying" };
            var journal = new Recovery.Journal { Drafts = { keep, drop }, Transfers = { xfer, dead } };
            var cmd = Sessions.LocalShells.Find("cmd")!;
            var tabs = new List<Workspace.Entry> { new("local", cmd.Id) };

            var built = RecoveryWindow.Build(unclean: true, tabs, journal);
            Render(built.Content, Path.Combine(reportDir, "shots", "recovery.png"));
            var shownText = string.Join("\n", Texts(built.Content));
            if (!shownText.Contains("didn't close cleanly") || !shownText.Contains("no longer exists") || !shownText.Contains("disconnected")) throw new Exception("screen text");
            if (built.Drafts.Count != 2 || built.Transfers.Count != 2 || built.Transfers.Single(t => t.Transfer == dead).Box.IsEnabled) throw new Exception("a transfer to a missing host is offered");
            if (built.TabsBox?.IsChecked != true) throw new Exception("tabs box");

            built.TabsBox.IsChecked = false;
            built.Drafts.Single(d => d.Draft == drop).Box.IsChecked = false;
            int cmdTabs() => main.Panes.AllTabs.Count(t => t.Content is TerminalView { Target: Sessions.LocalTarget lt } && lt.Profile.Id == cmd.Id);
            int cmdBefore = cmdTabs();
            var status = RecoveryWindow.Apply(main, tabs, journal, built.Read());
            await Settle(300);
            if (!TextEditorWindow.Open_.Any(o => o.Box.Text == keep.Text)) throw new Exception("the ticked draft wasn't reopened");
            var ed = TextEditorWindow.Open_.Single(o => o.Box.Text == keep.Text);
            if (TextEditorWindow.Open_.Any(o => o.Box.Text == drop.Text)) throw new Exception("an unticked draft was reopened");
            var job = TransferQueue.Jobs.FirstOrDefault(j => j.Title == "Copy small.bin" && j.DestDir == dest) ?? throw new Exception("the ticked transfer wasn't queued");
            await job.Done.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (job.Error != null || !File.Exists(Path.Combine(dest, "small.bin"))) throw new Exception("recovered transfer: " + job.Error);
            if (cmdTabs() != cmdBefore) throw new Exception("unticked tabs were reopened");
            if (File.ReadAllText(file) != "198.51.100.10 lab\n") throw new Exception("a recovered draft was written before Save");
            if (!status.Contains("1 unsaved edit") || !status.Contains("1 transfer") || !status.Contains("discarded")) throw new Exception("status: " + status);
            Dialogs.ConfirmForTests = true; ed.Window.Close(); Dialogs.ConfirmForTests = null;
            ok($"recovery screen: rendered (shots\\recovery.png), lists tabs / edits / transfers with what each does, a missing host's transfer can't be ticked; only ticked items come back (the draft reopened unsaved, the transfer resumed and finished), the rest discarded — \"{status}\"");
        }
        catch (Exception ex) { fail("recovery screen", ex); }
        finally { Dialogs.ConfirmForTests = null; }

        // 6. A REAL crash: a child process journals a draft, a paused transfer and its tabs, and is killed; the next run finds them.
        var store = Path.Combine(reportDir, "crash-store-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var exe = Environment.ProcessPath ?? throw new Exception("no process path");
            Directory.CreateDirectory(store);
            var outFile = Path.Combine(work, "probe.txt");
            using (var p = Start(exe, store, "write", outFile))
            {
                for (int i = 0; i < 240 && !(File.Exists(outFile) && ReadOut(outFile).ContainsKey("READY")); i++) await Task.Delay(250);
                if (!ReadOut(outFile).ContainsKey("READY")) { try { p.Kill(true); } catch { } throw new Exception("the writer never got ready"); }
                p.Kill(true);   // a hard kill: no exit code runs, like a crash or power loss
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            }
            if (!File.Exists(Path.Combine(store, "running.marker"))) throw new Exception("no crash marker after the kill");
            var r1 = await ProbeAsync(exe, store, "read", outFile);
            if (r1.GetValueOrDefault("unclean") != "True") throw new Exception("the kill wasn't seen as an unclean exit");
            if (r1.GetValueOrDefault("drafts") != "1" || r1.GetValueOrDefault("draftok") != "True") throw new Exception("draft lost in the crash: " + Dump(r1));
            if (r1.GetValueOrDefault("transfers") != "1" || r1.GetValueOrDefault("transferlabel") != "crash probe") throw new Exception("transfer lost in the crash: " + Dump(r1));
            if (r1.GetValueOrDefault("workspace") != "1;1;True;ops") throw new Exception("workspace: " + r1.GetValueOrDefault("workspace"));
            if (r1.GetValueOrDefault("plain") != "False") throw new Exception("journal in plaintext");
            // "Later": a clean run that settles nothing still has it, and isn't an unclean exit.
            var r2 = await ProbeAsync(exe, store, "read", outFile);
            if (r2.GetValueOrDefault("unclean") != "False" || r2.GetValueOrDefault("drafts") != "1") throw new Exception("kept items: " + Dump(r2));
            var r3 = await ProbeAsync(exe, store, "settle", outFile);
            if (r3.GetValueOrDefault("journal") != "gone") throw new Exception("settle: " + Dump(r3));
            ok("real crash: a child process journaled an unsaved draft, a paused transfer and its tabs (pane, active, group) and was hard-killed; the next run saw the unclean exit and recovered all three from the encrypted journal; kept until settled (\"Later\" survives a clean run), gone once settled");
        }
        catch (Exception ex) { fail("real crash recovery", ex); }
        finally
        {
            foreach (var d in new[] { store, store + "-logs", store + "-webview2", work }) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
        }
    }

    // ---- the child process (App.OnStartup, "--selftest-crash <mode> <out>", scratch store only) -----------------------

    /// <summary>Runs in the probe process before any window. True = exit now; false = keep running (to be killed).</summary>
    public static bool Probe(string mode, string outFile)
    {
        var sb = new StringBuilder();
        try
        {
            if (mode == "write")
            {
                var text = "listen 443 ssl; # " + Marker + "\n";
                Recovery.TrackDraft(() => new Recovery.Draft { Title = "probe", Source = "local", Path = @"C:\probe\site.conf", Text = text, OriginalHash = Recovery.Hash("x"), Updated = DateTime.Now });
                var dir = Path.GetDirectoryName(outFile)!;
                var src = Path.Combine(dir, "crash-src.bin");
                File.WriteAllBytes(src, new byte[64 * 1024]);
                var dest = Directory.CreateDirectory(Path.Combine(dir, "crash-dest")).FullName;
                var job = new TransferJob { From = FsRef.Local, To = FsRef.Local, Items = new[] { new FsItem { Name = "crash-src.bin", FullPath = src, Length = 64 * 1024 } }, DestDir = dest, Label = "crash probe" };
                job.Pause();
                TransferQueue.Enqueue(job);
                Workspace.Save(new[] { new Workspace.Entry("local", "cmd") { Pane = 1, Active = true, Group = "ops" } });
                if (!Recovery.FlushNow()) throw new Exception("flush failed");
                File.WriteAllText(outFile, "READY=1\n");
                return false;
            }
            var j = File.Exists(Path.Combine(SecureStore.DataDir, Recovery.FileName)) ? SecureStore.Read<Recovery.Journal>(Recovery.FileName) : new Recovery.Journal();
            if (mode == "settle")
            {
                if (Recovery.Previous is { } prev) Recovery.Settle(prev.Drafts.Select(d => d.Id).Concat(prev.Transfers.Select(t => t.Id)));
                Recovery.FlushNow();
                sb.Append("journal=").Append(File.Exists(Path.Combine(SecureStore.DataDir, Recovery.FileName)) ? "present" : "gone").Append('\n');
            }
            else
            {
                var prev = Recovery.Previous;
                var ws = Workspace.Load();
                sb.Append("unclean=").Append(Workspace.UncleanLastExit).Append('\n');
                sb.Append("drafts=").Append(prev?.Drafts.Count ?? 0).Append('\n');
                sb.Append("draftok=").Append(prev?.Drafts.FirstOrDefault()?.Text.Contains(Marker) == true).Append('\n');
                sb.Append("transfers=").Append(prev?.Transfers.Count ?? 0).Append('\n');
                sb.Append("transferlabel=").Append(prev?.Transfers.FirstOrDefault()?.Label).Append('\n');
                sb.Append("workspace=").Append(ws.Count).Append(';').Append(ws.FirstOrDefault()?.Pane).Append(';').Append(ws.FirstOrDefault()?.Active).Append(';').Append(ws.FirstOrDefault()?.Group).Append('\n');
                sb.Append("plain=").Append(Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(SecureStore.DataDir, Recovery.FileName))).Contains(Marker)).Append('\n');
                // Leaving the journal alone keeps it (what "Later" does); this probe exits cleanly.
                Recovery.FlushNow();
            }
        }
        catch (Exception ex) { sb.Append("error=").Append(ex.Message.Replace('\n', ' ')).Append('\n'); }
        Workspace.EndSessionCleanly();
        File.WriteAllText(outFile, sb.ToString());
        return true;
    }

    private static Process Start(string exe, string store, string mode, string outFile)
    {
        try { File.Delete(outFile); } catch { }
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--selftest-crash"); psi.ArgumentList.Add(mode); psi.ArgumentList.Add(outFile);
        psi.Environment["DCX_DATA_DIR"] = store;
        return Process.Start(psi) ?? throw new Exception("couldn't start the probe");
    }

    private static async Task<Dictionary<string, string>> ProbeAsync(string exe, string store, string mode, string outFile)
    {
        using var p = Start(exe, store, mode, outFile);
        try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException) { try { p.Kill(true); } catch { } throw new Exception($"the {mode} probe didn't exit (it may be showing UI)"); }
        if (p.ExitCode != 0) throw new Exception($"the {mode} probe exited with {p.ExitCode}");
        var r = ReadOut(outFile);
        if (r.TryGetValue("error", out var err)) throw new Exception($"{mode} probe: {err}");
        return r;
    }

    private static Dictionary<string, string> ReadOut(string file)
    {
        var d = new Dictionary<string, string>();
        try { foreach (var l in File.ReadAllLines(file)) if (l.IndexOf('=') is > 0 and var i) d[l[..i]] = l[(i + 1)..]; } catch { }
        return d;
    }

    private static string Dump(Dictionary<string, string> d) => string.Join(", ", d.Select(kv => kv.Key + "=" + kv.Value));

    private static IEnumerable<string> Texts(DependencyObject o)
    {
        if (o is TextBlock t) yield return t.Text;
        if (o is ContentControl { Content: DependencyObject c }) foreach (var s in Texts(c)) yield return s;
        if (o is Panel p) foreach (UIElement ch in p.Children) foreach (var s in Texts(ch)) yield return s;
    }

    /// <summary>Renders a detached element to a PNG (no window).</summary>
    private static void Render(FrameworkElement fe, string path)
    {
        var host = new Border { Child = fe, Width = 640 };
        host.SetResourceReference(Border.BackgroundProperty, "Bg");
        host.SetResourceReference(TextElement.ForegroundProperty, "Fg");
        host.Measure(new Size(640, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
        host.Child = null;
    }
}
