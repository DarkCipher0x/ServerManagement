using System.Windows;
using System.Windows.Controls;
using DcxManager.Ai;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// Settings → AI engine → Foundry Local (ADR-026): what's true now (installed / service / model), automatic set-up and
/// repair, a button for every step, and a model guide built from THIS PC's catalogue (grouped by NPU / GPU / CPU, with
/// size, downloaded, tool use and what each is good for; the recommendation on top).
/// </summary>
public sealed class FoundryPanel : StackPanel
{
    private readonly Func<string> _getModel;
    private readonly Action<string> _setModel;
    private readonly TextBlock _installed = Line(), _service = Line(), _model = Line(), _progress = Line();
    private readonly StackPanel _guide = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _setup;
    private bool _refreshing;

    private static TextBlock Line() => new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };

    public FoundryPanel(Func<string> getModel, Action<string> setModel)
    {
        _getModel = getModel; _setModel = setModel;
        var s = AppSettings.Current;
        TextBlock Label(string t) => new() { Text = t, Style = (Style)FindResource("Label"), Margin = new Thickness(0, 10, 0, 4) };
        Button B(string t, string tip, Func<Task> a, string? style = null)
        {
            var b = new Button { Content = t, ToolTip = tip, Margin = new Thickness(0, 0, 8, 6) };
            if (style != null) b.Style = (Style)FindResource(style);
            b.Click += async (_, _) => { try { b.IsEnabled = false; await a(); } finally { b.IsEnabled = true; } };
            return b;
        }

        var auto = new CheckBox
        {
            Content = new TextBlock { Text = "Set up and repair automatically — install Foundry Local, start it, download and load the model, and restart the service if it stops answering", TextWrapping = TextWrapping.Wrap },
            IsChecked = s.FoundryAutoSetup, Margin = new Thickness(0, 4, 0, 6),
        };
        auto.Click += (_, _) => { AppSettings.Current.FoundryAutoSetup = auto.IsChecked == true; AppSettings.Save(); if (auto.IsChecked == true) _ = RunAsync(byUser: true); };
        Children.Add(auto);

        Children.Add(Label("STATUS"));
        _progress.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        foreach (var t in new[] { _installed, _service, _model, _progress }) Children.Add(t);

        _setup = B("Set up now", "Install (if needed), start, repair, download and load the model — the same steps as the automatic set-up", () => RunAsync(byUser: true), "PrimaryButton");
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(_setup);
        buttons.Children.Add(B("Restart service", "foundry server restart", async () => { await Cli("server restart", "restarting the service"); }));
        buttons.Children.Add(B("Stop service", "foundry server stop (frees the memory the model uses)", async () => { await Cli("server stop", "stopping the service"); }));
        buttons.Children.Add(B("Unload model", "foundry model unload <model> (frees its memory; the next request or Set up now loads it again)", async () => { await Cli("model unload " + _getModel(), "unloading the model"); }));
        buttons.Children.Add(B("Show log", "foundry server logs", async () =>
        {
            var (code, output) = await AiClient.FoundryCli("server logs", TimeSpan.FromSeconds(30), default);
            Dialogs.ShowText("Foundry Local log", code == -1 ? "Foundry Local isn't installed." : output.Trim().Length == 0 ? "(the log is empty)" : output);
        }));
        buttons.Children.Add(B("Copy the commands", "The same steps for a terminal", () =>
        {
            Menus.Clip($"winget install --id Microsoft.FoundryLocal -e\nfoundry server start --idle-timeout 0\nfoundry model load {_getModel()}\nfoundry status");
            return Task.CompletedTask;
        }));
        Children.Add(buttons);

        Children.Add(Label("WHICH MODEL — WHAT THIS PC CAN RUN"));
        var hint = new TextBlock
        {
            Text = "Bigger models answer better but need more memory and are slower. The AI operator needs a model that can use tools (✓ tools). " +
                   "NPU builds run at low power; GPU builds are fastest on a graphics card; CPU builds run anywhere. “Use” sets the model and loads it (downloading it the first time).",
            Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap,
        };
        Children.Add(hint);
        var show = B("Show the models for this PC", "foundry model list (this PC's catalogue: the builds Foundry Local offers for its NPU, GPU and CPU)", ShowModelsAsync);
        show.HorizontalAlignment = HorizontalAlignment.Left; show.Margin = new Thickness(0, 6, 0, 6);
        Children.Add(show);
        Children.Add(_guide);

        Loaded += OnLoaded;
        Unloaded += (_, _) => FoundryManager.Progress -= OnProgress;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        FoundryManager.Progress -= OnProgress;
        FoundryManager.Progress += OnProgress;
        if (FoundryManager.LastStatus.Length > 0) _progress.Text = FoundryManager.LastStatus;
        await RefreshAsync(shared: true);
    }

    private void OnProgress(string text) => Dispatcher.BeginInvoke(() =>
    {
        _progress.Text = text;
        if (text.StartsWith("✓") || text.StartsWith("✕")) _ = RefreshAsync();
    });

    private async Task RunAsync(bool byUser)
    {
        _setup.IsEnabled = false;
        try { await FoundryManager.EnsureReadyAsync(byUser); }
        finally { _setup.IsEnabled = true; await RefreshAsync(); }
    }

    private async Task Cli(string args, string doing)
    {
        _progress.Text = $"… {doing}";
        var r = await FoundryManager.CliAsync(args, TimeSpan.FromMinutes(3), default);
        AiClient.ForgetFoundry();
        AuditLog.Write("you", "Foundry Local", "foundry " + args, r.Code == 0 ? "ok" : r.Error);
        _progress.Text = r.Missing ? "✕ Foundry Local isn't installed." : r.Code == 0 ? $"✓ done: foundry {args}" : $"✕ foundry {args}: {r.Error}";
        await RefreshAsync();
    }

    /// <summary>What's true now — local commands only (nothing is installed, started or downloaded by looking).</summary>
    /// <param name="shared">On opening: share a check that is already running (see FoundryManager.SharedHealthAsync).
    /// After an action the answer must be fresh.</param>
    internal async Task RefreshAsync(bool shared = false)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var h = shared ? await FoundryManager.SharedHealthAsync() : await FoundryManager.HealthAsync();
            _installed.Text = h.Installed ? $"✓ Foundry Local {h.Version} is installed." : "✕ Foundry Local isn't installed." + (AppSettings.Current.FoundryAutoSetup ? " Set up now installs it." : "");
            _service.Text = !h.Installed ? "" : h.Reachable ? $"✓ The service answers at {h.Url}." : h.Running ? $"⚠ The service says it's running{(h.Url != null ? $" at {h.Url}" : "")} but doesn't answer — Set up now repairs it." : "✕ The service isn't running.";
            if (!h.Reachable) { _model.Text = $"Model: {_getModel()} (not loaded)"; return; }
            var loaded = await FoundryManager.LoadedAsync();
            var want = _getModel();
            var match = loaded.FirstOrDefault(m => m.Alias.Equals(want, StringComparison.OrdinalIgnoreCase) || m.Bare.Equals(want.Split(':')[0], StringComparison.OrdinalIgnoreCase));
            _model.Text = match != null ? $"✓ {match.Alias} is loaded on the {match.DeviceText} ({match.SizeText})."
                : loaded.Count > 0 ? $"⚠ {want} isn't loaded (loaded: {string.Join(", ", loaded.Select(m => $"{m.Alias} {m.DeviceText}"))})." : $"✕ {want} isn't loaded.";
        }
        catch (Exception ex) { _progress.Text = "✕ " + ex.Message.Split('\n')[0]; }
        finally { _refreshing = false; }
    }

    internal async Task ShowModelsAsync()
    {
        _guide.Children.Clear();
        _guide.Children.Add(new TextBlock { Text = "reading this PC's catalogue…", Style = (Style)FindResource("Hint") });
        List<FoundryModel> variants;
        try { variants = await FoundryManager.CatalogAsync(variants: true); }
        catch (Exception ex) { _guide.Children.Clear(); _guide.Children.Add(new TextBlock { Text = "✕ " + ex.Message.Split('\n')[0], TextWrapping = TextWrapping.Wrap }); return; }
        var hw = await AiHardware.DetectAsync();
        var h = await FoundryManager.SharedHealthAsync();
        ShowModels(variants, FoundryManager.Recommend(variants, hw.RamBytes, hw.BestVram, h.Npus.Count > 0 || hw.Npu != null));
    }

    /// <summary>Builds the guide from a catalogue (the self-test passes a fixed one).</summary>
    internal void ShowModels(IReadOnlyList<FoundryModel> variants, (FoundryModel Model, string Why)? rec)
    {
        _variants = variants;
        _guide.Children.Clear();
        if (rec is { } r) _guide.Children.Add(Row(r.Model, $"★ Recommended for this PC — {r.Why}", strong: true));
        foreach (var dev in new[] { ("npu", "NPU — low power (Copilot+ PCs)"), ("gpu", "GPU — fastest on a graphics card"), ("cpu", "CPU — runs anywhere") })
        {
            var list = variants.Where(v => v.Device == dev.Item1).OrderBy(v => v.SizeMb).ToList();
            if (list.Count == 0) continue;
            _guide.Children.Add(new TextBlock { Text = dev.Item2, Style = (Style)FindResource("Label"), Margin = new Thickness(0, 10, 0, 2) });
            foreach (var v in list) _guide.Children.Add(Row(v, FoundryManager.GoodFor(v.Alias), strong: false));
        }
        if (variants.Count == 0) _guide.Children.Add(new TextBlock { Text = "Foundry Local offers no chat model for this PC.", TextWrapping = TextWrapping.Wrap });
    }

    private FrameworkElement Row(FoundryModel m, string note, bool strong)
    {
        var use = new Button { Content = "Use", MinWidth = 60, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top, ToolTip = $"Use {m.Id} and load it" };
        if (strong) use.Style = (Style)FindResource("PrimaryButton");
        use.Click += async (_, _) => { _setModel(m.Alias == "" ? m.Id : PreferredName(m)); await RunAsync(byUser: true); };
        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = $"{m.Alias} · {m.DeviceText} · {m.SizeText} · {(m.Cached ? "downloaded" : "not downloaded")} · {(m.Tools ? "✓ tools" : "no tools")}",
            FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap,
        });
        if (note.Length > 0) text.Children.Add(new TextBlock { Text = note, Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap });
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        DockPanel.SetDock(use, Dock.Left);
        row.Children.Add(use); row.Children.Add(text);
        return row;
    }

    /// <summary>The alias when this build is the one the alias picks here (portable to another PC); the exact build otherwise
    /// (e.g. the CPU build on a PC with a GPU).</summary>
    private string PreferredName(FoundryModel m) => FoundryManager.Resolve(m.Alias, _variants)?.Id == m.Id ? m.Alias : m.Bare;
    private IReadOnlyList<FoundryModel> _variants = Array.Empty<FoundryModel>();
}
