using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using DcxManager.Docs;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// ADR-091 (0.6.12, DarkCipher0x: "a powerful but lightweight and efficient markdown editor (separate from notes), but allows me
/// to pin to servers or clusters etc, and store in organised directories"). The store (folders, pins, search, revisions,
/// trash, encryption on disk, damaged files, path hygiene), the renderer (blocks, safe links, no image fetching, raw HTML
/// as text, speed) and the Docs screen (smart lists, autosave, preview, pinned documents from a host). Demo data only;
/// no window or dialog.
/// </summary>
public static class DocsTests
{
    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail, Action<Window, string> snap)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        var host = new HostEntry { Name = "pve-node-qx7", Address = "198.51.100.40", Username = "demo", Group = "Lab/Proxmox" };
        var prov = new Infra.ProviderConfig { Kind = "proxmox", Name = "Lab cluster qx7", Endpoint = "https://pve.test:8006" };
        AppData.Current.Hosts.Add(host);
        AppData.Current.InfraProviders.Add(prov);
        var made = new List<Doc>();
        try
        {
            // 1. The store.
            try
            {
                DocStore.Reset();
                if (DocStore.NormalizeFolder(@"..\Runbooks/./Linux//") != "Runbooks/Linux" || DocStore.NormalizeFolder("/a/../b") != "a/b") throw new Exception("folder paths aren't cleaned");
                var a = DocStore.Create("Patch the Proxmox nodes qx7", "Runbooks/Proxmox", new[] { "host:" + host.Id, "infra:" + prov.Id }, "# Patch\n\nsecret-marker-qx7");
                var b = DocStore.Create("Lab network map qx7", "Runbooks", new[] { "hostfolder:Lab" });
                var c = DocStore.Create("Scratch qx7", "", null, "nothing");
                made.AddRange(new[] { a, b, c });
                if (DocStore.ValidPin("file:C:/x") || DocStore.ValidPin("host:../../x") || !DocStore.ValidPin("hostfolder:Lab/Proxmox")) throw new Exception("pin validation");
                // Pins: a host sees its own and its folders' documents; a provider its own.
                var forHost = DocStore.PinnedTo("host:" + host.Id).Select(d => d.Id).ToList();
                if (!forHost.Contains(a.Id) || !forHost.Contains(b.Id) || forHost.Contains(c.Id)) throw new Exception("pinned to the host: " + string.Join(",", forHost));
                if (DocStore.PinnedTo("infra:" + prov.Id).Count != 1 || !DocStore.PinLabel("infra:" + prov.Id).Contains("Lab cluster qx7") || !DocStore.PinLabel("host:" + host.Id).Contains("pve-node-qx7"))
                    throw new Exception("provider pins / labels");
                // Folders: rename moves the documents inside; remove moves them up; nothing is deleted.
                DocStore.CreateFolder("Archive/2026");
                DocStore.RenameFolder("Runbooks", "Ops/Runbooks");
                if (a.Folder != "Ops/Runbooks/Proxmox" || b.Folder != "Ops/Runbooks" || !DocStore.Folders().Contains("Archive/2026")) throw new Exception($"rename: {a.Folder} / {b.Folder}");
                try { DocStore.RenameFolder("Ops", "Ops/Runbooks/inner"); throw new Exception("a folder moved into itself"); } catch (InvalidOperationException) { }
                DocStore.RemoveFolder("Ops/Runbooks");
                if (a.Folder != "Ops/Proxmox" || b.Folder != "Ops") throw new Exception($"remove: {a.Folder} / {b.Folder}");
                // Search, revisions, trash.
                if (DocStore.Search("proxmox patch").FirstOrDefault()?.Id != a.Id || DocStore.Search("nothing-like-this").Count != 0) throw new Exception("search");
                var before = a.Body;
                a.Body += "\n\n- [ ] step";
                DocStore.Save(a, a.Title, before, snapshot: true);
                if (a.Revisions.Count != 1 || a.Revisions[0].Body != before) throw new Exception("revision");
                DocStore.Delete(c);
                if (DocStore.All.Any(d => d.Id == c.Id) || DocStore.Trash.All(d => d.Id != c.Id)) throw new Exception("trash");
                DocStore.Restore(c);
                // On disk: encrypted (no title, body or pin in the file), and a damaged file is set aside, never overwritten.
                var raw = File.ReadAllBytes(Path.Combine(SecureStore.DataDir, a.FileName));
                var text = Encoding.UTF8.GetString(raw);
                if (text.Contains("Proxmox", StringComparison.OrdinalIgnoreCase) || text.Contains("secret-marker-qx7") || text.Contains(host.Id)) throw new Exception("a document is readable on disk");
                var bad = Path.Combine(SecureStore.DataDir, Doc.FileFor("0123456789abcdef0123456789abcdef"));
                File.WriteAllBytes(bad, Encoding.UTF8.GetBytes("not a sealed file"));
                DocStore.Reset();
                _ = DocStore.All;   // the reload (it fills Damaged)
                if (!DocStore.Damaged.Contains(Path.GetFileName(bad)) || DocStore.Find(a.Id) is not { } a2 || a2.Revisions.Count != 1 || a2.Folder != "Ops/Proxmox" || a2.Pins.Count != 2) throw new Exception("reload");
                if (!File.ReadAllText(bad).StartsWith("not a sealed")) throw new Exception("a damaged file was overwritten");
                File.Delete(bad);
                DocStore.Reset();
                made.Clear(); made.AddRange(DocStore.All.Where(d => d.Title.EndsWith("qx7")));
                ok("docs store: documents in folders (paths cleaned, ../ dropped), pinned to a host (and through its folder), a host folder and an infrastructure provider, with readable pin labels; renaming a folder moves what's inside, a folder can't move into itself, removing one moves its documents up (nothing deleted); search, revisions, trash and restore; each document is its own encrypted file (no title, body or pin on disk); a damaged file is set aside and never overwritten");
            }
            catch (Exception ex) { fail("docs store", ex); }

            // 2. The renderer.
            try
            {
                const string md = "# Title\n\nSome **bold**, *italic*, ~~old~~ and `code`.  \nNext line.\n\n## Steps\n\n1. First\n2. Second\n   - nested\n\n- [x] done\n- [ ] todo\n\n> a quote\n\n" +
                                  "```bash\napt update && apt full-upgrade\n```\n\n| Node | Role |\n|---|:---:|\n| pve1 | primary |\n\n---\n\n[docs](https://pve.proxmox.com/wiki) [bad](javascript:alert(1)) [file](file:///C:/x) <https://example.test/x>\n\n" +
                                  "![diagram](https://tracker.example/pixel.png)\n\n<script>alert(1)</script>";
                var opened = new List<string>();
                var doc = MarkdownRenderer.Render(md, opened.Add);
                var blocks = doc.Blocks.ToList();
                int headings = blocks.OfType<Paragraph>().Count(p => p.FontWeight == FontWeights.Bold);
                var allText = new TextRange(doc.ContentStart, doc.ContentEnd).Text;
                var links = Descendants(doc).OfType<Hyperlink>().ToList();
                if (headings < 2 || blocks.OfType<List>().Count() < 2 || blocks.OfType<Table>().Count() != 1 || blocks.OfType<Section>().Count() != 1 || blocks.OfType<BlockUIContainer>().Count() != 1)
                    throw new Exception($"blocks: {string.Join(", ", blocks.Select(x => x.GetType().Name))}");
                if (links.Count != 2 || links.Any(l => l.ToolTip is string t && !t.StartsWith("https://"))) throw new Exception("only the https links may be live: " + string.Join(", ", links.Select(l => l.ToolTip)));
                if (!allText.Contains("☑") || !allText.Contains("☐") || !allText.Contains("apt full-upgrade") || !allText.Contains("<script>alert(1)</script>") || !allText.Contains("🖼 diagram"))
                    throw new Exception("text: " + allText.Replace("\n", " ⏎ ")[..Math.Min(400, allText.Length)]);
                if (Descendants(doc).OfType<System.Windows.Controls.Image>().Any()) throw new Exception("an image element was created (images are never fetched)");
                if (!MarkdownRenderer.LinkAllowed("https://x.test") || MarkdownRenderer.LinkAllowed("http://x.test") || MarkdownRenderer.LinkAllowed("javascript:x") || MarkdownRenderer.LinkAllowed("file:///c:/x"))
                    throw new Exception("link rules");
                // Speed: a long runbook (4,000 lines) renders quickly enough to preview while typing.
                var big = new StringBuilder();
                for (int i = 0; i < 400; i++) big.Append($"## Section {i}\n\nText with **bold** and `code` {i}.\n\n- item\n- [ ] task\n\n```\ncode {i}\n```\n\n");
                var sw = Stopwatch.StartNew();
                var bigDoc = MarkdownRenderer.Render(big.ToString(), _ => { });
                sw.Stop();
                if (bigDoc.Blocks.Count < 1600 || sw.ElapsedMilliseconds > 3000) throw new Exception($"a 4,000-line document: {bigDoc.Blocks.Count} blocks in {sw.ElapsedMilliseconds} ms");
                if (MarkdownRenderer.Outline(md).Select(o => o.Text).SequenceEqual(new[] { "Title", "Steps" }) == false) throw new Exception("outline");
                // A crafted line (tens of thousands of markers) can't freeze the preview: it renders, as plain text if need be.
                var crafted = new string('*', 40_000) + "x" + string.Concat(Enumerable.Repeat("_a", 20_000));
                var csw = Stopwatch.StartNew();
                MarkdownRenderer.Render(crafted, _ => { });
                if (csw.ElapsedMilliseconds > 3000) throw new Exception($"a crafted line took {csw.ElapsedMilliseconds} ms");
                ok($"docs renderer: headings, paragraphs with hard breaks, bold / italic / strike / code, nested numbered and bullet lists, task lists (☑ ☐), quotes, fenced code, tables with alignment and rules; only https: links are live (javascript:, file: and http: stay text), images are never fetched (their description shows), raw HTML stays text; a 4,000-line document renders in {sw.ElapsedMilliseconds} ms; the outline lists the headings");
            }
            catch (Exception ex) { fail("docs renderer", ex); }

            // 3. The screen: smart lists, autosave, preview, a host's pinned documents; rendered for the guide / website.
            try
            {
                main.Navigate("docs");
                await Settle(400);
                var v = DocsView.Current ?? throw new Exception("the Docs screen didn't open");
                var d = v.NewDoc("Ops/Proxmox", "host:" + host.Id);
                made.Add(d);
                v.BodyForTest = "# Patch the Proxmox nodes\n\n- [ ] Migrate the VMs";
                v.CaretToEndForTest();
                if (!v.ContinueListForTest() || !v.BodyForTest.EndsWith("\n- [ ] ")) throw new Exception("Enter on a task line didn't start the next task: " + v.BodyForTest.Replace("\n", "⏎"));
                v.BodyForTest = DemoRunbook;
                v.SaveNowForTest();
                if (DocStore.Find(d.Id)?.Body != DemoRunbook) throw new Exception("autosave");
                if (d.Title != "Patch the Proxmox nodes") throw new Exception("an untitled document didn't take its first heading as the title: " + d.Title);
                v.ModeForTest("split");
                await Settle(500);
                if (v.PreviewForTest.Document is not { } pd || pd.Blocks.Count < 5) throw new Exception("no preview");
                snap(main, Path.Combine(reportDir, "shots", "docs.png"));
                main.OpenDocs("host:" + host.Id, host.Display);
                await Settle(400);
                // The host's own documents open first (here three are pinned to it or its folder; the directly pinned ones lead).
                if (DocsView.Current?.OpenDocForTest?.Pins.Contains("host:" + host.Id) != true) throw new Exception("the host's pinned documents didn't open (or a folder's opened first): " + DocsView.Current?.OpenDocForTest?.Title);
                ok($"docs screen: a new document lands in the chosen folder with its pin, Enter on a task line starts the next task, typing autosaves (encrypted), an untitled document takes its first heading as the title, the preview renders beside the editor (split), and a host's 📄 Docs opens what is pinned to it; rendered docs.png ({v.LastRenderMs:0} ms preview)");
            }
            catch (Exception ex) { fail("docs screen", ex); }
        }
        finally
        {
            foreach (var d in made) { try { File.Delete(Path.Combine(SecureStore.DataDir, d.FileName)); } catch { } }
            try { File.Delete(Path.Combine(SecureStore.DataDir, DocStore.FoldersFile)); } catch { }
            DocStore.Reset();
            AppData.Current.Hosts.Remove(host);
            AppData.Current.InfraProviders.Remove(prov);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(FlowDocument doc)
    {
        var stack = new Stack<DependencyObject>(new[] { (DependencyObject)doc });
        while (stack.Count > 0)
        {
            var o = stack.Pop();
            yield return o;
            foreach (var child in LogicalTreeHelper.GetChildren(o).OfType<DependencyObject>()) stack.Push(child);
        }
    }

    /// <summary>A demo runbook (TEST-NET addresses, made-up names) for the screenshot.</summary>
    private const string DemoRunbook =
        "# Patch the Proxmox nodes\n\n" +
        "Monthly patching for the **lab cluster**. Migrate guests first, one node at a time.\n\n" +
        "## Before you start\n\n" +
        "- [x] Snapshot the critical VMs\n- [x] Check the cluster is quorate: `pvecm status`\n- [ ] Tell the team in #ops\n\n" +
        "## Steps\n\n" +
        "1. Migrate the guests off `pve1` (198.51.100.41)\n2. Update and reboot:\n\n" +
        "```bash\napt update && apt full-upgrade -y\nreboot\n```\n\n" +
        "3. Confirm the node rejoined, then repeat for the next one\n\n" +
        "| Node | Address | Role |\n|---|---|:---:|\n| pve1 | 198.51.100.41 | primary |\n| pve2 | 198.51.100.42 | replica |\n\n" +
        "> Never patch two nodes at once: the cluster loses quorum.\n\n" +
        "See the [Proxmox upgrade notes](https://pve.proxmox.com/wiki/Upgrade_from_8_to_9).\n";
}
