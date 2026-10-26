using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Sessions;

namespace DcxManager.Views;

// TerminalView: keyword highlighting, Ctrl+click words and the snippet button bar (0.6.29, ADR-104).
public sealed partial class TerminalView
{
    private readonly WrapPanel _snipBar = new() { Margin = new Thickness(8, 0, 8, 4), Visibility = Visibility.Collapsed };
    private string _hlSent = "";
    private string _barSig = "";

    /// <summary>Self-test: the snippets the bar shows now, and answers for their questions instead of asking.</summary>
    internal IReadOnlyList<Snippet> BarSnippets { get; private set; } = Array.Empty<Snippet>();
    internal static Func<SnippetVariables.Question, string?>? AnswerForTest;
    internal string LastSnippetSent { get; private set; } = "";

    private SnippetVariables.Context SnippetContext() =>
        new(_target.Host, Shell, EnvUi.ForView(this), _target.Kind is "local" or "wsl");

    // ---- highlighting ------------------------------------------------------------------------------------------------

    private void SendHighlighting(bool force = false)
    {
        var sig = Highlighting.Signature(AppSettings.Current);
        if (!force && sig == _hlSent) return;
        _hlSent = sig;
        Post(Highlighting.PageMessage(AppSettings.Current));
    }

    /// <summary>Self-test: how many highlight marks the page holds now, and how many rules it compiled.</summary>
    internal async Task<(int Marks, int Rules, bool On)?> HighlightProbeAsync()
    {
        var id = ++_waitSeq;
        if (await AskPageAsync(new { t = "hlprobe", id }, id) is not JsonElement e) return null;
        return (e.GetProperty("marks").GetInt32(), e.GetProperty("rules").GetInt32(), e.GetProperty("on").GetBoolean());
    }

    private void OnTerminalSettings()
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                if (_pageReady) SendHighlighting();
                BuildButtonBar();
            });
        }
        catch { }
    }

    private void OnTerminalData()
    {
        try { Dispatcher.BeginInvoke(() => { if (!_disposed) BuildButtonBar(); }); } catch { }
    }

    /// <summary>A highlight rule marked "alert" matched in this tab's output.</summary>
    private void OnHighlightAlert(JsonElement m)
    {
        if (!m.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return;
        foreach (var a in list.EnumerateArray().Take(5))
        {
            var id = a.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            var rule = AppSettings.Current.TerminalHighlightRules.FirstOrDefault(r => r.Id == id && r.Alert);
            if (rule == null) continue;
            var line = a.TryGetProperty("d", out var d) ? d.GetString() ?? "" : "";
            if (Alert("match:" + id, $"“{(rule.Name.Length > 0 ? rule.Name : rule.Pattern)}” · {_target.Title}", line.Length > 160 ? line[..160] + "…" : line)) break;
        }
    }

    /// <summary>A Ctrl+clicked word in the output: offer what can be done with it (nothing happens without a click here).</summary>
    private void OnWordClicked(string? word)
    {
        if (Highlighting.Classify(word) is not { } w) return;
        object?[] items = w.Kind switch
        {
            "url" => new object?[] { Menus.Item("Open in browser…", () => OpenLink(w.Value)), Menus.Item("Copy link", () => Menus.Clip(w.Value)) },
            "ip" => new object?[]
            {
                Menus.Item($"SSH to {w.Value}{(w.Port > 0 ? ":" + w.Port : "")}", () => MainWindow.Instance.QuickConnectTo("ssh", w.Port > 0 ? $"{w.Value}:{w.Port}" : w.Value)),
                Menus.Item("Add as a host…", () => AddHostFrom(w.Value, w.Port)),
                Menus.Item("Copy", () => Menus.Clip(w.Port > 0 ? $"{w.Value}:{w.Port}" : w.Value)),
            },
            _ => new object?[]
            {
                _target.Has(SessionCaps.HostTools) && _target.Host is { } h ? Menus.Item("Open in Files", () => MainWindow.Instance.OpenFiles(h, w.Value.EndsWith('/') ? w.Value : System.IO.Path.GetDirectoryName(w.Value.Replace('/', '\\'))?.Replace('\\', '/') ?? "/")) : null,
                Menus.Item("Copy path", () => Menus.Clip(w.Value)),
                IsConnected ? Menus.Item("Paste at the prompt", () => PasteText(SnippetVariables.Quote(w.Value, Shell))) : null,
            },
        };
        Menus.ShowAt(_web, items);
    }

    private void AddHostFrom(string ip, int port)
    {
        var seed = new HostEntry { Name = ip, Address = ip, Port = port > 0 ? port : 22 };
        var w = new HostEditWindow(seed, seedIsNew: true) { Owner = Window.GetWindow(this) };
        if (w.ShowDialog() != true || w.Result is not { } h) return;
        AppData.Current.Hosts.Add(h);
        AppData.Save();
        AuditLog.Write("you", h.Display, "added host from terminal output");
    }

    // ---- snippet button bar -----------------------------------------------------------------------------------------

    /// <summary>The bar: snippets marked "button bar" whose scope fits this terminal. Rebuilt when snippets or settings change.</summary>
    internal void BuildButtonBar()
    {
        var c = SnippetContext();
        var list = AppSettings.Current.TerminalButtonBar
            ? AppData.Current.Snippets.Where(s => s.Bar && s.Command.Length > 0 && SnippetVariables.InScope(s, c)).Take(40).ToList()
            : new List<Snippet>();
        var sig = string.Join("|", list.Select(s => s.Id + s.Name + s.Command + s.Sudo));
        BarSnippets = list;
        if (sig == _barSig) return;
        _barSig = sig;
        _snipBar.Children.Clear();
        foreach (var s in list)
        {
            var b = new Button { Content = s.Name.Length > 0 ? s.Name : s.Command, ToolTip = Tip(s), Style = (Style)Application.Current.Resources["SmallButton"], Margin = new Thickness(0, 0, 4, 0) };
            b.Click += async (_, _) => await SendSnippetAsync(s, new[] { this });
            Menus.Attach(b, () => SendToItems(s));
            _snipBar.Children.Add(b);
        }
        _snipBar.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Tip(Snippet s) =>
        (s.Description.Length > 0 ? s.Description + "\n" : "") + (s.Sudo ? "sudo " : "") + s.Command + "\n\nRight-click: send to the group, the pane or every terminal";

    private IEnumerable<object?> SendToItems(Snippet s)
    {
        var tab = MainWindow.Instance.Panes.AllTabs.FirstOrDefault(t => t.Content == this);
        yield return Menus.Item("Send here", () => _ = SendSnippetAsync(s, new[] { this }));
        if (tab != null && TabGroups.GetName(tab) is { Length: > 0 } g)
            yield return Menus.Item($"Send to group “{g}”", () => _ = SendSnippetAsync(s, TabGroups.Members(g).Select(t => t.Content).OfType<TerminalView>().ToList()));
        if (tab != null && MainWindow.Instance.Panes.PaneOf(tab) is { } pane)
            yield return Menus.Item("Send to this pane's terminals", () => _ = SendSnippetAsync(s, pane.Items.OfType<TabItem>().Select(t => t.Content).OfType<TerminalView>().ToList()));
        yield return Menus.Item("Send to every open terminal", () => _ = SendSnippetAsync(s, AllTerminals().ToList()));
        yield return Menus.Sep();
        yield return Menus.Item("Edit snippets…", () => MainWindow.Instance.Navigate("snippets"));
    }

    /// <summary>Asks the snippet's questions once (here), fills each terminal's own host values in, shows risky commands
    /// and PRODUCTION targets first, then types it into each live terminal. Also used by the Snippets menu.</summary>
    internal async Task<int> SendSnippetAsync(Snippet s, IReadOnlyList<TerminalView> views)
    {
        var live = views.Where(v => v.IsConnected && !v._disposed).Distinct().ToList();
        if (live.Count == 0) { SetStatus($"{_target.Endpoint} · not connected"); return 0; }
        var answers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in SnippetVariables.Questions(s.Command))
        {
            var a = await AnswerAsync(q);
            if (a == null) { SetStatus($"{_target.Endpoint} · snippet cancelled"); return 0; }
            answers[q.Label] = a;
        }
        var sends = new List<(TerminalView View, string Text)>();
        foreach (var v in live)
        {
            var text = SnippetVariables.Expand(s.Command, v.SnippetContext(), q => answers.GetValueOrDefault(q.Label, ""));
            if (text == null) return 0;
            bool sudo = s.Sudo && v._target.Has(SessionCaps.Sudo) && v._target.Host?.Username != "root";
            sends.Add((v, (sudo ? "sudo " : "") + text));
        }
        var risks = sends.SelectMany(x => PasteGuard.Check(x.Text).Risks).Distinct().ToList();
        var prod = live.Where(v => Envs.IsProduction(EnvUi.ForView(v))).Select(v => v._target.Title).ToList();
        if (risks.Count > 0 || prod.Count > 0 || live.Count > 1)
        {
            var msg = (prod.Count > 0 ? $"⚠ PRODUCTION: {string.Join(", ", prod.Take(6))}{(prod.Count > 6 ? $" and {prod.Count - 6} more" : "")}\n\n" : "")
                + $"Send to {live.Count} terminal(s):\n\n  {sends[0].Text}" + (sends.Select(x => x.Text).Distinct().Count() > 1 ? "\n  (host values differ per terminal)" : "")
                + string.Concat(risks.Select(r => "\n\n⚠ Looks risky: " + r + "."));
            if (!Dialogs.Confirm("Send snippet?", msg, "Send", danger: risks.Count > 0 || prod.Count > 0)) { SetStatus($"{_target.Endpoint} · snippet cancelled"); return 0; }
        }
        foreach (var (v, text) in sends) { v.SendInput(text + "\r"); v.LastSnippetSent = text; }
        AuditLog.Write("you", _target.Title, $"sent snippet “{s.Name}”", live.Count > 1 ? $"ok, to {live.Count} terminals" : "ok");
        return sends.Count;
    }

    private async Task<string?> AnswerAsync(SnippetVariables.Question q)
    {
        if (AnswerForTest is { } t) return t(q);
        if (q.PickCommand is { } cmd && _target.Has(SessionCaps.HostTools) && _target.Host is { } h)
        {
            SetStatus($"● {_target.Endpoint} · listing choices for “{q.Label}”…");
            try
            {
                var r = await SshPool.RunAsync(h, cmd, timeout: TimeSpan.FromSeconds(20));
                var choices = SnippetVariables.Choices(r.Output);
                if (choices.Count > 0) return Dialogs.Pick(q.Label, $"Choose {q.Label} (from: {cmd})", choices, x => x);
                SetStatus($"● {_target.Endpoint} · “{cmd}” listed nothing: type it instead");
            }
            catch (Exception ex) { SetStatus($"● {_target.Endpoint} · couldn't list choices: {ex.Message}"); }
        }
        return Dialogs.Prompt(q.Label, q.PickCommand != null ? $"{q.Label}:" : $"{q.Label} (it's quoted for the shell, one line):", q.Default);
    }
}
