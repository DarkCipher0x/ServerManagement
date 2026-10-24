using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using DcxManager.Mail;
using DcxManager.Services;

namespace DcxManager.Views.Toolbox;

/// <summary>
/// Toolbox → Headers: paste an email's headers (Outlook: File → Properties → Internet headers; Gmail: Show original) and
/// see the route with the delay at each hop, the SPF / DKIM / DMARC verdicts the receiver recorded, alignment, and the
/// warning signs. Everything is parsed on this PC; the text is never sent or stored unless you choose "Keep in history".
/// </summary>
public sealed class HeadersTab : DockPanel
{
    private readonly TextBox _input = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 120 };
    private readonly CheckBox _keep = new() { Content = "Keep the result in history", ToolTip = "Off by default: headers can carry addresses and names" };
    private readonly TextBlock _status = ToolboxUi.Status();
    private readonly ObservableCollection<MailFinding> _findings = new();
    private readonly TextBox _out = ToolboxUi.Output();

    public HeadersTab()
    {
        _input.Mono();
        var head = new StackPanel();
        head.Children.Add(ToolboxUi.Hint("Paste the full headers below. Nothing is looked up or sent — the analysis is local. Received hops are read oldest-first; a long delay points at the server that queued the message."));
        var clear = ToolboxUi.Button("✕ Clear"); clear.Click += (_, _) => { _input.Clear(); _findings.Clear(); _out.Clear(); _status.Text = ""; };
        var paste = ToolboxUi.Button("📋 Paste"); paste.Click += (_, _) => { try { if (Clipboard.ContainsText()) _input.Text = Clipboard.GetText(); } catch { } };   // the clipboard can be held by another program
        var analyze = ToolboxUi.Button("⌕ Analyse", primary: true); analyze.Click += (_, _) => Analyze();
        head.Children.Add(ToolboxUi.Bar(paste, analyze, clear, _keep));
        head.Children.Add(_status);
        SetDock(head, Dock.Top);
        Children.Add(head);
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var list = ToolboxUi.Findings(_findings, this);
        Grid.SetRow(_input, 0); Grid.SetRow(list, 1); Grid.SetRow(_out, 2);
        list.Margin = new Thickness(0, 8, 0, 0); _out.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(_input); body.Children.Add(list); body.Children.Add(_out);
        Children.Add(body);
    }

    public HeaderAnalyzer.Report Analyze()
    {
        var r = HeaderAnalyzer.Analyze(_input.Text);
        _findings.Clear();
        foreach (var f in r.Findings) _findings.Add(f);
        var sb = new StringBuilder();
        sb.AppendLine($"From domain: {(r.FromDomain.Length > 0 ? r.FromDomain : "—")}   DKIM signatures: {(r.DkimSignatures.Count == 0 ? "none" : string.Join(", ", r.DkimSignatures.Select(s => $"d={s.Domain} s={s.Selector}")))}");
        sb.AppendLine($"Route: {r.Hops.Count} hop(s){(r.Total is { } t ? $", {HeaderAnalyzer.Fmt(t)} end to end" : "")}");
        foreach (var h in r.Hops) sb.AppendLine($"  {h.Index,2}. {(h.Delay is { } d ? $"+{HeaderAnalyzer.Fmt(d),-7}" : "        ")} {(h.When is { } w ? w.ToString("yyyy-MM-dd HH:mm:ss zzz") : "(no date)")}  from {Trunc(h.From)}  by {Trunc(h.By)}  {h.With}");
        if (r.Auth.Count > 0) { sb.AppendLine("Authentication results:"); foreach (var a in r.Auth) sb.AppendLine($"  {a.Method,-8} {a.Result,-9} {a.Detail}"); }
        sb.AppendLine($"Headers parsed: {r.Headers.Count}");
        _out.Text = Redaction.Mask(sb.ToString());
        _status.Text = r.Headers.Count == 0 ? "Nothing that looks like headers was pasted." : $"{r.Hops.Count} hop(s) · {ToolboxUi.Verdict(_findings)}.";
        if (_keep.IsChecked == true && r.Headers.Count > 0) ToolboxUi.Record("Email headers", r.FromDomain.Length > 0 ? "from " + r.FromDomain : "(no From)", _status.Text, ToolboxUi.Report(_findings) + "\n\n" + _out.Text);
        return r;
    }

    private static string Trunc(string s) => s.Length > 48 ? s[..48] + "…" : s;
    internal void SetForTest(string text) => _input.Text = text;
    internal IReadOnlyList<MailFinding> FindingsForTest => _findings;
}
