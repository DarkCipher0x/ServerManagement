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

// SelfTest: notes, incidents, documents and vision (moved unchanged out of RunFullAsync in 0.2.0 phase 6½; the run's shared state is SelfTestRun).
public static partial class SelfTest
{
    private static async Task NotesIncidentsDocumentsAsync(SelfTestRun sharedRun)
    {
        var main = sharedRun.Main; var reportPath = sharedRun.ReportPath; Action<string> Ok = sharedRun.Ok; Action<string, Exception?> Fail = sharedRun.Fail; SelfTestRun.SettleFn Settle = sharedRun.Settle;

        // Notes & knowledge base (ADR-005): encrypted per-note files, deterministic offline search incl. natural phrasing,
        // revisions, trash, damaged/swapped files never overwritten, backup round trip, index speed, live autosave.
        try
        {
            Notes.NoteStore.Reset();
            var web = new HostEntry { Name = "PROD-WEB-04", Group = "Production", Address = "203.0.113.40", Username = "alex" };
            var db = new HostEntry { Name = "docker-01", Group = "LAN", Address = "192.168.1.30", Username = "sam" };
            AppData.Current.Hosts.AddRange(new[] { web, db }); AppData.Save();
            var n1 = Notes.NoteStore.Create("nginx certificate issue", new[] { web.Id }, "[14:06:17] Connected to PROD-WEB-04\n[14:08:42] nginx -t failed: certificate expired\n```\ncertbot renew --dry-run\n```\n");
            n1.Tags.AddRange(new[] { "tls", "nginx" }); Notes.NoteStore.Save(n1);
            var n2 = Notes.NoteStore.Create("Resolver trouble on the LAN", null, "DNS problems: resolv.conf pointed at 192.0.2.53 which stopped answering.");
            var n3 = Notes.NoteStore.Create("Docker host disk full", new[] { db.Id }, "Pruned images; /var/lib/docker was at 97%.");
            n3.Favorite = true; Notes.NoteStore.Save(n3);

            string First(string q) => Notes.NoteStore.Search(q).FirstOrDefault()?.Note.Id ?? "none";
            int Count(string q) => Notes.NoteStore.Search(q).Count;
            if (First("nginx certificate issue") != n1.Id) throw new Exception("nginx certificate issue");
            if (First("everything I wrote about PROD-WEB-04") != n1.Id || Count("everything I wrote about PROD-WEB-04") != 1) throw new Exception("host name search");
            if (First("DNS problems") != n2.Id || First("tag:tls") != n1.Id || Count("is:fav") != 1 || First("is:fav") != n3.Id) throw new Exception("dns/tag/fav");
            if (First("\"certbot renew\"") != n1.Id || First("192.0.2") != n2.Id || First("certif") != n1.Id || Count("zzqqxx") != 0) throw new Exception("phrase/prefix/none");
            if (First("all incidents involving docker-01") != n3.Id || !Notes.NoteStore.LastIgnored.Contains("incidents") || First("host:docker") != n3.Id) throw new Exception("host filter");
            if (Count("incidents") != 0) throw new Exception("a lone unknown word must not match everything");
            // "notes from last Tuesday"
            var today = DateTime.Today; int back = ((int)today.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7; if (back == 0) back = 7;
            n2.Created = n2.Updated = today.AddDays(-back).AddHours(10);
            var lt = Notes.NoteStore.Search("notes from last Tuesday");
            if (lt.Count != 1 || lt[0].Note != n2) throw new Exception("last tuesday: " + lt.Count);
            var pq = Notes.NoteIndex.Parse("after:2026-10-01 before:2026-10-05 tag:dns x");
            if (pq.From != new DateTime(2026, 10, 2) || pq.To != new DateTime(2026, 10, 5) || pq.Tags[0] != "dns") throw new Exception("parse");

            // Encrypted at rest, atomic, file name as AAD (no swapping), damaged files left untouched.
            var file1 = Path.Combine(SecureStore.DataDir, n1.FileName);
            if (Encoding.UTF8.GetString(File.ReadAllBytes(file1)).Contains("certbot") || Directory.GetFiles(SecureStore.DataDir, "note-*.tmp").Length != 0) throw new Exception("plaintext or temp left");
            var swapped = Path.Combine(SecureStore.DataDir, Notes.Note.FileFor(Guid.NewGuid().ToString("N")));
            File.Copy(file1, swapped);
            var junk = Path.Combine(SecureStore.DataDir, Notes.Note.FileFor(Guid.NewGuid().ToString("N")));
            var junkBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(300);
            File.WriteAllBytes(junk, junkBytes);
            Notes.NoteStore.Reset();
            if (Notes.NoteStore.All.Count != 3 || Notes.NoteStore.Damaged.Count != 2 || Notes.NoteStore.Find(n1.Id)?.Body.Contains("certbot") != true) throw new Exception($"reload/damaged: {Notes.NoteStore.All.Count} / {Notes.NoteStore.Damaged.Count}");
            if (!File.ReadAllBytes(junk).SequenceEqual(junkBytes)) throw new Exception("damaged file was modified");
            File.Delete(swapped); File.Delete(junk);
            Notes.NoteStore.Reset();
            n1 = Notes.NoteStore.Find(n1.Id)!; n2 = Notes.NoteStore.Find(n2.Id)!; n3 = Notes.NoteStore.Find(n3.Id)!;

            // Revisions: kept, capped.
            var every = Notes.NoteStore.RevisionEvery;
            Notes.NoteStore.RevisionEvery = TimeSpan.Zero;
            for (int i = 0; i < 40; i++) { var b = n2.Body; n2.Body += $"\nline {i}"; Notes.NoteStore.Save(n2, n2.Title, b); }
            Notes.NoteStore.RevisionEvery = every;
            if (n2.Revisions.Count != Notes.NoteStore.MaxRevisions || !n2.Revisions[^1].Body.EndsWith("line 38")) throw new Exception("revisions " + n2.Revisions.Count);

            // Trash: soft delete, excluded from search, restorable; only Empty trash deletes the file.
            Notes.NoteStore.Delete(n3);
            if (Notes.NoteStore.All.Contains(n3) || !Notes.NoteStore.Trash.Contains(n3) || Count("docker") != 0 || !File.Exists(Path.Combine(SecureStore.DataDir, n3.FileName))) throw new Exception("soft delete");
            Notes.NoteStore.Restore(n3);
            if (First("docker disk") != n3.Id) throw new Exception("restore");

            // Terminal "Add selection to notes" → one log note per host, appended with timestamps.
            var log1 = Notes.NoteStore.AppendToLog(web.Display, web.Id, null, "```\nsystemctl status nginx\n```");
            var log2 = Notes.NoteStore.AppendToLog(web.Display, web.Id, null, "```\njournalctl -u nginx\n```");
            if (log1 != log2 || !log2.Body.Contains("journalctl") || !log2.Tags.Contains("log") || Notes.NoteStore.ForHost(web.Id).Count != 2) throw new Exception("host log");
            var serialLog = Notes.NoteStore.AppendToLog("console", null, "serial:abc", "show version");
            if (serialLog == log1 || serialLog.SessionKey != "serial:abc") throw new Exception("session log");

            // Store move + backup cover notes.
            if (!DataMover.StoreFiles(SecureStore.DataDir).Any(f => Path.GetFileName(f) == n1.FileName)) throw new Exception("store mover would skip notes");
            var backupPath = Path.Combine(Path.GetDirectoryName(reportPath)!, "notes-test.dcxbak");
            var bf = Backup.Full(new Backup.Include());
            if (bf.Notes?.Count != Notes.NoteStore.All.Count + Notes.NoteStore.Trash.Count) throw new Exception("backup notes count");
            Backup.Write(backupPath, "selftest-pass-123", bf);
            var back2 = Backup.Read(backupPath, "selftest-pass-123");
            File.Delete(backupPath);
            File.Delete(Path.Combine(SecureStore.DataDir, n2.FileName));
            Notes.NoteStore.Reset();
            if (Notes.NoteStore.Find(n2.Id) != null) throw new Exception("delete for restore test");
            var evil = new Notes.Note { Id = "..\\..\\evil", Title = "path traversal" };
            int imported = Notes.NoteStore.Import(back2.Notes!.Append(evil));
            if (imported != 1 || Notes.NoteStore.Find(n2.Id)?.Revisions.Count != Notes.NoteStore.MaxRevisions || File.Exists(Path.Combine(SecureStore.DataDir, "..", "evil"))) throw new Exception("import " + imported);

            // Index speed at 3,000 notes (in memory only).
            var idx = new Notes.NoteIndex();
            var big = Enumerable.Range(0, 3000).Select(i => new Notes.Note { Title = $"Change {i} on host-{i % 50}", Body = string.Join(" ", Enumerable.Range(0, 120).Select(j => $"word{(i * 7 + j) % 900} 10.0.{i % 255}.{j % 255}")) }).ToList();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var bn in big) idx.Add(bn, new[] { $"host-{big.IndexOf(bn) % 50}" });
            var build = sw.ElapsedMilliseconds; sw.Restart();
            var res = idx.Search("word12 host-7", big.ToDictionary(x => x.Id), _ => Array.Empty<string>());
            var search = sw.ElapsedMilliseconds;
            if (res.Count == 0 || build > 4000 || search > 300) throw new Exception($"index speed: build {build} ms, search {search} ms, {res.Count} hits");

            // The screen: open a note, type, autosave writes the encrypted file.
            main.Navigate("notes"); await Settle(500);
            var nv = NotesView.Current ?? throw new Exception("notes view");
            nv.Open(n1); await Settle(200);
            nv.TestType("\n[autosave probe] 7f3a");
            await Settle(1800);
            Notes.NoteStore.Reset();
            if (Notes.NoteStore.Find(n1.Id)?.Body.Contains("[autosave probe] 7f3a") != true) throw new Exception("autosave did not reach disk");
            nv.Refresh(); nv.Open(Notes.NoteStore.Find(n1.Id)!); await Settle(400);
            var shots = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(reportPath)!, "shots")).FullName;
            Snap(main, Path.Combine(shots, "notes.png"));
            Ok($"notes: natural/offline search (\"nginx certificate issue\", \"everything I wrote about PROD-WEB-04\", \"DNS problems\", \"notes from last Tuesday\", tag:/host:/is:fav/phrase/prefix), encrypted per-note files, swapped (AAD) + damaged files left untouched, {Notes.NoteStore.MaxRevisions}-revision cap, trash/restore, host + session logs, store mover + backup round trip, path-traversal id refused on import, 3,000-note index in {build} ms / search {search} ms, live autosave to disk; rendered notes.png");
        }
        catch (Exception ex) { Fail("notes", ex); }

        // Incidents / work sessions (ADR-010): encrypted incident + attachment files, captures into the ACTIVE incident
        // without dialogs, attachments bound to their incident, connection de-duplication, status timeline, Markdown/AI
        // material (attachments named only, secrets masked by the preview's redaction), damaged files untouched,
        // backup round trip incl. attachments, trash + purge, caps, schema/recovery integration, rendered screen.
        try
        {
            Incidents.IncidentStore.Reset();
            var web = AppData.Current.Hosts.FirstOrDefault(h => h.Name == "PROD-WEB-04") ?? new HostEntry { Name = "PROD-WEB-04", Address = "203.0.113.40", Username = "alex" };
            if (!AppData.Current.Hosts.Contains(web)) { AppData.Current.Hosts.Add(web); AppData.Save(); }
            var inc = Incidents.IncidentStore.Create("selftest: nginx 502 on web", new[] { web.Id }, Incidents.IncidentSeverity.Sev2, "PRODUCTION");
            Incidents.IncidentStore.Active = inc;
            if (Incidents.IncidentStore.Active != inc || AppSettings.Current.ActiveIncidentId != inc.Id) throw new Exception("active");
            var incPath = Path.Combine(SecureStore.DataDir, inc.FileName);
            if (Encoding.UTF8.GetString(File.ReadAllBytes(incPath)).Contains("nginx 502")) throw new Exception("plaintext incident");

            // captures go to the active incident with no dialog
            IncidentUi.AddText(Incidents.ItemKind.Terminal, "web: systemctl status nginx", "alex@203.0.113.40\n```\nnginx.service: failed (Result: exit-code)\n```", web);
            var px = new byte[16 * 16 * 4]; for (int i = 0; i < px.Length; i += 4) { px[i] = 0x20; px[i + 1] = 0x80; px[i + 2] = 0xC0; px[i + 3] = 0xFF; }
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(16, 16, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, 64); bmp.Freeze();
            IncidentUi.AddScreenshot(bmp, "web console", web);
            var shot = inc.Items.Last();
            if (shot.Kind != Incidents.ItemKind.Screenshot || shot.AttachmentId == null) throw new Exception("screenshot item");
            var attPath = Path.Combine(SecureStore.DataDir, Incidents.IncidentAttachment.FileFor(shot.AttachmentId));
            var attRaw = File.ReadAllBytes(attPath);
            if (attRaw.AsSpan().IndexOf("PNG"u8) >= 0 || Encoding.UTF8.GetString(attRaw).Contains("web console")) throw new Exception("attachment not encrypted");
            var png = Incidents.IncidentStore.ReadAttachment(inc, shot);
            if (png.Length < 8 || png[1] != (byte)'P' || png[2] != (byte)'N') throw new Exception("attachment round trip");
            var other = Incidents.IncidentStore.Create("selftest: other");
            try { Incidents.IncidentStore.ReadAttachment(other, shot); throw new Exception("attachment readable through another incident"); } catch (InvalidDataException) { }
            IncidentUi.AddText(Incidents.ItemKind.AiAnalysis, "AI: likely cause", "php-fpm pool exhausted. Seen in config: password=hunter2-secret");
            var note = Notes.NoteStore.Create("selftest incident note", new[] { web.Id }, "php-fpm restarted at 10:02, 502s stopped");
            Incidents.IncidentStore.Add(inc, new Incidents.IncidentItem { Kind = Incidents.ItemKind.Note, Title = note.Title, NoteId = note.Id });
            Incidents.IncidentStore.RecordConnection(web, "Terminal session");
            Incidents.IncidentStore.RecordConnection(web, "Terminal session");
            if (inc.Items.Count(x => x.Kind == Incidents.ItemKind.Connection) != 1) throw new Exception("connection de-dup");
            if (Incidents.IncidentStore.ForHost(web.Id).FirstOrDefault() == null) throw new Exception("for host");

            // Markdown / AI material: subset, notes by current text, attachments named only, redaction catches the secret
            var md = Incidents.IncidentStore.Markdown(inc);
            if (!md.Contains("php-fpm restarted") || !md.Contains("web console") || !md.Contains("content not included") || md.Contains(Convert.ToBase64String(png)[..20]) || !md.Contains("PRODUCTION")) throw new Exception("markdown");
            var sub = Incidents.IncidentStore.Markdown(inc, inc.Items.Where(x => x.Kind == Incidents.ItemKind.Terminal));
            if (sub.Contains("likely cause") || !sub.Contains("nginx.service")) throw new Exception("subset");
            var (masked, maskedCount) = Ai.AiClient.RedactCounted(IncidentsView.SummaryMaterial(inc, inc.Items));
            if (masked.Contains("hunter2-secret") || maskedCount == 0) throw new Exception("redaction of incident material");

            // status timeline; resolved incidents stop being active
            Incidents.IncidentStore.SetStatus(inc, Incidents.IncidentStatus.Resolved, "php-fpm pool raised");
            if (inc.Resolved == null || !inc.Items.Any(x => x.Kind == Incidents.ItemKind.Status && x.Title == "Open → Resolved") || Incidents.IncidentStore.Active != null) throw new Exception("status");
            Incidents.IncidentStore.SetStatus(inc, Incidents.IncidentStatus.Monitoring);
            if (inc.Resolved != null) throw new Exception("reopen clears resolved");
            Incidents.IncidentStore.Active = inc;

            // reload from disk; schema + recovery know both file kinds
            int items = inc.Items.Count;
            Incidents.IncidentStore.Reset();
            inc = Incidents.IncidentStore.Find(inc.Id) ?? throw new Exception("reload");
            if (inc.Items.Count != items || Incidents.IncidentStore.Damaged.Count != 0) throw new Exception("reload items");
            StoreSchema.ParseCheck(inc.FileName, SecureStore.Decrypt(File.ReadAllBytes(incPath), inc.FileName));
            StoreSchema.ParseCheck(Path.GetFileName(attPath), SecureStore.Decrypt(File.ReadAllBytes(attPath), Path.GetFileName(attPath)));
            if (StoreSchema.KindOf(inc.FileName) != "incidents" || StoreSchema.KindOf(Path.GetFileName(attPath)) != "incident-files" || StoreRecovery.Check().Count != 0) throw new Exception("schema kinds");

            // damaged / swapped incident files are listed and never touched
            var junk = Path.Combine(SecureStore.DataDir, Incidents.Incident.FileFor(Guid.NewGuid().ToString("N")));
            var junkBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(200);
            File.WriteAllBytes(junk, junkBytes);
            var swapped = Path.Combine(SecureStore.DataDir, Incidents.Incident.FileFor(Guid.NewGuid().ToString("N")));
            File.Copy(incPath, swapped);
            Incidents.IncidentStore.Reset();
            int loaded = Incidents.IncidentStore.All.Count;   // loading fills Damaged
            bool damagedOk = Incidents.IncidentStore.Damaged.Count == 2 && File.ReadAllBytes(junk).SequenceEqual(junkBytes) && loaded == 2;
            File.Delete(junk); File.Delete(swapped);
            if (!damagedOk) throw new Exception($"damaged incident files: {Incidents.IncidentStore.Damaged.Count} damaged, {loaded} loaded");
            Incidents.IncidentStore.Reset();
            inc = Incidents.IncidentStore.Find(inc.Id)!;
            other = Incidents.IncidentStore.Find(other.Id)!;

            // caps
            try { Incidents.IncidentStore.Add(inc, new Incidents.IncidentItem { Kind = Incidents.ItemKind.File, Title = "huge" }, new byte[Incidents.IncidentStore.MaxAttachment + 1]); throw new Exception("oversize attachment accepted"); }
            catch (InvalidOperationException) { }
            var longItem = Incidents.IncidentStore.Add(inc, new Incidents.IncidentItem { Kind = Incidents.ItemKind.Log, Title = "long", Text = new string('x', Incidents.IncidentStore.MaxText + 50) });
            if (longItem.Text.Length > Incidents.IncidentStore.MaxText + 100) throw new Exception("text cap");
            Incidents.IncidentStore.RemoveItem(inc, longItem);

            // backup round trip with attachments; ids validated on import
            var bf = Backup.Full(new Backup.Include());
            if (bf.Incidents?.Any(x => x.Id == inc.Id) != true || bf.IncidentFiles?.Any(f => f.Id == shot.AttachmentId) != true) throw new Exception("backup content");
            var bpath = Path.Combine(Path.GetDirectoryName(reportPath)!, "incidents-test.dcxbak");
            Backup.Write(bpath, "selftest-pass-123", bf);
            var back = Backup.Read(bpath, "selftest-pass-123");
            File.Delete(bpath);
            Incidents.IncidentStore.Delete(inc);
            if (Incidents.IncidentStore.Active != null || !Incidents.IncidentStore.Trash.Contains(inc) || !File.Exists(attPath)) throw new Exception("trash");
            if (Incidents.IncidentStore.EmptyTrash() != 1 || File.Exists(incPath) || File.Exists(attPath)) throw new Exception("purge");
            var evil = new Incidents.Incident { Id = "..\\..\\evil", Title = "traversal" };
            var evilAtt = new Incidents.IncidentAttachment { Id = "..\\x", IncidentId = inc.Id, Data = new byte[] { 1 } };
            int imported = Incidents.IncidentStore.Import(back.Incidents!.Append(evil), back.IncidentFiles!.Append(evilAtt));
            var restored = Incidents.IncidentStore.Find(inc.Id) ?? throw new Exception("not imported");
            if (imported != 1 || Incidents.IncidentStore.ReadAttachment(restored, restored.Items.First(x => x.Id == shot.Id)).Length != png.Length || File.Exists(Path.Combine(SecureStore.DataDir, "..", "evil"))) throw new Exception("import " + imported);

            // removing an entry deletes its attachment
            var shot2 = restored.Items.First(x => x.Id == shot.Id);
            Incidents.IncidentStore.RemoveItem(restored, shot2);
            if (File.Exists(attPath)) throw new Exception("attachment left after removing its entry");
            Incidents.IncidentStore.Delete(other); Incidents.IncidentStore.EmptyTrash();

            // the screen
            Incidents.IncidentStore.Active = restored;
            main.OpenIncidents(restored); await Settle(700);
            var iv = IncidentsView.Current ?? throw new Exception("incidents view");
            if (iv.OpenIncident != restored || iv.TimelineCount != restored.Items.Count) throw new Exception($"view: {iv.TimelineCount} rows for {restored.Items.Count}");
            var shots = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(reportPath)!, "shots")).FullName;
            Snap(main, Path.Combine(shots, "incidents.png"));
            Ok("incidents: encrypted incident + attachment files, captures into the active incident (terminal, screenshot, AI answer, note link), attachments bound to their incident, connection de-dup, status timeline + resolve/reopen, Markdown subset with attachments named only, secrets masked in AI material, damaged/swapped files untouched, caps, backup round trip with attachments, traversal ids refused, trash + purge, schema kinds; rendered incidents.png");
        }
        catch (Exception ex) { Fail("incidents", ex); }

        // Documents & vision (ROADMAP #7): real PDF text extraction with honest states; images only for verified vision
        // models (checked before any network call); redaction burned into the pixels. No model or network is used.
        try
        {
            byte[] MakePdf(params string?[] pages)
            {
                var b = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
                var font = b.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
                foreach (var t in pages)
                {
                    var page = b.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
                    if (t != null) page.AddText(t, 12, new UglyToad.PdfPig.Core.PdfPoint(40, 760), font);
                }
                return b.Build();
            }
            var mixed = await Documents.PdfText.ExtractAsync(MakePdf("Invoice 2026-10 total 42.00 password=hunter2-xyz", null, "Third page text"));
            if (mixed.State != Documents.PdfTextState.Partial || mixed.Pages != 3 || mixed.PagesWithText != 2 || !mixed.Label.StartsWith("PDF TEXT PARTLY EXTRACTED")) throw new Exception("mixed: " + mixed.Label);
            var all = mixed.Text();
            if (!all.Contains("--- page 1 ---") || !all.Contains("Invoice 2026-10") || !all.Contains("(no machine-readable text on this page)") || !mixed.Text(3, 3).Contains("Third page") || mixed.Text(3, 3).Contains("Invoice")) throw new Exception("page text/ranges");
            var (masked, n) = Ai.AiClient.RedactCounted(all);
            if (n < 1 || masked.Contains("hunter2-xyz")) throw new Exception("redaction count");
            var full = await Documents.PdfText.ExtractAsync(MakePdf("Only page"));
            if (full.State != Documents.PdfTextState.Extracted || !full.Label.StartsWith("PDF TEXT EXTRACTED")) throw new Exception("full: " + full.Label);
            var scanned = await Documents.PdfText.ExtractAsync(MakePdf(null, null));
            if (scanned.State != Documents.PdfTextState.NoText || !scanned.Label.Contains("OCR") || scanned.HasText) throw new Exception("image-only: " + scanned.Label);
            var notPdf = await Documents.PdfText.ExtractAsync(Encoding.ASCII.GetBytes("hello, not a pdf"));
            var corrupt = await Documents.PdfText.ExtractAsync(Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << /Type /Catalog /Pages 9 0 R >> garbage garbage\n%%EOF"));
            if (notPdf.State != Documents.PdfTextState.Failed || corrupt.State is not (Documents.PdfTextState.Failed or Documents.PdfTextState.NoText) || corrupt.HasText) throw new Exception($"bad input: {notPdf.State}/{corrupt.State}");

            // Vision capability: Ollama /api/show parsing.
            System.Text.Json.Nodes.JsonObject J(string s) => (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(s)!;
            if (Ai.AiClient.VisionFromOllamaShow(J("{\"capabilities\":[\"completion\",\"vision\"]}")) != true
                || Ai.AiClient.VisionFromOllamaShow(J("{\"capabilities\":[\"completion\",\"tools\"]}")) != false
                || Ai.AiClient.VisionFromOllamaShow(J("{\"projector_info\":{\"clip.has_vision_encoder\":true}}")) != true
                || Ai.AiClient.VisionFromOllamaShow(J("{\"details\":{\"families\":[\"mllama\"]}}")) != true
                || Ai.AiClient.VisionFromOllamaShow(J("{\"details\":{\"families\":[\"llama\"]}}")) != false
                || Ai.AiClient.VisionFromOllamaShow(J("{}")) != null) throw new Exception("ollama show parsing");

            // Per engine, and the guards that refuse images BEFORE any request (OpenAI-compatible on a dead local port).
            var s = AppSettings.Current;
            var saved = (s.AiEnabled, s.AiEngine, s.OpenAiUrl, s.OpenAiModel, s.OpenAiAcceptsImages, s.AnthropicUrl, s.AnthropicModel, s.AiFeatureFiles);
            var ws = Ai.AiWorkspace.Current;
            var chat = ws.Chat;
            try
            {
                s.AiEnabled = true; s.AiFeatureFiles = true;
                s.AiEngine = "anthropic"; s.AnthropicUrl = "https://api.anthropic.com"; s.AnthropicModel = "claude-sonnet-5-5";
                if (!(await Ai.AiClient.VisionAsync()).Supported) throw new Exception("claude should accept images");
                s.AiEngine = "openai"; s.OpenAiUrl = "http://127.0.0.1:1/v1"; s.OpenAiModel = "local-model"; s.OpenAiAcceptsImages = false;
                if ((await Ai.AiClient.VisionAsync()).Supported) throw new Exception("unverified OpenAI-compatible model accepted images");
                var withImage = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = "what is this?", ["images"] = new System.Text.Json.Nodes.JsonArray("iVBORw0KGgo=") } };
                try { await Ai.AiClient.ChatAsync(withImage, null, null, CancellationToken.None); throw new Exception("image chat to a text model was attempted"); }
                catch (InvalidOperationException e) when (e.Message.StartsWith("Not sent")) { }

                // The workspace: image item listed, refused for an unverified model (model never called), attached once allowed.
                bool called = false; System.Text.Json.Nodes.JsonArray? sent = null;
                ws.Chat = (m, ct) => { called = true; sent = m; return Task.FromResult(new System.Text.Json.Nodes.JsonObject { ["role"] = "assistant", ["content"] = "I see a test pattern." }); };
                ws.NewConversation();
                var white = new System.Windows.Media.Imaging.RenderTargetBitmap(200, 100, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                var dv = new System.Windows.Media.DrawingVisual();
                using (var dc = dv.RenderOpen()) dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, 200, 100));
                white.Render(dv);
                var (png, pw, ph) = ScreenshotAi.Render(white, new[] { new Rect(10, 10, 50, 40) });
                var decoded = new System.Windows.Media.Imaging.FormatConvertedBitmap(System.Windows.Media.Imaging.BitmapFrame.Create(new MemoryStream(png)), System.Windows.Media.PixelFormats.Bgra32, null, 0);
                var px = new byte[4];
                decoded.CopyPixels(new Int32Rect(20, 20, 1, 1), px, 4, 0);
                var px2 = new byte[4];
                decoded.CopyPixels(new Int32Rect(150, 50, 1, 1), px2, 4, 0);
                if (pw != 200 || px[0] > 10 || px[1] > 10 || px[2] > 10 || px2[0] < 245) throw new Exception("redaction not burned into pixels");
                var wide = new System.Windows.Media.Imaging.RenderTargetBitmap(4000, 50, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                if (ScreenshotAi.Render(wide, Array.Empty<Rect>()).Width != ScreenshotAi.MaxSide) throw new Exception("downscale");

                var item = ws.AddImage("screenshot — selftest", png, pw, ph);
                if (!ws.Context.Contains(item) || item.Glyph != "✓" || !item.Content.Contains("only sent to a model that reads images")) throw new Exception("image context item");
                await ws.SendAsync("What does this show?", Ai.AiFeature.Files, consented: true);
                if (called || ws.Active.Messages.LastOrDefault()?.Role != "error" || !ws.Active.Messages.Last().Text.StartsWith("Not sent")) throw new Exception("image sent to an unverified model");
                s.OpenAiAcceptsImages = true;
                await ws.SendAsync("What does this show?", Ai.AiFeature.Files, consented: true);
                var last = sent?.OfType<System.Text.Json.Nodes.JsonObject>().LastOrDefault();
                if (!called || sent == null || last?["images"] is not System.Text.Json.Nodes.JsonArray { Count: 1 } imgs || imgs[0]!.GetValue<string>() != Convert.ToBase64String(png)) throw new Exception("image not attached once allowed");
                if (sent.OfType<System.Text.Json.Nodes.JsonObject>().Take(sent.Count - 1).Any(m => m["images"] != null)) throw new Exception("image leaked into other messages");
            }
            finally
            {
                ws.Chat = chat;
                (s.AiEnabled, s.AiEngine, s.OpenAiUrl, s.OpenAiModel, s.OpenAiAcceptsImages, s.AnthropicUrl, s.AnthropicModel, s.AiFeatureFiles) = saved;
                ws.NewConversation();
                AppSettings.Save();
            }
            Ok($"documents & vision: PDF extracted (full / partial with per-page markers + ranges / image-only → \"OCR would be required\" / not-a-PDF + corrupt → failed, no exception), secrets in PDF text masked ({n}); Ollama vision parsing (capabilities, projector, families, unknown); Claude yes, unverified OpenAI-compatible no; image chat refused before any request; workspace image item refused for unverified model then attached only to the question; redaction burned into pixels; {ScreenshotAi.MaxSide}px cap");
        }
        catch (Exception ex) { Fail("documents & vision", ex); }
    }
}
