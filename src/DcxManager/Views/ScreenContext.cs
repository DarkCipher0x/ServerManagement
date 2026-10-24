using System.Text;
using DcxManager.Cloud;
using DcxManager.Services;
using DcxManager.Sessions;

namespace DcxManager.Views;

/// <summary>What the user is looking at (ADR-028; the "current_context" capability, used by the AI and add-ons; was Ai\AiSelection.cs): screen, host, terminal target, cloud provider/account,
/// infrastructure provider. Names and kinds only — never contents, credentials or output.</summary>
public static class ScreenContext
{
    public static string Describe()
    {
        var main = MainWindow.Instance;
        var sb = new StringBuilder();
        var tab = main?.Panes.ActiveTab;
        sb.AppendLine($"Screen in front: {(tab?.Header as string) ?? "(none)"}");
        switch (tab?.Content)
        {
            case TerminalView tv: sb.AppendLine($"Terminal session {tv.SessionId}: {tv.Target.Title} · {tv.Target.Endpoint} · {(tv.IsConnected ? "connected" : "disconnected")} · environment {EnvUi.ForView(tv)}"); break;
            case CloudWorkspaceView w when w.Provider is { } p: sb.AppendLine($"Cloud provider: {p.Title} · account {p.ActiveAccount?.Name ?? "(none)"} · screen {w.Shown}"); break;
            case IHostView hv when hv.Host is { } h: sb.AppendLine($"Host: {h.Display} ({h.Endpoint}) · environment {Envs.For(h)}"); break;
        }
        if (InfraView.Current is { } iv && iv.SelectedProviderName is { } ip) sb.AppendLine($"Infrastructure screen open on provider “{ip}”{(iv.SelectedObjectNames.Count > 0 ? ", selected: " + string.Join(", ", iv.SelectedObjectNames.Take(10)) : "")}");
        sb.AppendLine(TerminalBridge.Target is { } t ? $"AI terminal target (chosen by the user): {t.Describe()}" : "AI terminal target: none chosen");
        var clouds = CloudProviders.All.Where(p => p.ActiveAccount != null).Select(p => $"{p.Id} ({p.ActiveAccount!.Name})").ToList();
        sb.AppendLine("Connected cloud accounts: " + (clouds.Count > 0 ? string.Join(", ", clouds) : "none"));
        sb.AppendLine($"Infrastructure providers: {(AppData.Current.InfraProviders.Count > 0 ? string.Join(", ", AppData.Current.InfraProviders.Select(p => p.Name)) : "none")}");
        return sb.ToString().TrimEnd();
    }
}
