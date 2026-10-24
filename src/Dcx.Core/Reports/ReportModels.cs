using DcxManager.Services;

namespace DcxManager.Reports;

// Stored records (0.2.0 phase 6): kept in the encrypted store, so they live in Dcx.Core with the store; the code that uses them stays here.

/// <summary>Saved reports in the encrypted store (reports.dcx), newest first, at most 200. <c>ReportEngine</c>'s
/// Load / Save / Delete forward here; backups read and write it directly.</summary>
public static class ReportStore
{
    public const string FileName = "reports.dcx";
    private const int MaxStored = 200;

    public static List<ServerReport> Load()
    {
        try { return SecureStore.Read<List<ServerReport>>(FileName); } catch (Exception ex) { Log.Write("Reports load", ex); return new(); }
    }

    public static void Save(ServerReport r)
    {
        var all = Load();
        all.RemoveAll(x => x.Id == r.Id);
        all.Insert(0, r);
        if (all.Count > MaxStored) all.RemoveRange(MaxStored, all.Count - MaxStored);
        SecureStore.Write(FileName, all);
    }

    public static void Delete(string id)
    {
        var all = Load();
        if (all.RemoveAll(x => x.Id == id) > 0) SecureStore.Write(FileName, all);
    }
}

public sealed class Finding
{
    public string Host { get; set; } = "";
    public string Severity { get; set; } = "info";   // critical | high | medium | low | info
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Recommendation { get; set; } = "";
    public List<string> Commands { get; set; } = new();
    public string Source { get; set; } = "rule";      // rule | ai
}

public sealed class EvidenceSection
{
    public string Host { get; set; } = "";
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Command { get; set; } = "";
    public string Digest { get; set; } = "";
    public int RawLines { get; set; }
    public string Error { get; set; } = "";
}

public sealed class ServerReport
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public List<string> Hosts { get; set; } = new();
    public string Window { get; set; } = "24 hours ago";
    public string Health { get; set; } = "ok";        // ok | degraded | critical | unknown
    public string Summary { get; set; } = "";
    public List<Finding> Findings { get; set; } = new();
    public List<string> NextSteps { get; set; } = new();
    public List<EvidenceSection> Evidence { get; set; } = new();
    public string Engine { get; set; } = "rules (offline)";
    public bool AiUsed { get; set; }
    public string AiNote { get; set; } = "";
    public string Notes { get; set; } = "";
}
