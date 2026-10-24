using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Ssh;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using DcxManager.Sessions;

namespace DcxManager.Views;

// TerminalView: terminal profiles, the find bar and session logging (ADR-019) (moved unchanged from TerminalView.cs in 0.2.0 phase 6½).
public sealed partial class TerminalView
{
    // ---- terminal features (ADR-019) ---------------------------------------------------------------------------------

    public TerminalProfile Profile => _profile;
    /// <summary>True when this tab switched profile for this session only.</summary>
    public bool SessionProfile => _sessionProfile;
    internal string? LogPath => _log?.Path;
    internal string LastInput { get; private set; } = "";

    private static IEnumerable<TerminalView> AllTerminals() => MainWindow.Instance.Panes.AllTabs.Select(t => t.Content).OfType<TerminalView>();

    /// <summary>Applies a profile to this tab (colours, cursor, scrollback, keys; encoding from the next output on).</summary>
    public void ApplyProfile(TerminalProfile p, bool session)
    {
        _profile = p;
        _sessionProfile = session;
        lock (_decodeGate) _decoder = RemoteEncoding.GetDecoder();
        Post(TerminalProfiles.PageMessage(p));
        if (p.Log && _log == null && IsConnected) StartLog();
        SetStatus($"{(IsConnected ? "● " : "○ ")}{_target.Endpoint} · profile “{p.Name}”{(session ? " (this session)" : "")}");
    }

    public void SetLogging(bool on)
    {
        _logOn = on;
        if (on && _log == null && IsConnected) StartLog();
        if (!on && _log != null) { var l = _log; _log = null; l.Dispose(); SetStatus($"● {_target.Endpoint} · session log closed"); }
        else if (on && _log == null) SetStatus($"○ {_target.Endpoint} · logging starts when the session connects");
    }

    private void StartLog()
    {
        if (_log != null) return;
        if (!SecureStore.IsUnlocked) { SetStatus($"● {_target.Endpoint} · not logging: the store is locked"); return; }
        try
        {
            _log = new SessionLogger($"{_target.Title} · {_target.Endpoint}", _profile.LogTimestamps);
            AuditLog.Write("you", _target.Title, "session logging started", System.IO.Path.GetFileName(_log.Path));
        }
        catch (Exception ex) { App.Log("Session log start", ex); SetStatus($"● {_target.Endpoint} · couldn't start the session log: {ex.Message}"); }
    }

    private void BuildFindBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "⌕", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        row.Children.Add(_findBox);
        Button B(string t, string tip, Action a) { var b = new Button { Content = t, ToolTip = tip, Style = (Style)Application.Current.Resources["SmallButton"], Margin = new Thickness(4, 0, 0, 0) }; b.Click += (_, _) => a(); row.Children.Add(b); return b; }
        B("▲", "Older match (Enter)", () => _ = FindAsync(_findBox.Text, "prev"));
        B("▼", "Newer match (Shift+Enter)", () => _ = FindAsync(_findBox.Text, "next"));
        row.Children.Add(_findCase); row.Children.Add(_findRegex);
        _findCount.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _findCount.Fs(FontStep.Small);
        row.Children.Add(_findCount);
        B("✕", "Close (Esc)", () => ShowFind(false));
        _findBar.Child = row;
        _findBar.SetResourceReference(Border.BackgroundProperty, "Surface");
        _findBar.SetResourceReference(Border.BorderBrushProperty, "Line");
        _findBox.Mono();
        _findBox.TextChanged += (_, _) => _ = FindAsync(_findBox.Text, "prev");
        _findCase.Click += (_, _) => _ = FindAsync(_findBox.Text, "prev");
        _findRegex.Click += (_, _) => _ = FindAsync(_findBox.Text, "prev");
        _findBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _ = FindAsync(_findBox.Text, (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? "next" : "prev"); e.Handled = true; }
            else if (e.Key == Key.Escape) { ShowFind(false); e.Handled = true; }
        };
        Menus.ForText(_findBox);
    }

    public void ShowFind(bool show)
    {
        _findBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) { _findBox.Focus(); _findBox.SelectAll(); }
        else { Post(new { t = "clearsel" }); _findCount.Text = ""; Post(new { t = "focus" }); }
    }

    internal bool FindVisible => _findBar.Visibility == Visibility.Visible;
    internal string FindCountText => _findCount.Text;

    public sealed record FindResult(int Index, int Count, string? Error);
    private string _lastQuery = "";

    private readonly Dictionary<int, TaskCompletionSource<object?>> _waits = new();
    private int _waitSeq;

    private async Task<object?> AskPageAsync(object message, int id, int ms = 5000)
    {
        if (_disposed || !_pageReady) return null;
        var tcs = new TaskCompletionSource<object?>();
        _waits[id] = tcs;
        Post(message);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(ms));
        _waits.Remove(id);
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    /// <summary>Searches the scrollback (in the page) and selects the match. "prev" = towards older output.</summary>
    public async Task<FindResult?> FindAsync(string query, string dir, bool? matchCase = null, bool? regex = null)
    {
        Flush();
        _lastQuery = query;
        var id = ++_waitSeq;
        return await AskPageAsync(new { t = "find", id, q = query, dir, cs = matchCase ?? _findCase.IsChecked == true, re = regex ?? _findRegex.IsChecked == true }, id) as FindResult;
    }

    /// <summary>What the page applied (self-test).</summary>
    internal async Task<JsonElement?> ProbeAsync()
    {
        var id = ++_waitSeq;
        return await AskPageAsync(new { t = "probe", id }, id) is JsonElement e ? e : null;
    }

    /// <summary>A key press through the page's own key handler (self-test).</summary>
    internal void PressKey(string code, string key, bool ctrl = false, bool alt = false, bool shift = false) => Post(new { t = "key", code, key, ctrl, alt, shift });

    private Border? _inputBanner;

    /// <summary>Shows/hides the "input goes to n terminals" strip (n = 0 hides it).</summary>
    internal void ShowInputBroadcast(int members)
    {
        if (_disposed || Content is not DockPanel root) return;
        if (members == 0) { if (_inputBanner != null) { root.Children.Remove(_inputBanner); _inputBanner = null; } return; }
        if (_inputBanner == null)
        {
            var text = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Bg");
            var stop = new Button { Content = "■ Stop", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1) };
            stop.Click += (_, _) => InputBroadcast.Stop("stopped by you");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(text); row.Children.Add(stop);
            _inputBanner = new Border { Child = row, Padding = new Thickness(10, 4, 10, 4) };
            _inputBanner.SetResourceReference(Border.BackgroundProperty, "Warn");
            DockPanel.SetDock(_inputBanner, Dock.Top);
            root.Children.Insert(0, _inputBanner);
        }
        ((TextBlock)((StackPanel)_inputBanner.Child).Children[0]).Text = $"⌨ INPUT GOES TO {members} TERMINALS — what you type here is sent to all of them";
    }

    internal bool InputBannerShown => _inputBanner != null;
}
