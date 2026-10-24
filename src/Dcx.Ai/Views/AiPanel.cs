using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Ai;
using DcxManager.Services;
using DcxManager.Sessions;

namespace DcxManager.Views;

/// <summary>
/// The AI Engineer sidebar. It only presents an <see cref="AiWorkspace"/>: the conversation, the context list (what the
/// model sees: ✓ included, ○ available, ⛔ BLOCKED) and the actions. One instance lives for the whole app and is moved
/// between the dock and its detached window (<see cref="AiDock"/>), so it subscribes to the workspace exactly once.
/// </summary>
public sealed class AiPanel : UserControl
{
    public AiWorkspace Workspace { get; }

    public event Action? DetachToggleRequested;
    public event Action? CollapseRequested;
    public event Action? HideRequested;

    private readonly TextBlock _provider = new TextBlock() { TextTrimming = TextTrimming.CharacterEllipsis }.Fs(FontStep.Caption);
    private readonly ComboBox _conversations = new() { Margin = new Thickness(0, 0, 6, 0), ToolTip = "Switch conversation (kept in memory until the app closes)" };
    private readonly StackPanel _context = new();
    private readonly TextBlock _contextHead = new() { Margin = new Thickness(0, 0, 0, 3) };
    private readonly StackPanel _messages = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBox _input = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MaxHeight = 160, Padding = new Thickness(6, 4, 6, 4),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ToolTip = "Ask the AI engineer · Enter sends · Shift+Enter new line",
    };
    private readonly Button _send = new() { Content = "Send", MinWidth = 70, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Button _stop = new() { Content = "■ Stop", MinWidth = 70, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
    private readonly TextBlock _thinking = new TextBlock() { Margin = new Thickness(2, 4, 0, 0), Visibility = Visibility.Collapsed }.Fs(FontStep.Small);
    private readonly Button _detach;
    private readonly ComboBox _target = new() { ToolTip = "Choose the terminal session the AI's commands may run in (each one is still shown and asks first)" };
    private readonly Button _stopCmd = new() { Content = "■ Stop", MinWidth = 0, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed, ToolTip = "Send Ctrl+C to the command the AI is running" };
    private readonly TextBlock _targetInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) }.Fs(FontStep.Caption);
    private readonly StackPanel _targetBox;
    private readonly CheckBox _autoRun = new()
    {
        Content = "Auto-run read-only commands in the target session", Margin = new Thickness(0, 4, 0, 0),
        ToolTip = "Off by default. When on, the AI runs commands that only read (ls, df, journalctl, systemctl status…) in the chosen session without asking each time.\n" +
                  "Change, sensitive and destructive commands still show the review and typed confirmation. Never in a PRODUCTION session, never interactive or endless commands. Every run is audited.",
    };
    // Per-conversation model and agent mode (ADR-034).
    private readonly ComboBox _model = new() { IsEditable = true, MinWidth = 120, MaxWidth = 200, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Model for this conversation (of the current engine). Empty = the default from Settings." };
    private readonly CheckBox _agent = new() { Content = "Agent", VerticalAlignment = VerticalAlignment.Center, ToolTip = "Agent mode: the AI may use the app's tools over several steps (look things up, run commands in the target terminal, act with your approval). Off = answers only." };
    private bool _syncingModel;
    // Streaming answer + step log.
    private readonly TextBox _liveText = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent };
    private readonly Border _live;
    private readonly StackPanel _stepList = new();
    private readonly Expander _steps = new() { Header = "STEPS", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0), IsExpanded = true };
    private int _livePending, _stepsPending;
    private bool _fillingTargets;
    private AiConversation? _shown;
    private TextBlock? _intro;
    private bool _syncingCombo;
    private bool _detached;
    // 0.5.1 #14: when Foundry Local is the engine and isn't ready, one click sets it up here (install, start, phi-4-mini).
    private readonly Border _foundryBanner = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(1) };
    private readonly TextBlock _foundryText = new TextBlock { TextWrapping = TextWrapping.Wrap }.Fs(FontStep.Small);
    private readonly Button _foundrySetup = new() { Content = "⚙ Set up Foundry Local", Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };

    public AiPanel(AiWorkspace workspace)
    {
        Workspace = workspace;
        _provider.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _thinking.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _contextHead.SetResourceReference(StyleProperty, "Label");
        _send.SetResourceReference(StyleProperty, "PrimaryButton");

        // Header: title, provider · model, actions.
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        Button Tool(string text, string tip, Action click)
        {
            var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(3, 0, 0, 0), MinWidth = 0, Padding = new Thickness(7, 2, 7, 2) };
            b.SetResourceReference(StyleProperty, "SmallButton");
            b.Click += (_, _) => click();
            tools.Children.Add(b);
            return b;
        }
        Tool("＋", "New conversation (the current one stays in the list)", () => { Workspace.NewConversation(); FocusInput(); });
        Tool("⌫", "Clear this conversation: messages and shared context items", () =>
        {
            if (Workspace.Active.Messages.Count == 0 || Dialogs.Confirm("Clear conversation", "Clear this conversation's messages and shared context items?", "Clear")) Workspace.Clear();
        });
        _detach = Tool("⧉", "", () => DetachToggleRequested?.Invoke());
        Tool("▸", "Collapse to a thin strip (Ctrl+Shift+A shows it again)", () => CollapseRequested?.Invoke());
        Tool("✕", "Hide the AI sidebar (AI → AI Engineer sidebar, or Ctrl+Shift+A, brings it back)", () => HideRequested?.Invoke());
        IsDetached = false;

        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = "✦ AI Engineer", FontWeight = FontWeights.SemiBold }.Fs(FontStep.Title));
        title.Children.Add(_provider);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(tools, Dock.Right);
        head.Children.Add(tools);
        head.Children.Add(title);

        _conversations.SelectionChanged += (_, _) =>
        {
            if (!_syncingCombo && _conversations.SelectedItem is ComboBoxItem { Tag: AiConversation c }) Workspace.Switch(c);
        };
        var convRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_agent, Dock.Right); DockPanel.SetDock(_model, Dock.Right);
        convRow.Children.Add(_agent); convRow.Children.Add(_model);
        convRow.Children.Add(_conversations);
        _agent.Checked += (_, _) => { if (!_syncingModel) Workspace.Active.Agent = true; };
        _agent.Unchecked += (_, _) => { if (!_syncingModel) Workspace.Active.Agent = false; };
        _model.DropDownOpened += async (_, _) => await FillModelsAsync();
        _model.SelectionChanged += (_, _) => { if (!_syncingModel && _model.SelectedItem is ComboBoxItem { Tag: var tag }) SetModel(tag as string); };
        _model.LostKeyboardFocus += (_, _) => { if (!_syncingModel && _model.IsEditable) SetModel(_model.Text.StartsWith("(default") ? null : _model.Text); };
        System.Windows.Automation.AutomationProperties.SetName(_model, "Model for this conversation");

        // Target terminal (ADR-028): the one session proposed commands may run in — chosen here, never switched by the AI.
        var targetHead = new TextBlock { Text = "TARGET TERMINAL", Margin = new Thickness(0, 0, 0, 3) };
        targetHead.SetResourceReference(StyleProperty, "Label");
        _target.DropDownOpened += (_, _) => FillTargets();
        _target.SelectionChanged += (_, _) =>
        {
            if (_fillingTargets) return;
            if ((_target.SelectedItem as ComboBoxItem)?.Tag is FollowFront) { TerminalBridge.FollowFront(); return; }
            TerminalBridge.Choose((_target.SelectedItem as ComboBoxItem)?.Tag as ITerminal);
        };
        System.Windows.Automation.AutomationProperties.SetName(_target, "Target terminal for AI commands");
        _stopCmd.SetResourceReference(StyleProperty, "DangerButton");
        _stopCmd.Click += (_, _) => { if (TerminalBridge.Interrupt()) _targetInfo.Text = "Ctrl+C sent to the running command."; };
        var targetRow = new DockPanel();
        DockPanel.SetDock(_stopCmd, Dock.Right);
        targetRow.Children.Add(_stopCmd);
        targetRow.Children.Add(_target);
        _targetInfo.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var targetBox = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        targetBox.Children.Add(targetHead); targetBox.Children.Add(targetRow); targetBox.Children.Add(_autoRun); targetBox.Children.Add(_targetInfo);
        _autoRun.IsChecked = AppSettings.Current.AiAutoRunReadOnlyTarget;
        // Admin-protected (0.3.0 §1): the change goes through the gate; the box shows what is in force afterwards.
        _autoRun.Click += async (_, _) =>
        {
            bool on = _autoRun.IsChecked == true;
            _autoRun.IsChecked = AppSettings.Current.AiAutoRunReadOnlyTarget;
            if (on == AppSettings.Current.AiAutoRunReadOnlyTarget) return;
            _autoRun.IsEnabled = false;
            try
            {
                if (await ProtectedSettings.ChangeAsync(on ? "turn on AI auto-run of read-only commands" : "turn off AI auto-run of read-only commands", "ai.autoRunReadOnly", on))
                    AuditLog.Write("you", "ai", on ? "AI Engineer: auto-run of read-only commands in the target session turned ON" : "AI Engineer: auto-run of read-only commands turned off", TerminalBridge.Target?.Describe() ?? "");
            }
            finally { _autoRun.IsEnabled = true; _autoRun.IsChecked = AppSettings.Current.AiAutoRunReadOnlyTarget; ShowTarget(); }
        };
        _targetBox = targetBox;
        TerminalBridge.TargetChanged += () => Dispatcher.BeginInvoke(ShowTarget);
        TerminalBridge.BusyChanged += () => Dispatcher.BeginInvoke(ShowTarget);

        // Context: what the model will see. Only what the user explicitly shared; credentials are listed as BLOCKED.
        var contextBox = new StackPanel();
        contextBox.Children.Add(_contextHead);
        contextBox.Children.Add(new ScrollViewer { Content = _context, MaxHeight = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var hint = new TextBlock
        {
            Text = "Shared only when you click ✦ on something. Terminals, Remote Desktop and notes are never streamed; a terminal's output is shared only for a command you ran through TARGET TERMINAL.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0),
        }.Fs(FontStep.Caption);
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        contextBox.Children.Add(hint);
        var contextBorder = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 8), Child = contextBox };
        contextBorder.SetResourceReference(Border.BorderBrushProperty, "Line");

        _scroll.Content = _messages;
        _liveText.SetResourceReference(Control.ForegroundProperty, "Fg");
        _liveText.Mono();
        var liveStack = new StackPanel();
        var liveWho = new TextBlock { Text = "✦ AI  · writing…", Margin = new Thickness(0, 0, 0, 2) }.Fs(FontStep.Caption);
        liveWho.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        liveStack.Children.Add(liveWho); liveStack.Children.Add(_liveText);
        _live = new Border { Child = liveStack, Padding = new Thickness(8, 5, 8, 6), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(2, 0, 0, 0) };
        _live.SetResourceReference(Border.BackgroundProperty, "Input");
        _live.SetResourceReference(Border.BorderBrushProperty, "Accent");
        _steps.Content = new ScrollViewer { Content = _stepList, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        System.Windows.Automation.AutomationProperties.SetName(_steps, "Steps of the current question");
        var chat = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(8, 6, 8, 6), Child = _scroll };
        chat.SetResourceReference(Border.BorderBrushProperty, "Line");
        chat.SetResourceReference(Border.BackgroundProperty, "Input");

        var inputRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(_send, Dock.Right); DockPanel.SetDock(_stop, Dock.Right);
        inputRow.Children.Add(_send); inputRow.Children.Add(_stop); inputRow.Children.Add(_input);
        _send.Click += async (_, _) => await SendAsync();
        _stop.Click += (_, _) => Workspace.Stop();
        _input.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; await SendAsync(); }
        };

        var bannerBody = new StackPanel();
        bannerBody.Children.Add(_foundryText); bannerBody.Children.Add(_foundrySetup);
        _foundryBanner.Child = bannerBody;
        _foundryBanner.SetResourceReference(Border.BorderBrushProperty, "Accent");
        _foundrySetup.SetResourceReference(StyleProperty, "PrimaryButton");
        _foundrySetup.Click += async (_, _) => await SetUpFoundryAsync();
        Loaded += (_, _) => { FoundryManager.Progress += OnFoundryProgress; _ = CheckFoundryAsync(); };
        Unloaded += (_, _) => FoundryManager.Progress -= OnFoundryProgress;
        var root = new DockPanel { Margin = new Thickness(10, 10, 10, 10) };
        DockPanel.SetDock(_foundryBanner, Dock.Top);
        DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(convRow, Dock.Top); DockPanel.SetDock(_targetBox, Dock.Top); DockPanel.SetDock(contextBorder, Dock.Top);
        DockPanel.SetDock(inputRow, Dock.Bottom); DockPanel.SetDock(_thinking, Dock.Bottom); DockPanel.SetDock(_steps, Dock.Bottom);
        root.Children.Add(head); root.Children.Add(_foundryBanner); root.Children.Add(convRow); root.Children.Add(_targetBox); root.Children.Add(contextBorder);
        root.Children.Add(inputRow); root.Children.Add(_thinking); root.Children.Add(_steps); root.Children.Add(chat);
        _input.TextChanged += (_, _) => { if (_input.Text.Length == 1) AiClient.Warm(); };
        // 0.6.19: start loading the model when the question box is clicked into, not at the first keystroke (and not just
        // because the panel is showing: a GPU model would hold video memory while the PC is used for something else).
        _input.GotKeyboardFocus += (_, _) => AiClient.Warm();
        Content = root;
        SetResourceReference(BackgroundProperty, "Surface");

        // Subscribed once, for the life of the app (the panel is never recreated, only re-parented).
        Workspace.Changed += Refresh;
        // Approvals for the agent's tools: the host's environment banner; PRODUCTION needs its name typed (ADR-017).
        Workspace.Approve = (title, detail, host) => Dispatcher.InvokeAsync(() =>
            Envs.Confirm(host, title, detail + "\n\nAllow the AI Engineer to do this?", "Allow", danger: true)).Task;
        // Streaming and steps arrive on background threads: one pending UI update at a time, never Invoke per token.
        Workspace.LiveChanged += c =>
        {
            if (Interlocked.Exchange(ref _livePending, 1) == 1) return;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => { Interlocked.Exchange(ref _livePending, 0); ShowLive(); });
        };
        Workspace.StepAdded += (c, _) =>
        {
            if (Interlocked.Exchange(ref _stepsPending, 1) == 1) return;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => { Interlocked.Exchange(ref _stepsPending, 0); ShowSteps(); });
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) { Refresh(); ShowTarget(); } };
        Refresh();
        ShowTarget();
    }

    /// <summary>Set by the dock: the detach button becomes "reattach" while the panel is in its own window.</summary>
    public bool IsDetached
    {
        get => _detached;
        set
        {
            _detached = value;
            _detach.Content = value ? "⇲" : "⧉";
            _detach.ToolTip = value ? "Reattach to the main window (same conversation)" : "Detach into its own window (same conversation; move it to another monitor)";
        }
    }

    public void FocusInput() => Dispatcher.BeginInvoke(() => { _input.Focus(); Keyboard.Focus(_input); }, System.Windows.Threading.DispatcherPriority.Input);

    private async Task SendAsync()
    {
        var q = _input.Text.Trim();
        if (q.Length == 0 || Workspace.Running) return;
        _input.Clear();
        await Workspace.SendAsync(q);
    }

    // ---- streaming, steps, model (ADR-034) -------------------------------------------------------------------------

    private void ShowLive()
    {
        var text = Workspace.Active.Live;
        if (text.Length == 0) { _messages.Children.Remove(_live); return; }
        _liveText.Text = text;
        if (!_messages.Children.Contains(_live))
        {
            if (_intro != null) { _messages.Children.Remove(_intro); _intro = null; }
            _messages.Children.Add(_live);
        }
        _scroll.ScrollToEnd();
    }

    internal string LiveTextForTest => _messages.Children.Contains(_live) ? _liveText.Text : "";
    internal int StepLinesForTest => _stepList.Children.Count;

    private void ShowSteps()
    {
        List<AgentStep> steps;
        lock (Workspace.Active.Steps) steps = Workspace.Active.Steps.ToList();
        _steps.Visibility = steps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        // Append new lines; rebuild when the list was cleared or another conversation is shown.
        if (_stepList.Children.Count > steps.Count) _stepList.Children.Clear();
        for (int i = _stepList.Children.Count; i < steps.Count; i++)
        {
            var st = steps[i];
            var line = new TextBlock { Text = st.ToString(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(st.Kind is "result" ? 14 : 0, 1, 0, 1) }.Fs(FontStep.Caption);
            line.SetResourceReference(TextBlock.ForegroundProperty, st.Kind == "error" ? "Warn" : st.Kind is "tool" or "answer" ? "Fg" : "Muted");
            _stepList.Children.Add(line);
        }
        _steps.Header = Workspace.Running ? $"STEPS · {steps.Count(x => x.Kind == "tool")} tool call(s) so far" : $"STEPS · {steps.Count(x => x.Kind == "tool")} tool call(s)";
        if (_steps.Content is ScrollViewer sv) sv.ScrollToEnd();
    }

    private void SetModel(string? model)
    {
        model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        if (Workspace.Active.Model == model) return;
        Workspace.Active.Model = model;
        Refresh();
    }

    private async Task FillModelsAsync()
    {
        var keep = Workspace.Active.Model;
        List<string> models;
        try { using var cts = new CancellationTokenSource(4000); models = await AiClient.ModelsAsync(cts.Token); }
        catch { models = new(); }
        _syncingModel = true;
        try
        {
            _model.Items.Clear();
            _model.Items.Add(new ComboBoxItem { Content = $"(default) {AiClient.DefaultModel}", Tag = null });
            foreach (var m in models.Where(m => m != AiClient.DefaultModel).Take(60)) _model.Items.Add(new ComboBoxItem { Content = m, Tag = m });
            _model.Text = keep ?? $"(default) {AiClient.DefaultModel}";
        }
        finally { _syncingModel = false; }
    }

    // ---- terminal target (ADR-028) --------------------------------------------------------------------------------

    private const string NoTarget = "(none — the AI only shows commands)";
    private const string FollowFront = "↻ Follow the terminal in front";
    internal bool StopVisibleForTest => _stopCmd.Visibility == Visibility.Visible;

    /// <summary>Lists the live sessions the bridge can use; the current choice stays selected (or "none").</summary>
    private void FillTargets()
    {
        _fillingTargets = true;
        try
        {
            _target.Items.Clear();
            _target.Items.Add(new ComboBoxItem { Content = NoTarget, Tag = null });
            // 0.6.10: back to the default after picking a terminal yourself (the default follows the terminal in front).
            if (!TerminalBridge.Following)
                _target.Items.Add(new ComboBoxItem { Content = FollowFront, Tag = FollowFront, ToolTip = "The target becomes whichever terminal you bring to the front" });
            foreach (var v in TerminalBridge.Candidates())
                _target.Items.Add(new ComboBoxItem { Content = $"{v.Target.Title} · {v.Target.Endpoint} · session {v.SessionId}", Tag = v, ToolTip = $"{v.Target.Kind} · {v.Shell} shell · {EnvUi.ForView(v)}" });
            foreach (var (title, why) in TerminalBridge.Unsupported())
                _target.Items.Add(new ComboBoxItem { Content = $"{title} — {why}", IsEnabled = false });
            var cur = TerminalBridge.Target;
            _target.SelectedItem = _target.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is ITerminal v && v.SessionId == cur?.SessionId) ?? _target.Items[0];
        }
        finally { _fillingTargets = false; }
    }

    private void ShowTarget()
    {
        // 0.6.10: no target yet (or it closed / reconnected) while following → the terminal in front.
        if (TerminalBridge.Following && (TerminalBridge.Target is not { } cur || TerminalBridge.Problem(cur, out _) != null)) TerminalBridge.Follow();
        var t = TerminalBridge.Target;
        if (_target.Items.Count == 0 || ((_target.SelectedItem as ComboBoxItem)?.Tag as ITerminal)?.SessionId != t?.SessionId) FillTargets();
        _stopCmd.Visibility = TerminalBridge.Busy ? Visibility.Visible : Visibility.Collapsed;
        if (!AppSettings.Current.AiTerminalBridge) { _targetInfo.Text = "Running AI commands in terminals is off (Settings → AI & models → AI permissions & safety)."; return; }
        _autoRun.IsChecked = AppSettings.Current.AiAutoRunReadOnlyTarget;
        _targetInfo.Text = t == null
            ? "No target: commands in answers are shown only. Pick a live terminal to review and run them there."
            : (TerminalBridge.Problem(t, out _) is { } p ? "⚠ " + p : $"→ {t.Describe()}{(TerminalBridge.Following ? " (follows the terminal in front; pick one to keep it)" : "")}. " + (Workspace.Active.Agent
                ? (AppSettings.Current.AiAutoRunReadOnlyTarget ? (Envs.IsProduction(t.EnvLabel) ? "PRODUCTION: every command is reviewed." : "The agent runs read-only commands here by itself; anything else asks.") : "The agent may run commands here; each one is reviewed first.")
                : "Commands in answers get a “Review & run” button; nothing runs without your click."));
        RebuildMessages();
    }

    /// <summary>Review (risk, target, exact text) → run in the chosen session only → result back into the conversation.</summary>
    private async Task RunBlockAsync(string code, string purpose)
    {
        var t = TerminalBridge.Target;
        var c = Workspace.Active;
        if (TerminalBridge.Problem(t, out var view) is { } why) { c.Messages.Add(new AiMessage("error", why, DateTime.Now)); ShowTarget(); return; }
        if (!TerminalBridge.Review(t!, view!, code, purpose, out var risk)) { c.Messages.Add(new AiMessage("note", "Not run (declined).", DateTime.Now)); return; }
        _stopCmd.Visibility = Visibility.Visible;
        c.Messages.Add(new AiMessage("note", $"▶ Running in {t!.Title} (session {t.SessionId}, {risk.Label})…", DateTime.Now));
        try
        {
            var r = await TerminalBridge.RunAsync(t, code, "you (AI suggestion)", risk, CancellationToken.None);
            c.Messages.Add(new AiMessage(r.Sent ? "note" : "error", $"{(r.Finished && r.ExitCode == 0 ? "✓" : r.Sent ? "⚠" : "✕")} {r.Message}{(r.Cwd != null ? $" · {r.Cwd}" : "")}" +
                (r.Output.Length > 0 ? "\n" + (r.Output.Length > 1500 ? "…" + r.Output[^1500..] : r.Output) : ""), DateTime.Now));
            if (r.Sent && AppSettings.Current.AiShareCommandOutput)
                Workspace.AddContext($"Output: {code.Split('\n')[0]} · {t.Title} {DateTime.Now:HH:mm:ss}", TerminalBridge.ForAi(t, code, r));
        }
        finally { _stopCmd.Visibility = Visibility.Collapsed; ShowTarget(); }
    }

    // ---- presentation ---------------------------------------------------------------------------------------------

    private void Refresh()
    {
        var ws = Workspace;
        var model = ws.Active.Model ?? ws.ModelLabel;
        _provider.Text = model.Length > 0 && AiClient.Engine != "off" ? $"{ws.ProviderLabel} · {model}{(ws.Active.Model != null ? " (this chat)" : "")}" : ws.ProviderLabel;
        _syncingModel = true;
        try
        {
            _agent.IsChecked = ws.Active.Agent;
            if (!_model.IsKeyboardFocusWithin) _model.Text = ws.Active.Model ?? $"(default) {AiClient.DefaultModel}";
        }
        finally { _syncingModel = false; }
        _provider.ToolTip = "Engine and model (Settings → AI engine)";

        _syncingCombo = true;
        try
        {
            // Rebuilt each time (at most a dozen): titles change after the first question.
            _conversations.Items.Clear();
            foreach (var c in ws.Conversations)
            {
                var item = new ComboBoxItem { Content = $"{c.Title}  ·  {c.Started:HH:mm}", Tag = c };
                _conversations.Items.Add(item);
                if (c == ws.Active) _conversations.SelectedItem = item;
            }
        }
        finally { _syncingCombo = false; }

        if (_shown != ws.Active)
        {
            if (_shown != null) _shown.Messages.CollectionChanged -= OnMessages;
            _shown = ws.Active;
            _shown.Messages.CollectionChanged += OnMessages;
            RebuildMessages();
            _stepList.Children.Clear();
            ShowSteps();
        }

        _context.Children.Clear();
        int included = 0, chars = 0;
        foreach (var item in ws.Context)
        {
            _context.Children.Add(ContextRow(item));
            if (item.Included) { included++; chars += item.Content.Length; }
        }
        _contextHead.Text = included == 0 ? "CONTEXT · nothing shared yet" : $"CONTEXT · {included} item(s) shared, {chars:N0} characters";

        _send.Visibility = ws.Running ? Visibility.Collapsed : Visibility.Visible;
        _stop.Visibility = ws.Running ? Visibility.Visible : Visibility.Collapsed;
        _thinking.Visibility = ws.Running ? Visibility.Visible : Visibility.Collapsed;
        _thinking.Text = $"… {(ws.Active.Agent ? "working" : "thinking")} ({ws.Active.Model ?? ws.ModelLabel}) · ■ Stop cancels{(ws.Active.Agent ? " (and interrupts its terminal command)" : "")}";
    }

    private FrameworkElement ContextRow(AiContextItem item)
    {
        var row = new DockPanel { Tag = item };
        row.SetResourceReference(Control.MarginProperty, "ListRowPadding");
        var state = new Button
        {
            Content = item.Glyph, Width = 26, MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0), IsEnabled = !item.Blocked,
            ToolTip = item.Blocked ? "Blocked: secrets are never sent to any model" : item.Included ? "Included in the next question — click to leave it out" : "Available — click to include it in the next question",
        };
        state.SetResourceReference(StyleProperty, "SmallButton");
        state.SetResourceReference(Control.ForegroundProperty, item.Blocked ? "Warn" : item.Included ? "Good" : "Muted");
        state.Click += (_, _) => Workspace.SetIncluded(item, !item.Included);
        DockPanel.SetDock(state, Dock.Left);
        row.Children.Add(state);
        if (item != Workspace.Active.VaultRow)
        {
            var remove = new Button { Content = "×", Width = 22, MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Remove from this conversation" };
            remove.SetResourceReference(StyleProperty, "SmallButton");
            remove.Click += (_, _) => Workspace.Remove(item);
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
        }
        // State word docked right so it never ellipsises away (✓/○/⛔ plus a word: never colour alone).
        var meta = new TextBlock
        {
            Text = item.Blocked ? "BLOCKED" : $"{item.StateText} · {item.Content.Length:N0} ch" + (item.Kind == AiContextKind.Help ? " · help" : ""), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            FontWeight = item.Blocked ? FontWeights.SemiBold : FontWeights.Normal,
        }.Fs(FontStep.Caption);
        meta.SetResourceReference(TextBlock.ForegroundProperty, item.Blocked ? "Warn" : "Muted");
        DockPanel.SetDock(meta, Dock.Right);
        row.Children.Add(meta);
        var text = new TextBlock { Text = item.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.PaddingProperty, "RowPadding");
        text.ToolTip = item.Blocked ? item.Title + "\nNever sent. The AI never sees passwords, private keys, passphrases or API keys."
                                    : item.Title + $"\nAdded {item.Added:HH:mm:ss} (a snapshot; it isn't updated afterwards).\n\n" + (item.Content.Length > 600 ? item.Content[..600] + "…" : item.Content);
        row.Children.Add(text);
        return row;
    }

    private void OnMessages(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            if (_intro != null) { _messages.Children.Remove(_intro); _intro = null; }
            bool live = _messages.Children.Contains(_live);
            if (live) _messages.Children.Remove(_live);
            foreach (AiMessage m in e.NewItems) _messages.Children.Add(Bubble(m));
            if (live && Workspace.Active.Live.Length > 0) _messages.Children.Add(_live);
            _scroll.ScrollToEnd();
        }
        else RebuildMessages();
    }

    private void RebuildMessages()
    {
        _messages.Children.Clear();
        _intro = null;
        if (_shown == null || _shown.Messages.Count == 0)
        {
            var intro = _intro = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 2, 4),
                Text = "Ask about your servers, configs, errors or this app. Use ✦ (right-click menus, error dialogs, file and document viewers) " +
                       "to share something: it appears in CONTEXT above and the answer comes here. Detach (⧉) moves this same conversation to its own window.",
            };
            intro.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _messages.Children.Add(intro);
            return;
        }
        foreach (var m in _shown.Messages) _messages.Children.Add(Bubble(m));
        if (_shown.Live.Length > 0) { _liveText.Text = _shown.Live; _messages.Children.Add(_live); }
        _scroll.ScrollToEnd();
    }

    private FrameworkElement Bubble(AiMessage m)
    {
        var who = new TextBlock
        {
            Text = m.Role switch { "user" => "You", "assistant" => "✦ AI", "error" => "✕ Error", _ => "·" } + "  " + m.Time.ToString("HH:mm"), Margin = new Thickness(0, 0, 0, 2),
        }.Fs(FontStep.Caption);
        who.SetResourceReference(TextBlock.ForegroundProperty, m.Role == "error" ? "Warn" : m.Role == "assistant" ? "Accent" : "Muted");
        var body = new TextBox
        {
            Text = m.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
        };
        body.SetResourceReference(Control.ForegroundProperty, m.Role == "error" ? "Warn" : "Fg");
        if (m.Role == "assistant") body.Mono();
        Menus.ForText(body);
        var stack = new StackPanel();
        stack.Children.Add(who);
        stack.Children.Add(body);
        if (m.Role == "assistant" && m.Text.Trim().Length > 0)
        {
            // Keep an answer: into the active incident's timeline, or as a note. Only on click.
            var keep = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            Button K(string text, string tip, Action a)
            {
                var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 0) }.Fs(FontStep.Caption);
                b.SetResourceReference(FrameworkElement.StyleProperty, "SmallButton");
                b.Click += (_, _) => a();
                keep.Children.Add(b);
                return b;
            }
            string First() { var l = m.Text.Trim().Split('\n')[0].Trim().TrimStart('#', ' '); return l.Length > 70 ? l[..70] + "…" : l; }
            K("⚑ Add to incident", "Adds this answer to the active incident's timeline (or asks which incident)", () => Ui.Window.AddToIncident(Incidents.ItemKind.AiAnalysis, "AI: " + First(), m.Text));
            K("✎ Save as note", "Creates an encrypted note with this answer", () =>
            {
                var n = Notes.NoteStore.Create("AI: " + First(), null, $"[{m.Time:yyyy-MM-dd HH:mm}] AI answer ({Ai.AiClient.Model})\n\n{m.Text}");
                n.Tags.Add("ai"); Notes.NoteStore.Save(n);
                Ui.Window.Status($"✎ Saved as note “{n.Title}” (encrypted)");
            });
            stack.Children.Add(keep);
            // Commands in the answer: one "Review & run" per block, only for the chosen target (ADR-028). Click → review → run.
            if (AppSettings.Current.AiTerminalBridge && TerminalBridge.Target is { } t && t.Shell.Length > 0)
            {
                // A block that is a written tool call ("terminal_run --command 'df -h'") runs its command, never its text.
                var names = Capabilities.All.Select(c => c.Name).ToHashSet();
                var blocks = TerminalBridge.CommandBlocks(m.Text, t.Shell).Select(c => Ai.AiAgent.ShellCommandOf(c, names)).OfType<string>().Distinct().ToList();
                if (blocks.Count > 0)
                {
                    var runs = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
                    var purpose = First();
                    foreach (var code in blocks.Take(8))
                    {
                        var risk = CommandRisk.Assess(code);
                        var run = K($"▶ Review & run in {t.Title}: {(code.Split('\n')[0].Length > 40 ? code.Split('\n')[0][..40] + "…" : code.Split('\n')[0])}{(code.Contains('\n') ? " (+ more lines)" : "")} · {risk.Label}",
                            "Shows the target, the risk and the full text first; runs only in the chosen session", () => _ = RunBlockAsync(code, purpose));
                        keep.Children.Remove(run);
                        run.HorizontalAlignment = HorizontalAlignment.Left;
                        run.Margin = new Thickness(0, 2, 0, 0);
                        if (risk.Level >= RiskLevel.Sensitive) run.SetResourceReference(Control.ForegroundProperty, "Warn");
                        runs.Children.Add(run);
                    }
                    stack.Children.Add(runs);
                }
            }
        }
        var b = new Border { Child = stack, Padding = new Thickness(8, 5, 8, 6), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(3) };
        b.SetResourceReference(Border.BackgroundProperty, m.Role == "user" ? "Surface" : "Input");
        if (m.Role == "assistant") { b.BorderThickness = new Thickness(2, 0, 0, 0); b.SetResourceReference(Border.BorderBrushProperty, "Accent"); }
        return b;
    }

    // ---- Foundry Local one-click set-up (0.5.1 #14) -------------------------------------------------------------------

    /// <summary>Shows the banner when Foundry Local is the engine and doesn't answer (looks only; installs nothing).</summary>
    private async Task CheckFoundryAsync()
    {
        if (AiClient.Engine != "npu" || AppSettings.Current.FoundryUrl.Trim().Length > 0) { _foundryBanner.Visibility = Visibility.Collapsed; return; }
        bool ready;
        try { ready = await FoundryManager.ReadyAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException) { ready = false; }
        _foundryBanner.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        if (!ready) _foundryText.Text = $"Foundry Local (the on-device AI engine) isn't ready on this PC. One click installs it from Microsoft (winget, admin approval if Windows asks), starts it, downloads {AppSettings.Current.FoundryModel} and checks that it answers.";
    }

    private async Task SetUpFoundryAsync()
    {
        _foundrySetup.IsEnabled = false;
        try
        {
            if (await FoundryManager.EnsureReadyAsync(byUser: true)) { _foundryText.Text = "✓ Foundry Local is ready."; await Task.Delay(1500); }
            await CheckFoundryAsync();
        }
        finally { _foundrySetup.IsEnabled = true; }
    }

    private void OnFoundryProgress(string text) => Dispatcher.BeginInvoke(() => { if (_foundryBanner.Visibility == Visibility.Visible) _foundryText.Text = text; });
}
