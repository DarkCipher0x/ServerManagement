using System.Windows;
using System.Windows.Controls;
using DcxManager.Ai;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// The AI's three Settings cards ("AI engine", "AI speed, agents &amp; memory", "AI permissions &amp; safety"), contributed
/// by <see cref="AiModule"/> (0.2.0 phase 4 moved the bodies out of <see cref="SettingsView"/> unchanged; they build with
/// the view's card helpers). Settings itself no longer knows anything about the AI.
/// </summary>
public static class AiSettingsCards
{
    /// <summary>The AI engine card. Contributed by <see cref="AiModule"/>.</summary>
    public static Border Engine(ISettingsKit v)
    {
        var s = v.Settings;
        TextBox Tb(string text, double w = 300) => new() { Text = text, Width = w, Padding = new Thickness(5, 4, 5, 4) };
        PasswordBox Pb(string text) { var p = new PasswordBox { Width = 300, Padding = new Thickness(5, 4, 5, 4) }; p.Password = text; return p; }
        StackPanel? foundryRow = null;

        var engine = new ComboBox { Width = 320, ItemsSource = AiEngines.All.Select(e => e.Title).ToList() };
        engine.SelectedIndex = Math.Max(0, Array.FindIndex(AiEngines.All, e => e.Id == s.AiEngine));
        if (!s.AiAllowEngineChange) { engine.IsEnabled = false; engine.ToolTip = "🔒 Set by your organisation (Group Policy: AiAllowEngineChange)."; }
        var engineHint = new TextBlock { Style = (Style)v.FindResource("Hint"), Margin = new Thickness(0, 4, 0, 4) };

        // Ollama
        var url = Tb(s.OllamaUrl);
        var model = new ComboBox { IsEditable = true, Text = s.OllamaModel, Width = 260 };
        var tunnel = new ComboBox { Width = 300, DisplayMemberPath = "Display" };
        var hosts = new List<object> { "(none — use the address above)" };
        hosts.AddRange(AppData.Current.Hosts);
        tunnel.ItemsSource = hosts;
        tunnel.SelectedItem = AppData.Current.FindHost(s.OllamaTunnelHostId) ?? hosts[0];
        var ctx = new ComboBox { Width = 100, ItemsSource = new[] { 2048, 4096, 8192, 16384, 32768, 65536 }, SelectedItem = s.AiContext };
        var threads = new ComboBox { Width = 80, ItemsSource = new[] { 0, 2, 4, 6, 8, 12, 16 }, SelectedItem = s.AiCpuThreads, ToolTip = "0 = let Ollama decide" };
        var cpuRow = v.Labeled("CPU THREADS (CPU-only)", threads);
        var ollama = new StackPanel();
        ollama.Children.Add(v.L("OLLAMA ADDRESS")); ollama.Children.Add(url); ollama.Children.Add(NetUi.LiveBadge(url));
        ollama.Children.Add(v.L("OR THROUGH AN SSH TUNNEL TO (Ollama on a GPU server)")); ollama.Children.Add(tunnel);
        ollama.Children.Add(v.Row(v.Labeled("CONTEXT WINDOW (tokens)", ctx), cpuRow));

        // NPU / Foundry Local
        var fUrl = Tb(s.FoundryUrl); fUrl.ToolTip = "Leave empty to discover it with `foundry server status`. For a fixed address run `foundry server start --port 39839 --idle-timeout 0` and enter http://127.0.0.1:39839";
        var npu = new StackPanel();
        npu.Children.Add(new TextBlock { Text = "Microsoft Foundry Local runs the model on this PC: on the NPU of a Copilot+ PC (Intel AI Boost, AMD XDNA, Snapdragon Hexagon), otherwise on the CPU. " +
            "Nothing leaves the PC. With automatic set-up on (below) the app installs it, starts it, downloads and loads the model and repairs the service when it stops answering; every step is also a button. " +
            "Intel AI Boost NPUs also need the Intel NPU driver for Windows (from intel.com) before Foundry Local uses them.", Style = (Style)v.FindResource("Hint"), TextWrapping = TextWrapping.Wrap });
        npu.Children.Add(v.L("FOUNDRY LOCAL ADDRESS (leave empty: the app finds it; only for a fixed port such as http://127.0.0.1:39839)")); npu.Children.Add(fUrl);

        // OpenAI-compatible
        var oUrl = Tb(s.OpenAiUrl); var oKey = Pb(s.OpenAiKey);
        var openai = new StackPanel();
        openai.Children.Add(v.L("BASE URL (…/v1)")); openai.Children.Add(oUrl); openai.Children.Add(NetUi.LiveBadge(oUrl));
        openai.Children.Add(v.L("API KEY (empty for local servers; stored encrypted)")); openai.Children.Add(oKey);
        var oImg = new CheckBox { Content = "This model accepts images (screenshots are only ever sent to a model that reads them)", IsChecked = s.OpenAiAcceptsImages, Margin = new Thickness(0, 6, 0, 0) };
        oImg.Click += (_, _) => { s.OpenAiAcceptsImages = oImg.IsChecked == true; v.Save(); };
        openai.Children.Add(oImg);

        // Anthropic
        var aUrl = Tb(s.AnthropicUrl); var aKey = Pb(s.AnthropicKey);
        var anthropic = new StackPanel();
        anthropic.Children.Add(v.L("BASE URL (https://api.anthropic.com, or http://localhost:11434 for Ollama)")); anthropic.Children.Add(aUrl); anthropic.Children.Add(NetUi.LiveBadge(aUrl));
        anthropic.Children.Add(v.L("API KEY (stored encrypted; not needed for Ollama)")); anthropic.Children.Add(aKey);

        var result = new TextBlock { Style = (Style)v.FindResource("Hint"), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        string Id() => AiEngines.All[Math.Max(0, engine.SelectedIndex)].Id;
        string ModelOf(string id) => id switch { "npu" => s.FoundryModel, "openai" => s.OpenAiModel, "anthropic" => s.AnthropicModel, _ => s.OllamaModel };

        void ShowPanels()
        {
            var id = Id();
            engineHint.Text = AiEngines.All[Math.Max(0, engine.SelectedIndex)].Hint;
            ollama.Visibility = id is "ollama-gpu" or "ollama-cpu" ? Visibility.Visible : Visibility.Collapsed;
            cpuRow.Visibility = id == "ollama-cpu" ? Visibility.Visible : Visibility.Collapsed;
            npu.Visibility = id == "npu" ? Visibility.Visible : Visibility.Collapsed;
            openai.Visibility = id == "openai" ? Visibility.Visible : Visibility.Collapsed;
            anthropic.Visibility = id == "anthropic" ? Visibility.Visible : Visibility.Collapsed;
            model.IsEnabled = id != "off";
            model.Text = ModelOf(id);
            if (foundryRow != null) foundryRow.Visibility = id == "npu" ? Visibility.Visible : Visibility.Collapsed;
        }
        void Apply()
        {
            var id = Id();
            // 0.5.1 #14: AiAllowEngineChange = off (Group Policy) keeps the engine and model the organisation chose.
            if (!s.AiAllowEngineChange && (id != s.AiEngine || model.Text.Trim() != ModelOf(id)))
            {
                engine.SelectedIndex = Math.Max(0, Array.FindIndex(AiEngines.All, e => e.Id == s.AiEngine));
                model.Text = ModelOf(s.AiEngine);
                result.Text = "🔒 Changing the AI engine or model is turned off by your organisation (Group Policy: AiAllowEngineChange).";
                return;
            }
            s.AiEngine = id;
            switch (id)
            {
                case "npu": s.FoundryModel = model.Text.Trim(); break;
                case "openai": s.OpenAiModel = model.Text.Trim(); break;
                case "anthropic": s.AnthropicModel = model.Text.Trim(); break;
                case "off": break;
                default: s.OllamaModel = model.Text.Trim(); break;
            }
            s.OllamaUrl = url.Text.Trim();
            s.OllamaTunnelHostId = (tunnel.SelectedItem as HostEntry)?.Id;
            if (ctx.SelectedItem is int c) s.AiContext = c;
            if (threads.SelectedItem is int t) s.AiCpuThreads = t;
            if (s.FoundryUrl != fUrl.Text.Trim()) AiClient.ForgetFoundry();
            s.FoundryUrl = fUrl.Text.Trim();
            s.OpenAiUrl = oUrl.Text.Trim(); s.OpenAiKey = oKey.Password.Trim();
            s.AnthropicUrl = aUrl.Text.Trim(); s.AnthropicKey = aKey.Password.Trim();
            v.Save();
        }

        async Task Load()
        {
            Apply();
            try
            {
                var m = await AiClient.ModelsAsync(new CancellationTokenSource(15000).Token);
                model.ItemsSource = m;
                var ver = AiClient.Engine.StartsWith("ollama") ? $"Ollama {await OllamaClient.VersionAsync()} · " : "";
                result.Text = $"✓ {ver}{m.Count} model(s) available at {await AiClient.BaseUrlAsync()}";
            }
            catch (Exception ex) { result.Text = "✕ " + ex.Message.Split('\n')[0]; }
        }
        async Task Test()
        {
            Apply();
            if (AiClient.Engine == "off") { result.Text = "AI is off — nothing to test."; return; }
            result.Text = "testing…";
            try
            {
                var reply = await AiClient.ChatAsync(new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = "Reply with the single word OK." } }, null, null, new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token);
                result.Text = $"✓ {await AiClient.DescribeAsync()} answered “{(reply["content"]?.GetValue<string>() ?? "").Trim()}” in {AiClient.LastLatency.TotalSeconds:0.0}s";
            }
            catch (Exception ex) { result.Text = "✕ " + ex.Message.Split('\n')[0]; }
        }
        // Foundry Local (ADR-026): status, automatic set-up and repair, a button for every step, the model guide for this PC.

        // Hardware
        var hw = new TextBlock { Style = (Style)v.FindResource("Hint"), TextWrapping = TextWrapping.Wrap };
        (string Engine, string Model, string Why)? rec = null;
        var applyRec = v.Btn("Apply recommendation", () =>
        {
            if (rec is not { } r) return;
            s.AiEnabled = r.Engine != "off";
            engine.SelectedIndex = Array.FindIndex(AiEngines.All, e => e.Id == r.Engine);
            if (r.Model.Length > 0) model.Text = r.Model;
            if (r.Engine == "ollama-cpu" && ctx.SelectedItem is int c && c > 8192) ctx.SelectedItem = 8192;
            Apply();
            result.Text = r.Engine.StartsWith("ollama") ? $"Applied. Install the model if needed: ollama pull {r.Model}" : "Applied.";
        });
        applyRec.IsEnabled = false;
        async Task Detect(bool refresh = true)
        {
            hw.Text = "detecting…";
            var p = await AiHardware.DetectAsync(refresh);
            rec = p.Recommend();
            hw.Text = p.Describe() + $"\n→ Recommended: {AiEngines.Title(rec.Value.Engine)}{(rec.Value.Model.Length > 0 ? " with " + rec.Value.Model : "")}. {rec.Value.Why}";
            applyRec.IsEnabled = true;
        }
        // Opening uses the profile found earlier (a WMI query each open blocked a pool thread, s36c); the button looks again.
        v.Loaded += async (_, _) => { if (rec == null) await Detect(refresh: false); };

        // Claude Code
        var ccModel = Tb(s.ClaudeCodeModel, 220); ccModel.ToolTip = "Empty = same as the Ollama model. Large context + good tool use, e.g. qwen3-coder:30b or gpt-oss:20b";
        var ccWork = Tb(s.ClaudeCodeWorkspace, 360); ccWork.ToolTip = "Empty = Documents\\DarkCipher Systems\\claude-workspace, or a local app folder when Documents is synchronised (OneDrive etc.)";
        var ccStatus = new TextBlock { Style = (Style)v.FindResource("Hint"), TextWrapping = TextWrapping.Wrap };
        void ApplyCc() { s.ClaudeCodeModel = ccModel.Text.Trim(); s.ClaudeCodeWorkspace = ccWork.Text.Trim(); v.Save(); }
        async Task CheckCc(bool onOpen = false)
        {
            ApplyCc();
            ClaudeCode.Status? st;
            if (onOpen)
            {
                // Opening joins a check already running and drops it when Settings closes (a hung Ollama, s36c).
                using var closed = new CancellationTokenSource();
                RoutedEventHandler gone = (_, _) => closed.Cancel();
                v.Unloaded += gone;
                try { st = await ClaudeCode.SharedDetectAsync(closed.Token); }
                finally { v.Unloaded -= gone; }
                if (st == null) return;
            }
            else st = await ClaudeCode.DetectAsync();
            ccStatus.Text = $"Claude Code: {st.ClaudePath ?? "not found"} · Ollama: {st.OllamaVersion ?? "not reachable"} at {st.OllamaUrl} · model {st.Model}" + (st.Problem != null ? "\n✕ " + st.Problem : "\n✓ ready — opens in " + ClaudeCode.Workspace);
        }
        v.Loaded += async (_, _) => { if (ccStatus.Text.Length == 0) await CheckCc(onOpen: true); };

        var features = new WrapPanel();
        foreach (var (label, getE, set) in new (string, System.Linq.Expressions.Expression<Func<bool>>, Action<bool>)[]
        {
            ("AI operator (chat + tools)", () => s.AiFeatureOperator, v => s.AiFeatureOperator = v),
            ("Log analysis", () => s.AiFeatureLogAnalysis, v => s.AiFeatureLogAnalysis = v),
            ("AI reports", () => s.AiFeatureReports, v => s.AiFeatureReports = v),
            ("Help & FAQ answers", () => s.AiFeatureHelper, v => s.AiFeatureHelper = v),
            ("“✦ Explain / review with AI” actions (files, documents, invoices, hosts, keys, Vultr resources, logs)", () => s.AiFeatureFiles, v => s.AiFeatureFiles = v),
            ("Claude Code launcher", () => s.AiFeatureClaudeCode, v => s.AiFeatureClaudeCode = v),
        })
        {
            var c = v.Check(label, getE.Compile()(), set);
            if (getE.Body is System.Linq.Expressions.MemberExpression m) SettingTag.Set(c, m.Member.Name);   // 0.5.0: policy lock
            c.Margin = new Thickness(0, 6, 18, 0);
            features.Children.Add(c);
        }

        foundryRow = new FoundryPanel(() => model.Text.Trim(), m => { model.Text = m; s.FoundryModel = m; Apply(); });
        engine.SelectionChanged += (_, _) => ShowPanels();
        ShowPanels();

        return v.Card("AI engine",
            "Pick where models run — GPU, CPU-only for laptops, the NPU of a Copilot+ PC, any OpenAI-compatible server, or the Anthropic API — or switch AI off entirely. " +
            "Offline features (log digests, rule-based reports, help search) work in every mode.",
            v.Check("Enable AI features", s.AiEnabled, v => s.AiEnabled = v),
            v.L("ENGINE"), engine, engineHint,
            ollama, npu, openai, anthropic,
            v.L("MODEL"), v.Row(model, v.Btn("Load models", async () => await Load()), v.Btn("Test", async () => await Test()), v.Btn("Save", Apply, "PrimaryButton")),
            foundryRow!,
            result,
            v.L("THIS PC"), hw, v.Row(v.Btn("Detect hardware", async () => await Detect()), applyRec),
            v.L("FEATURES"), features,
            v.L("PRIVACY & POWER"),
            v.ProtectedCheck("ai.redactSecrets", "Mask secrets (keys, passwords, tokens) before anything is sent to a model"),
            CloudAiPanel(v, () => Id() switch { "openai" => oUrl.Text.Trim(), "anthropic" => aUrl.Text.Trim(), "npu" => fUrl.Text.Trim(), _ => url.Text.Trim() }),
            v.Check("Pause automatic AI work on battery power", s.AiPauseOnBattery, v => s.AiPauseOnBattery = v),
            v.L("CLAUDE CODE WITH A LOCAL MODEL (via Ollama's Anthropic-compatible API)"),
            v.Row(v.Labeled("MODEL", ccModel), v.Labeled("WORKSPACE FOLDER", ccWork)),
            v.Row(v.Btn("Check", async () => await CheckCc()), v.Btn("Open Claude Code (local)", async () => { ApplyCc(); await ClaudeCode.LaunchAsync(); }, "PrimaryButton"), v.Btn("Copy install command", () => Menus.Clip("irm https://claude.ai/install.ps1 | iex"))),
            ccStatus);
    }

    /// <summary>Speed, agents and the memory guard (ADR-034).</summary>
    public static Border Speed(ISettingsKit v)
    {
        var s = v.Settings;
        var steps = new ComboBox { Width = 90, ItemsSource = new[] { 4, 8, 12, 20, 30 }, SelectedItem = new[] { 4, 8, 12, 20, 30 }.Contains(s.AiMaxSteps) ? s.AiMaxSteps : 12, ToolTip = "Most model calls one question may make while it uses tools" };
        steps.SelectionChanged += (_, _) => { if (steps.SelectedItem is int n) { s.AiMaxSteps = n; v.Save(); } };
        var warm = new ComboBox { Width = 90, ItemsSource = new[] { 0, 2, 5, 10, 20, 30 }, SelectedItem = new[] { 0, 2, 5, 10, 20, 30 }.Contains(s.AiKeepWarmMinutes) ? s.AiKeepWarmMinutes : 10, ToolTip = "0 = unload the model right after each answer" };
        warm.SelectionChanged += (_, _) => { if (warm.SelectedItem is int n) { s.AiKeepWarmMinutes = n; v.Save(); } };
        var state = AiResourceGuard.Check();
        var now = new TextBlock { Style = (Style)v.FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Text = $"Now: {state.Text} · {(state.Level == AiPressure.Ok ? "no pressure" : state.Level.ToString().ToUpperInvariant() + " pressure")}" + (AiResourceGuard.LastNotice.Length > 0 ? "\nLast action: " + AiResourceGuard.LastNotice : "") };
        return v.Card("AI speed, agents & memory",
            "Answers stream as they are written; the model is loaded when you start typing and kept warm while you use the AI. " +
            "With tools, the AI works as an agent: it plans, calls tools, looks at the results and continues, up to the step limit (■ Stop cancels at any point). " +
            "The memory guard keeps a local model from pushing the PC into swap: under memory pressure it asks for a smaller context, unloads the model after the answer and unloads idle Foundry Local models. It never stops Ollama or Foundry.",
            v.Check("Stream answers as they are written", s.AiStream, v => s.AiStream = v),
            v.Row(v.Labeled("AGENT STEP LIMIT", steps), v.Labeled("KEEP THE MODEL WARM (minutes after last use)", warm)),
            v.Check("Memory guard: smaller context and unload after the answer when memory is tight", s.AiResourceGuard, v => s.AiResourceGuard = v),
            v.Check("Unload idle Foundry Local models (when memory is tight, or the app's own use has ended)", s.AiUnloadIdleFoundry, v => s.AiUnloadIdleFoundry = v),
            v.ProtectedCheck("ai.autoRunReadOnly", "AI Engineer: auto-run read-only commands in the target session (change, sensitive and destructive commands still ask; never in PRODUCTION)", async on =>
            {
                if (!await ProtectedSettings.ChangeAsync(on ? "turn on AI auto-run of read-only commands" : "turn off AI auto-run of read-only commands", "ai.autoRunReadOnly", on)) return false;
                AuditLog.Write("you", "ai", on ? "auto-run of read-only commands in the target session turned ON (Settings)" : "auto-run of read-only commands turned off (Settings)");
                return true;
            }),
            now);
    }

    /// <summary>What the AI may see and do (ADR-028). Every change it proposes still asks, whatever is set here.</summary>
    public static Border Permissions(ISettingsKit v)
    {
        var s = v.Settings;
        var timeout = new ComboBox { Width = 90, ItemsSource = new[] { 1, 2, 5, 10, 30 }, SelectedItem = s.AiCommandTimeoutMinutes, ToolTip = "A command that hasn't finished by then is reported as still running (press Stop to interrupt it)" };
        timeout.SelectionChanged += (_, _) => { if (timeout.SelectedItem is int m) { s.AiCommandTimeoutMinutes = m; v.Save(); } };
        return v.Card("AI permissions & safety",
            "Changes always ask first, and commands go only to the terminal you chose — never to another one, never in the background. " +
            "Anything the AI reads from servers, terminals, logs, documents or APIs is treated as data: it can't give the AI new permissions.",
            v.Check("Let the AI read status, logs and cloud / infrastructure inventories without asking (changes always ask)", s.AiAutoApproveReadOnly, v => s.AiAutoApproveReadOnly = v),
            v.Check("Let the AI deploy new cloud servers (Vultr) when I ask: it shows the plan, size, region and monthly price, and nothing is ordered until I approve", s.AiAllowServerDeploy, v => s.AiAllowServerDeploy = v),
            v.Check("AI Engineer: let me pick a live terminal and run the commands it proposes there (each command is shown and asks first; risky ones ask harder)", s.AiTerminalBridge, v => s.AiTerminalBridge = v),
            v.Check("Give the AI the output of the commands it ran (masked and shortened to the last 8,000 characters)", s.AiShareCommandOutput, v => s.AiShareCommandOutput = v),
            v.Row(v.Labeled("COMMAND TIME LIMIT (minutes)", timeout)),
            v.Check("Let the AI search and read my notes and incidents (asks before reading each one; off by default — notes are where secrets get pasted)", s.AiAllowNotes, v => s.AiAllowNotes = v));
    }

    /// <summary>"Allow internet AI providers" with the external-network treatment and a one-time confirmation when it is
    /// switched on. Off is the default: only local / on-premises endpoints are used.</summary>
    private static Border CloudAiPanel(ISettingsKit v, Func<string> endpoint)
    {
        var s = v.Settings;
        var c = new CheckBox { Content = new TextBlock { Text = "Enable external (internet) AI providers", TextWrapping = TextWrapping.Wrap }, IsChecked = s.AiAllowCloud, Margin = new Thickness(0, 4, 0, 0) };
        SettingTag.Set(c, nameof(AppSettings.AiAllowCloud));
        c.Click += async (_, _) =>
        {
            // The AI features lock guards this switch (0.2.0 phase 5): without the password nothing changes.
            if (FeatureLock.Gate(c.IsChecked == true ? "allow internet AI providers" : "stop internet AI providers",
                    () => Dialogs.Prompt("AI features lock", "Password for the AI features lock:", password: true)) is { } why)
            {
                c.IsChecked = s.AiAllowCloud;
                Dialogs.Info("Internet AI providers", why);
                return;
            }
            if (c.IsChecked == true)
            {
                var ep = endpoint();
                var cls = NetworkPolicy.Classify(ep);
                var shown = cls is NetClass.External or NetClass.UserConfigured && ep.Length > 0 ? ep : "the internet AI endpoint you configure (OpenAI, Anthropic, Azure OpenAI, gateways)";
                if (!NetUi.ConfirmEnable("The AI engine", shown)) { c.IsChecked = false; return; }
            }
            // Admin-protected too (0.3.0 §1): the switch shows what is in force afterwards.
            bool want = c.IsChecked == true;
            c.IsEnabled = false;
            try
            {
                if (await ProtectedSettings.ChangeAsync(want ? "allow internet AI providers" : "stop internet AI providers", "ai.allowCloud", want))
                    AuditLog.Write("you", "ai", want ? "external AI providers enabled" : "external AI providers disabled", want ? endpoint() : "");
            }
            finally { c.IsEnabled = true; c.IsChecked = s.AiAllowCloud; }
        };
        return NetUi.External("External network access",
            "Off (default): AI requests only go to this PC, private/LAN addresses or SSH tunnels. On: requests may go to internet providers, " +
            "so the text you choose to send (prompts, selected output, documents) leaves your network. Secrets are masked first when masking is on; " +
            "every “✦ … with AI” action still asks before using an internet provider.", c);
    }
}
