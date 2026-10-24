namespace DcxManager.Services;

/// <summary>
/// One run of a toolbox check (0.3.0 §18), kept in the encrypted store so the last results of a target can be compared
/// and copied as a report later: which tool, the target, a one-line summary and the text report (capped). At most
/// <see cref="Max"/> runs are kept, newest first; nothing is kept for the pasted email headers tool unless asked.
/// </summary>
public sealed class ToolboxRun
{
    public const int Max = 200;
    public const int MaxReport = 32 * 1024;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime When { get; set; } = DateTime.Now;
    /// <summary>"TCP port", "Traceroute", "DNS", "SPF", "Blacklists", "WHOIS"…</summary>
    public string Tool { get; set; } = "";
    public string Target { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Report { get; set; } = "";

    /// <summary>Adds a run to the store (trimmed, newest first) and saves.</summary>
    public static void Record(string tool, string target, string summary, string report)
    {
        var run = new ToolboxRun { Tool = tool, Target = target, Summary = Trim(summary, 300), Report = Trim(report, MaxReport) };
        var list = AppData.Current.ToolboxHistory;
        list.Insert(0, run);
        if (list.Count > Max) list.RemoveRange(Max, list.Count - Max);
        AppData.Save();
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public string Line => $"{When:yyyy-MM-dd HH:mm}  {Tool}  {Target}  —  {Summary}";
}
