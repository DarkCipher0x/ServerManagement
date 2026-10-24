namespace DcxManager;

/// <summary>
/// What the full self-test's areas share (0.2.0 phase 6½, when RunFullAsync was split into one method per area): the main
/// window, the report path, how to record a result, how to let the UI settle, and the demo host the screens area adds and
/// the UI area renders with. The log and the failure count stay in RunFullAsync, which writes the report.
/// </summary>
internal sealed class SelfTestRun(MainWindow main, string reportPath, Action<string> ok, Action<string, Exception?> fail, SelfTestRun.SettleFn settle)
{
    /// <summary>Waits, then lets the UI finish layout (default 600 ms).</summary>
    public delegate Task SettleFn(int ms = 600);

    public MainWindow Main { get; } = main;
    public string ReportPath { get; } = reportPath;
    public Action<string> Ok { get; } = ok;
    public Action<string, Exception?> Fail { get; } = fail;
    public SettleFn Settle { get; } = settle;
    public Services.HostEntry? TestHost { get; set; }
}
