using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DcxManager.Ai;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>Chat with the local AI operator. Read-only lookups run automatically (configurable); every change asks first.</summary>
public sealed class AssistantView : UserControl, IDisposable
{
    public static AssistantView? Current { get; private set; }

    private readonly Assistant _ai = new();
    private readonly StackPanel _chat = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, MaxHeight = 180, Padding = new Thickness(8, 6, 8, 6), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button _send = new() { Content = "Send", MinWidth = 90 };
    private readonly TextBlock _status = new TextBlock() { VerticalAlignment = VerticalAlignment.Center }.Fs(FontStep.Small);
    private CancellationTokenSource? _cts;
    private readonly ComboBox _model = new() { IsEditable = true, MinWidth = 150, MaxWidth = 240, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Model for this chat (of the current engine). Empty = the default from Settings." };
    // The answer streams into the "thinking" bubble; updates are coalesced (one pending BeginInvoke at a time).
    private TextBox? _liveBox;
    private readonly System.Text.StringBuilder _live = new();
    private int _livePending;

    public AssistantView()
    {
        Current = this;
        _send.Style = (Style)FindResource("PrimaryButton");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _ai.Approve = ApproveAsync;
        // Background threads: BeginInvoke, never a blocking Invoke per line (ADR-034).
        _ai.Line += l => Dispatcher.BeginInvoke(() => { if (!_life.IsCancellationRequested) AddBeforeLive(l); });
        _ai.Step += st => { if (st.Kind is "plan" or "note") Dispatcher.BeginInvoke(() => { if (!_life.IsCancellationRequested) AddBeforeLive(new ChatLine("tool", st.ToString())); }); };
        _ai.Round += _ => { lock (_live) _live.Clear(); QueueLive(); };
        _ai.Delta += d => { lock (_live) _live.Append(d); QueueLive(); };

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(tools, Dock.Right);
        void B(string t, RoutedEventHandler h, string tip) { var b = new Button { Content = t, ToolTip = tip, Style = (Style)FindResource("SmallButton") }; b.Click += h; tools.Children.Add(b); }
        B("New chat", (_, _) => { _ai.Reset(); _chat.Children.Clear(); Intro(); }, "Forget this conversation");
        B("■ Stop", (_, _) => _cts?.Cancel(), "Cancel the current answer");
        B("⚙", (_, _) => Ui.Window.Navigate("settings"), "Model, server address, SSH tunnel to a GPU box");
        tools.Children.Insert(0, _model);
        _model.Text = $"(default) {AiClient.DefaultModel}";
        _model.DropDownOpened += async (_, _) =>
        {
            List<string> models;
            try { using var c = new CancellationTokenSource(4000); models = await AiClient.ModelsAsync(c.Token); } catch { models = new(); }
            var keep = _ai.Model;
            _model.Items.Clear();
            _model.Items.Add(new ComboBoxItem { Content = $"(default) {AiClient.DefaultModel}", Tag = null });
            foreach (var m in models.Where(m => m != AiClient.DefaultModel).Take(60)) _model.Items.Add(new ComboBoxItem { Content = m, Tag = m });
            _model.Text = keep ?? $"(default) {AiClient.DefaultModel}";
        };
        _model.SelectionChanged += (_, _) => { if (_model.SelectedItem is ComboBoxItem { Tag: var t }) _ai.Model = t as string; };
        _model.LostKeyboardFocus += (_, _) => _ai.Model = _model.Text.StartsWith("(default") || _model.Text.Trim().Length == 0 ? null : _model.Text.Trim();
        System.Windows.Automation.AutomationProperties.SetName(_model, "Model for this chat");
        head.Children.Add(tools);
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = "AI operator", Style = (Style)FindResource("H1") });
        title.Children.Add(_status);
        head.Children.Add(title);

        var suggestions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var s in new[]
        {
            "Give me a health summary of all my servers",
            "Which of my Vultr instances are stopped, and what do they cost?",
            "Why is nginx failing on web1?",
            "Check disk usage everywhere and tell me what's nearly full",
            "Show failed SSH logins in the last day on my servers",
        })
        {
            var b = new Button { Content = s, Style = (Style)FindResource("SmallButton"), Margin = new Thickness(0, 0, 6, 6) };
            b.Click += (_, _) => { _input.Text = s; _input.Focus(); _input.CaretIndex = s.Length; };
            suggestions.Children.Add(b);
        }

        var inputRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        _send.Margin = new Thickness(8, 0, 0, 0);
        _send.VerticalAlignment = VerticalAlignment.Bottom;
        DockPanel.SetDock(_send, Dock.Right);
        inputRow.Children.Add(_send);
        inputRow.Children.Add(_input);
        _send.Click += async (_, _) => await SendAsync();
        _input.TextChanged += (_, _) => { if (_input.Text.Length == 1) AiClient.Warm(); };
        _input.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; await SendAsync(); }
        };

        _scroll.Content = _chat;
        var chatBorder = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(12, 8, 12, 8), Child = _scroll };
        chatBorder.SetResourceReference(Border.BorderBrushProperty, "Line");
        chatBorder.SetResourceReference(Border.BackgroundProperty, "Input");

        var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
        DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(inputRow, Dock.Bottom); DockPanel.SetDock(suggestions, Dock.Bottom);
        root.Children.Add(head); root.Children.Add(inputRow); root.Children.Add(suggestions); root.Children.Add(chatBorder);
        Content = root;
        Intro();
        // Not awaited here: an async handler would itself hold the view until the check ends.
        Loaded += (_, _) => _ = CheckAsync(new WeakReference<AssistantView>(this), _life.Token);
    }

    /// <summary>Cancelled when the view closes: its background engine check must not outlive it.</summary>
    private readonly CancellationTokenSource _life = new();

    private void Intro() => Add(new ChatLine("assistant",
        "I can look at your saved servers and cloud accounts (Vultr, Contabo, Hetzner) and infrastructure providers and help you troubleshoot or make changes. " +
        "I'll read status, logs and the Vultr API on my own" + (AppSettings.Current.AiAutoApproveReadOnly ? "" : " (after you approve)") +
        ", and I'll always ask before running commands, restarting services or powering servers. Everything I do is in the audit log."));

    /// <summary>The engine check runs while the screen is open. Static, holding the view only weakly: a slow engine
    /// (a CLI status call, a 4 s timeout, then another) must not keep a closed screen in memory (s26 leak flake, ADR-025).</summary>
    private static async Task CheckAsync(WeakReference<AssistantView> me, CancellationToken life)
    {
        void Show(string s) { if (!life.IsCancellationRequested && me.TryGetTarget(out var v)) v._status.Text = s; }
        if (AiClient.Blocked(AiFeature.Operator) is { } why) { Show("○ " + why); return; }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(life);
            cts.CancelAfter(4000);
            var models = await AiClient.ModelsAsync(cts.Token);
            if (life.IsCancellationRequested) return;
            var m = AiClient.Model;
            var desc = await AiClient.DescribeAsync();
            Show(models.Count == 0 || models.Contains(m) || models.Any(x => x.StartsWith(m + ":")) ? "● " + desc
                : AiClient.Engine.StartsWith("ollama") ? $"⚠ model {m} not installed — run: ollama pull {m}" : $"⚠ model {m} not listed by the server · {desc}");
        }
        catch (Exception ex) { Show("○ AI engine not reachable: " + ex.Message.Split('\n')[0]); }
    }

    /// <summary>Used by other views ("Ask AI", "Diagnose") to start a question.</summary>
    public void AskAbout(string text)
    {
        _input.Text = text;
        _ = SendAsync();
    }

    private async Task SendAsync()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0 || _cts != null) return;
        _input.Clear();
        Add(new ChatLine("you", text));
        var thinking = Add(new ChatLine("assistant", "…thinking"));
        _liveBox = (thinking as Border)?.Child is StackPanel sp && sp.Children.Count > 1 ? sp.Children[1] as TextBox : null;
        lock (_live) _live.Clear();
        _cts = new CancellationTokenSource();
        _send.IsEnabled = false;
        try
        {
            var answer = await _ai.AskAsync(text, _cts.Token);
            _liveBox = null;
            _chat.Children.Remove(thinking);
            Add(new ChatLine("assistant", answer.Length > 0 ? answer : "(no answer)"));
            if (_ai.LastRun is { } run)
                _status.Text = $"● {AiClient.Model}{(_ai.Model != null ? " (this chat)" : "")} · first text after {run.FirstToken.TotalSeconds:0.0} s · {run.Total.TotalSeconds:0.0} s in total · {run.Steps} model call(s), {run.ToolCalls} tool call(s){(run.NativeTools ? "" : " · text tool protocol")}";
        }
        catch (OperationCanceledException) { _chat.Children.Remove(thinking); Add(new ChatLine("assistant", "(stopped)")); }
        catch (Exception ex) { _chat.Children.Remove(thinking); Add(new ChatLine("assistant", "✕ " + ex.Message)); }
        finally { _cts = null; _liveBox = null; _send.IsEnabled = true; _input.Focus(); }
    }

    private void QueueLive()
    {
        if (Interlocked.Exchange(ref _livePending, 1) == 1) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _livePending, 0);
            if (_liveBox == null || _life.IsCancellationRequested) return;
            string t; lock (_live) t = _live.ToString();
            _liveBox.Text = t.Length > 0 ? t : "…thinking";
            _scroll.ScrollToEnd();
        });
    }

    internal string LiveTextForTest => _liveBox?.Text ?? "";

    /// <summary>Tool and step lines go above the bubble the answer streams into.</summary>
    private void AddBeforeLive(ChatLine l)
    {
        var b = Add(l);
        if (_liveBox != null && _chat.Children.Count >= 2)
        {
            var live = _chat.Children.OfType<FrameworkElement>().FirstOrDefault(x => x is Border { Child: StackPanel sp } && sp.Children.Count > 1 && sp.Children[1] == _liveBox);
            if (live != null) { _chat.Children.Remove(live); _chat.Children.Add(live); }
        }
    }

    private FrameworkElement Add(ChatLine l)
    {
        var text = new TextBox
        {
            Text = l.Text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap,
        }.Fs(l.IsTool ? FontStep.Small : FontStep.Base).Use(l.IsTool ? "MonoFont" : "UiFont");
        text.SetResourceReference(TextBox.ForegroundProperty, l.IsTool ? "Muted" : "Fg");
        var who = new TextBlock { Text = l.IsUser ? "you" : l.IsTool ? "⚙ tool" : "✦ operator", Style = (Style)FindResource("Label") };
        if (!l.IsUser) who.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var panel = new StackPanel();
        panel.Children.Add(who); panel.Children.Add(text);
        var bubble = new Border
        {
            Child = panel, Padding = new Thickness(10, 6, 10, 8), Margin = new Thickness(l.IsUser ? 80 : 0, 4, l.IsUser ? 0 : 80, 4),
            CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(l.IsUser ? 0 : 2, 0, 0, 0),
        };
        bubble.SetResourceReference(Border.BackgroundProperty, l.IsUser ? "Selection" : "Surface");
        bubble.SetResourceReference(Border.BorderBrushProperty, l.IsTool ? "Line" : "Accent");
        Menus.ForText(text, sel => new object?[]
        {
            Menus.Item("Copy message", () => Menus.Clip(text.Text)),
            l.IsUser ? Menus.Item("Edit and resend", () => { _input.Text = text.Text; _input.Focus(); _input.CaretIndex = _input.Text.Length; }) : null,
            !l.IsUser && !l.IsTool ? Menus.Item("Ask a follow-up about this", () => { _input.Text = $"About your message \"{Trim(text.Text)}\": "; _input.Focus(); _input.CaretIndex = _input.Text.Length; }) : null,
            sel.Length > 0 && !l.IsUser ? Menus.Item("Run selection as a command (asks first)…", () => RunSelection(sel)) : null,
            Menus.Sep(),
            Menus.Item("Copy whole conversation", () => Menus.Clip(Transcript())),
            Menus.Item("Save conversation…", () => Menus.SaveText(Transcript(), $"ai-chat-{DateTime.Now:yyyyMMdd-HHmm}.md", "Markdown|*.md|Text|*.txt")),
            Menus.Item("Share conversation…", () => ShareService.ShareText(Window.GetWindow(this), "AI operator conversation", Transcript(), $"ai-chat-{DateTime.Now:yyyyMMdd-HHmm}.md"), enabled: ShareService.Supported),
        });
        _chat.Children.Add(bubble);
        _scroll.ScrollToEnd();
        return bubble;
    }

    private static string Trim(string s) { s = s.Replace('\n', ' ').Trim(); return s.Length > 60 ? s[..60] + "…" : s; }

    /// <summary>The visible chat as Markdown (rebuilt from the bubbles, so it matches exactly what is on screen).</summary>
    private string Transcript()
    {
        var sb = new System.Text.StringBuilder($"# AI operator — {DateTime.Now:yyyy-MM-dd HH:mm}\n\n");
        foreach (var b in _chat.Children.OfType<Border>())
        {
            if (b.Child is not StackPanel p || p.Children.Count < 2 || p.Children[0] is not TextBlock who || p.Children[1] is not TextBox body) continue;
            bool tool = who.Text.StartsWith('⚙');
            sb.Append("**").Append(who.Text).Append("**\n\n");
            if (tool) sb.Append("```\n").Append(body.Text.TrimEnd()).Append("\n```\n\n");
            else sb.Append(body.Text.TrimEnd()).Append("\n\n");
        }
        return sb.ToString();
    }

    /// <summary>Runs text the AI suggested in a terminal of a host you choose — never without the pick and the confirmation.</summary>
    private void RunSelection(string command)
    {
        command = command.Trim();
        if (command.Length == 0 || command.Contains('\n') && !Dialogs.Confirm("Run selection", "The selection has several lines; they will be sent one after another. Continue?")) return;
        var hosts = AppData.Current.Hosts.Where(h => h.Protocol != "rdp").OrderBy(h => h.Display).ToList();
        if (hosts.Count == 0) { Dialogs.Info("Run selection", "Add a host first."); return; }
        if (Dialogs.Pick("Run on which host?", "A terminal opens and the command runs as soon as the session connects:", hosts, h => h.Display) is not { } host) return;
        if (!Envs.Confirm(host, "Run selection", $"On {host.Display}, run:\n\n{command}", "Open terminal and run", danger: false)) return;
        Ui.Window.OpenTerminal(host, command);
    }

    private Task<bool> ApproveAsync(string title, string detail, HostEntry? host) =>
        Dispatcher.InvokeAsync(() =>
        {
            // The host's environment banner; PRODUCTION needs its name typed (ADR-017).
            bool ok = Envs.Confirm(host, title, detail + "\n\nAllow the AI operator to do this?", "Allow", danger: true);
            if (!ok) Add(new ChatLine("tool", $"✕ declined: {title}"));
            return ok;
        }).Task;

    public void Dispose() { _cts?.Cancel(); _life.Cancel(); if (Current == this) Current = null; }
}
