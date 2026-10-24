using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Incidents;

public enum IncidentStatus { Open, Monitoring, Resolved, Closed }

public enum IncidentSeverity { Sev1, Sev2, Sev3, Sev4 }

/// <summary>What a timeline entry is. Screenshot and File carry an encrypted attachment file.</summary>
public enum ItemKind { Event, Terminal, Log, Screenshot, File, Note, Report, AiAnalysis, Connection, Status }

/// <summary>One entry on an incident's timeline.</summary>
public sealed class IncidentItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ItemKind Kind { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
    public string Title { get; set; } = "";
    /// <summary>Text material (terminal excerpt, log, AI answer, event description). Capped at <see cref="IncidentStore.MaxText"/>.</summary>
    public string Text { get; set; } = "";
    public string? HostId { get; set; }
    public string? NoteId { get; set; }
    public string? ReportId { get; set; }
    /// <summary>Screenshot / file: the id of its own encrypted file (incfile-&lt;id&gt;.dcx).</summary>
    public string? AttachmentId { get; set; }
    public string? Mime { get; set; }
    public long Bytes { get; set; }
}

/// <summary>A work session or incident: hosts, a status, and a timeline of everything gathered while working on it.</summary>
public sealed class Incident
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Untitled incident";
    public IncidentStatus Status { get; set; } = IncidentStatus.Open;
    public IncidentSeverity Severity { get; set; } = IncidentSeverity.Sev3;
    /// <summary>PRODUCTION / TEST / LAB / "" (not stated).</summary>
    public string Environment { get; set; } = "";
    /// <summary>Impact and current state, in the engineer's words.</summary>
    public string Summary { get; set; } = "";
    public List<string> HostIds { get; set; } = new();
    public List<IncidentItem> Items { get; set; } = new();
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
    public DateTime? Resolved { get; set; }
    /// <summary>Set when moved to the Trash; files are only deleted by "Empty trash".</summary>
    public DateTime? Deleted { get; set; }

    public string FileName => FileFor(Id);
    public static string FileFor(string id) => $"incident-{id}.dcx";
    public bool IsActive => Status is IncidentStatus.Open or IncidentStatus.Monitoring;
}

/// <summary>A screenshot or file attached to an incident: its own encrypted file, bound to the incident by id.</summary>
public sealed class IncidentAttachment
{
    public string Id { get; set; } = "";
    public string IncidentId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mime { get; set; } = "application/octet-stream";
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public static string FileFor(string id) => $"incfile-{id}.dcx";
}

/// <summary>
/// Incidents / work sessions (ADR-010). Like notes (ADR-005): one encrypted file per incident (incident-&lt;id&gt;.dcx,
/// AES-256-GCM, file name as AAD, atomic writes), loaded into memory after unlock. Screenshots and files are separate
/// encrypted files written once (incfile-&lt;id&gt;.dcx), so editing the timeline never rewrites megabytes.
/// Trash is a flag; only "Empty trash" deletes files. Damaged files are listed and never overwritten.
/// </summary>
public static class IncidentStore
{
    public const int MaxText = 1_000_000;
    public const long MaxAttachment = 20L << 20;
    public const int MaxItems = 2000;

    private static List<Incident>? _all;
    private static readonly object Gate = new();

    public static List<string> Damaged { get; } = new();

    /// <summary>Raised after any change (the incident, or null for bulk changes).</summary>
    public static event Action<Incident?>? Changed;

    private static List<Incident> Loaded { get { lock (Gate) return _all ??= LoadAll(); } }

    public static IReadOnlyList<Incident> All => Loaded.Where(i => i.Deleted == null).OrderByDescending(i => i.Updated).ToList();
    public static IReadOnlyList<Incident> Trash => Loaded.Where(i => i.Deleted != null).OrderByDescending(i => i.Deleted).ToList();
    public static Incident? Find(string? id) => id == null ? null : Loaded.FirstOrDefault(i => i.Id == id);

    public static void Reset() { lock (Gate) { _all = null; Damaged.Clear(); } }

    private static List<Incident> LoadAll()
    {
        var list = new List<Incident>();
        Damaged.Clear();
        if (!Directory.Exists(SecureStore.DataDir)) return list;
        foreach (var path in Directory.EnumerateFiles(SecureStore.DataDir, "incident-*.dcx"))
        {
            var name = Path.GetFileName(path);
            try
            {
                var i = SecureStore.Read<Incident>(name);
                if (i.FileName != name) throw new InvalidDataException("incident id does not match its file name");
                list.Add(i);
            }
            catch (Exception ex) { Damaged.Add(name); Log.Write("Incident load " + name, ex); }
        }
        return list;
    }

    // ---- the active incident: where "Add to incident" goes without asking ---------------------------------------------

    /// <summary>The incident captures go to (Settings-persisted), if it still exists and is open/monitoring.</summary>
    public static Incident? Active
    {
        get => Find(AppSettings.Current.ActiveIncidentId) is { Deleted: null, IsActive: true } i ? i : null;
        set { AppSettings.Current.ActiveIncidentId = value?.Id; try { AppSettings.Save(); } catch (Exception ex) { Log.Write("Active incident", ex); } Changed?.Invoke(value); }
    }

    // ---- create / save / items ------------------------------------------------------------------------------------

    public static Incident Create(string title, IEnumerable<string>? hostIds = null, IncidentSeverity severity = IncidentSeverity.Sev3, string environment = "")
    {
        var i = new Incident
        {
            Title = title.Trim().Length > 0 ? title.Trim() : $"Incident {DateTime.Now:yyyy-MM-dd HH:mm}",
            HostIds = hostIds?.Distinct().ToList() ?? new(), Severity = severity, Environment = environment,
        };
        i.Items.Add(new IncidentItem { Kind = ItemKind.Status, Title = "Opened", Text = $"Severity {SeverityText(severity)}" + (environment.Length > 0 ? $" · {environment}" : "") });
        lock (Gate) Loaded.Add(i);
        Persist(i);
        AuditLog.Write("you", "incidents", "incident opened", i.Title);
        Changed?.Invoke(i);
        return i;
    }

    public static void Save(Incident i)
    {
        i.Updated = DateTime.Now;
        Persist(i);
        Changed?.Invoke(i);
    }

    private static void Persist(Incident i) => SecureStore.Write(i.FileName, i);

    /// <summary>Changes the status and records it on the timeline.</summary>
    public static void SetStatus(Incident i, IncidentStatus s, string? note = null)
    {
        if (i.Status == s) return;
        var from = i.Status;
        i.Status = s;
        i.Resolved = s is IncidentStatus.Resolved or IncidentStatus.Closed ? i.Resolved ?? DateTime.Now : null;
        i.Items.Add(new IncidentItem { Kind = ItemKind.Status, Title = $"{from} → {s}", Text = note ?? "" });
        Save(i);
        AuditLog.Write("you", "incidents", "incident status changed", $"{i.Title}: {from} → {s}");
    }

    /// <summary>Adds an entry; <paramref name="attachment"/> (screenshot/file bytes) is written to its own encrypted file
    /// first, so the timeline never points at a file that doesn't exist.</summary>
    public static IncidentItem Add(Incident i, IncidentItem item, byte[]? attachment = null, string? attachmentName = null)
    {
        if (i.Deleted != null) throw new InvalidOperationException("The incident is in the Trash.");
        if (i.Items.Count >= MaxItems) throw new InvalidOperationException($"The incident already has {MaxItems} entries — start a follow-up incident.");
        if (item.Text.Length > MaxText) item.Text = item.Text[..MaxText] + $"\n…(cut at {MaxText:N0} characters)";
        if (attachment != null)
        {
            if (attachment.LongLength > MaxAttachment) throw new InvalidOperationException($"Attachments are limited to {MaxAttachment >> 20} MB.");
            var att = new IncidentAttachment { Id = Guid.NewGuid().ToString("N"), IncidentId = i.Id, Name = attachmentName ?? item.Title, Mime = item.Mime ?? "application/octet-stream", Data = attachment };
            SecureStore.Write(IncidentAttachment.FileFor(att.Id), att);
            item.AttachmentId = att.Id; item.Bytes = attachment.LongLength; item.Mime = att.Mime;
        }
        if (item.HostId != null && !i.HostIds.Contains(item.HostId) && AppData.Current.FindHost(item.HostId) is { IsAdHoc: false }) i.HostIds.Add(item.HostId);
        i.Items.Add(item);
        Save(i);
        return item;
    }

    /// <summary>Removes an entry (and its attachment file). The caller confirms with the user.</summary>
    public static void RemoveItem(Incident i, IncidentItem item)
    {
        i.Items.Remove(item);
        Save(i);
        if (item.AttachmentId != null) DeleteAttachment(item.AttachmentId);
        AuditLog.Write("you", "incidents", "timeline entry removed", $"{i.Title}: {item.Kind} {item.Title}");
    }

    public static byte[] ReadAttachment(Incident i, IncidentItem item)
    {
        if (item.AttachmentId is not { } id || !ValidId(id)) throw new InvalidDataException("This entry has no attachment.");
        var a = SecureStore.Read<IncidentAttachment>(IncidentAttachment.FileFor(id));
        if (a.Id != id || a.IncidentId != i.Id) throw new InvalidDataException("The attachment file does not belong to this incident.");
        return a.Data;
    }

    private static void DeleteAttachment(string id)
    {
        if (!ValidId(id)) return;
        try { File.Delete(Path.Combine(SecureStore.DataDir, IncidentAttachment.FileFor(id))); } catch (Exception ex) { Log.Write("Incident attachment delete", ex); }
    }

    public static void Delete(Incident i)
    {
        i.Deleted = DateTime.Now;
        if (Active?.Id == i.Id) Active = null;
        Persist(i); Changed?.Invoke(i);
        AuditLog.Write("you", "incidents", "incident moved to trash", i.Title);
    }

    public static void Restore(Incident i) { i.Deleted = null; Persist(i); Changed?.Invoke(i); }

    /// <summary>Permanently deletes every incident in the Trash with its attachments (the caller confirms).</summary>
    public static int EmptyTrash()
    {
        var gone = Trash.ToList();
        foreach (var i in gone)
        {
            try { File.Delete(Path.Combine(SecureStore.DataDir, i.FileName)); } catch (Exception ex) { Log.Write("Incident purge", ex); continue; }
            foreach (var a in i.Items.Select(x => x.AttachmentId).OfType<string>()) DeleteAttachment(a);
            lock (Gate) Loaded.Remove(i);
        }
        if (gone.Count > 0) { AuditLog.Write("you", "incidents", "trash emptied", $"{gone.Count} incident(s)"); Changed?.Invoke(null); }
        return gone.Count;
    }

    public static IReadOnlyList<Incident> ForHost(string hostId) => All.Where(i => i.HostIds.Contains(hostId)).ToList();

    /// <summary>Opening a session to a host linked to the active incident records it on the timeline.</summary>
    public static void RecordConnection(HostEntry host, string how)
    {
        try
        {
            if (Active is not { } i || host.IsAdHoc || !i.HostIds.Contains(host.Id)) return;
            // Reconnect loops and duplicate tabs shouldn't flood the timeline: one entry per host per 10 minutes.
            if (i.Items.Any(x => x.Kind == ItemKind.Connection && x.HostId == host.Id && DateTime.Now - x.At < TimeSpan.FromMinutes(10))) return;
            Add(i, new IncidentItem { Kind = ItemKind.Connection, Title = $"{how} to {host.Display}", Text = host.Endpoint, HostId = host.Id });
        }
        catch (Exception ex) { Log.Write("Incident connection event", ex); }
    }

    // ---- text for AI / export ----------------------------------------------------------------------------------------

    public static string SeverityText(IncidentSeverity s) => s switch
    {
        IncidentSeverity.Sev1 => "SEV1 (critical)", IncidentSeverity.Sev2 => "SEV2 (major)", IncidentSeverity.Sev3 => "SEV3 (minor)", _ => "SEV4 (low)",
    };

    public static string KindText(ItemKind k) => k switch
    {
        ItemKind.Event => "Event", ItemKind.Terminal => "Terminal", ItemKind.Log => "Log", ItemKind.Screenshot => "Screenshot",
        ItemKind.File => "File", ItemKind.Note => "Note", ItemKind.Report => "Diagnostics", ItemKind.AiAnalysis => "AI analysis",
        ItemKind.Connection => "Connection", _ => "Status",
    };

    public static string Glyph(ItemKind k) => k switch
    {
        ItemKind.Event => "•", ItemKind.Terminal => "⌨", ItemKind.Log => "☰", ItemKind.Screenshot => "📷", ItemKind.File => "⎙",
        ItemKind.Note => "✎", ItemKind.Report => "▤", ItemKind.AiAnalysis => "✦", ItemKind.Connection => "⇄", _ => "◆",
    };

    /// <summary>The incident as Markdown, limited to <paramref name="items"/> (all when null). Screenshots and files are
    /// listed by name only — their content is never part of this text. Linked notes are included by reference text.</summary>
    public static string Markdown(Incident i, IEnumerable<IncidentItem>? items = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {i.Title}");
        sb.AppendLine($"Status: {i.Status} · Severity: {SeverityText(i.Severity)}" + (i.Environment.Length > 0 ? $" · Environment: {i.Environment}" : ""));
        sb.AppendLine($"Opened {i.Created:yyyy-MM-dd HH:mm}" + (i.Resolved is { } r ? $" · resolved {r:yyyy-MM-dd HH:mm} (duration {Duration(r - i.Created)})" : ""));
        var hosts = i.HostIds.Select(AppData.Current.FindHost).Where(h => h != null).Select(h => $"{h!.Display} ({h.Address})").ToList();
        if (hosts.Count > 0) sb.AppendLine("Hosts: " + string.Join(", ", hosts));
        if (i.Summary.Trim().Length > 0) sb.AppendLine().AppendLine("## Summary").AppendLine(i.Summary.Trim());
        sb.AppendLine().AppendLine("## Timeline");
        foreach (var it in (items ?? i.Items).OrderBy(x => x.At))
        {
            var host = it.HostId != null && AppData.Current.FindHost(it.HostId) is { } h ? $" [{h.Display}]" : "";
            sb.AppendLine($"### {it.At:yyyy-MM-dd HH:mm:ss} · {KindText(it.Kind)}{host} · {it.Title}");
            if (it.AttachmentId != null) { sb.AppendLine($"({(it.Kind == ItemKind.Screenshot ? "image" : "file")} “{it.Title}”, {it.Bytes / 1024.0:N0} KB — content not included)"); continue; }
            var text = it.Kind == ItemKind.Note && it.NoteId != null && Notes.NoteStore.Find(it.NoteId) is { Deleted: null } n ? (Locks.IsOpen("note:" + n.Id) ? n.Body : "🔒 (a locked note: unlock it to include its text)") : it.Text;
            if (text.Trim().Length == 0) continue;
            bool code = it.Kind is ItemKind.Terminal or ItemKind.Log && !text.TrimStart().StartsWith("```");
            sb.AppendLine(code ? "```\n" + text.TrimEnd() + "\n```" : text.TrimEnd());
        }
        return sb.ToString();
    }

    public static string Duration(TimeSpan t) => t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Max(0, t.Minutes)} min";

    // ---- backup ------------------------------------------------------------------------------------------------------

    public static bool ValidId(string? id) => id != null && Regex.IsMatch(id, "^[0-9a-f]{32}$");

    /// <summary>Deep copies of every incident (incl. trash) and their attachments, for a backup file.</summary>
    public static (List<Incident> Incidents, List<IncidentAttachment> Attachments) Snapshot()
    {
        var incidents = Loaded.Select(i => System.Text.Json.JsonSerializer.Deserialize<Incident>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(i, SecureStore.Json), SecureStore.Json)!).ToList();
        var atts = new List<IncidentAttachment>();
        foreach (var i in incidents)
            foreach (var it in i.Items.Where(x => x.AttachmentId != null))
            {
                try { var a = SecureStore.Read<IncidentAttachment>(IncidentAttachment.FileFor(it.AttachmentId!)); if (a.IncidentId == i.Id) atts.Add(a); }
                catch (Exception ex) { Log.Write("Incident backup attachment", ex); }
            }
        return (incidents, atts);
    }

    /// <summary>Merge from a backup: incidents with a new id are added with the attachments that belong to them; existing
    /// ids are left as they are. Ids are validated (they become file names).</summary>
    public static int Import(IEnumerable<Incident> incidents, IEnumerable<IncidentAttachment>? attachments)
    {
        var atts = (attachments ?? Enumerable.Empty<IncidentAttachment>()).Where(a => ValidId(a.Id) && ValidId(a.IncidentId)).GroupBy(a => a.IncidentId).ToDictionary(g => g.Key, g => g.ToList());
        int added = 0;
        foreach (var i in incidents.Where(i => ValidId(i.Id) && Find(i.Id) == null))
        {
            var mine = atts.GetValueOrDefault(i.Id) ?? new();
            foreach (var it in i.Items)
            {
                if (it.AttachmentId == null) continue;
                var a = mine.FirstOrDefault(x => x.Id == it.AttachmentId);
                if (a == null || File.Exists(Path.Combine(SecureStore.DataDir, IncidentAttachment.FileFor(a.Id)))) { if (a == null) { it.AttachmentId = null; it.Title += " (attachment not in the backup)"; } continue; }
                SecureStore.Write(IncidentAttachment.FileFor(a.Id), a);
            }
            lock (Gate) Loaded.Add(i);
            Persist(i);
            added++;
        }
        if (added > 0) Changed?.Invoke(null);
        return added;
    }
}
