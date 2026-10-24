using System.Windows;
using System.Windows.Threading;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>The WPF side of the seams in <see cref="Ui"/> (0.2.0 phase 1): registered once at start-up, before any
/// store or provider code runs. Each member forwards to what the app did directly before.</summary>
public static class WpfUi
{
    public static void Register()
    {
        Ui.Dialogs = new WpfDialogs();
        Ui.Window = new WpfWindowShell();
        Ui.Thread = new WpfUiThread();
        // 0.6.17 (ADR-096): a vault credential kept in Bitwarden read synchronously on the UI thread keeps it pumping, so
        // the unlock prompt can show.
        ExternalCredentials.WaitOnUi = t =>
        {
            if (t.IsCompleted) return;
            var frame = new DispatcherFrame();
            t.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        };
    }

    private sealed class WpfDialogs : IDialogs
    {
        public string? Prompt(string title, string message, string initial = "", bool password = false, bool multiline = false) => Dialogs.Prompt(title, message, initial, password, multiline);
        public (string? Text, bool Option) PromptPassword(string title, string message, string option, bool optionDefault = false) => Dialogs.PromptPassword(title, message, option, optionDefault);
        public bool Confirm(string title, string message, string action = "Continue", bool danger = false, string? typeToConfirm = null, (string Env, string Target)? banner = null) => Dialogs.Confirm(title, message, action, danger, typeToConfirm, banner);
        public int Choose(string title, string message, string[] options, int cancelIndex = -1, double width = 560) => Dialogs.Choose(title, message, options, cancelIndex, width);
        public void Info(string title, string message) => Dialogs.Info(title, message);
        public void Error(string title, string message, bool explain = true, Exception? ex = null) => Dialogs.Error(title, message, explain, ex);
        public void ShowText(string title, string text, bool modal = false) => Dialogs.ShowText(title, text, modal);
        public T? Pick<T>(string title, string message, IEnumerable<T> items, Func<T, string> label) where T : class => Dialogs.Pick(title, message, items, label);
    }

    private sealed class WpfWindowShell : IShell
    {
        // MainWindow.Instance is set when the window is constructed; before that (and after it closed) these are no-ops.
        private static MainWindow? Main => Application.Current?.MainWindow as MainWindow ?? (MainWindow.Instance is { } m ? m : null);
        public void Status(string text) => Main?.Status(text);
        public void Navigate(string key) => Main?.Navigate(key);
        public void Activate() => Main?.Activate();
        public void SaveWorkspaceNow() => Main?.SaveWorkspaceNow();
        public void SaveAllNow()
        {
            try { NotesView.Current?.SaveNow(); } catch { }
            try { IncidentsView.Current?.FlushPending(); } catch { }
            try { Main?.SaveWorkspaceNow(); } catch { }
        }
        public void OpenWeb(string title, string url, string? reuseKey = null) => WebWindow.Open(title, url, reuseKey);
        public void ShowDocument(Documents.DocumentSource doc) => DocumentViewerWindow.Open(doc);
        public void OpenTerminal(HostEntry host, string? initialCommand = null) => Main?.OpenTerminal(host, initialCommand);
        public string DescribeScreen() => ScreenContext.Describe();
        public void AddToIncident(Incidents.ItemKind kind, string title, string text) => IncidentUi.AddText(kind, title, text);
    }

    private sealed class WpfUiThread : IUiThread
    {
        private static Dispatcher? D => Application.Current?.Dispatcher;
        public bool HasUi => D != null && !D.HasShutdownStarted;
        public bool CheckAccess() => D?.CheckAccess() ?? true;
        public void Post(Action action) { if (D is { } d) d.BeginInvoke(action); else action(); }
        public void Invoke(Action action) { if (D is { } d && !d.CheckAccess()) d.Invoke(action); else action(); }
        public T Invoke<T>(Func<T> func) => D is { } d && !d.CheckAccess() ? d.Invoke(func) : func();
        public bool TryInvoke(Action action, TimeSpan timeout)
        {
            if (D is not { } d || d.CheckAccess()) { action(); return true; }
            bool ran = false;
            try { d.Invoke(() => { action(); ran = true; }, DispatcherPriority.Send, CancellationToken.None, timeout); } catch (TimeoutException) { }
            return ran;
        }
        public Task InvokeAsync(Action action) => D is { } d ? d.InvokeAsync(action).Task : Task.Run(action);
        public Task<T> InvokeAsync<T>(Func<T> func) => D is { } d ? d.InvokeAsync(func).Task : Task.Run(func);
        public IDisposable Every(TimeSpan interval, Action tick)
        {
            if (D is not { } d) return new Timer(_ => tick(), null, interval, interval);
            var t = new DispatcherTimer(interval, DispatcherPriority.Background, (_, _) => tick(), d);
            t.Start();
            return new Stopper(t);
        }
        private sealed class Stopper(DispatcherTimer t) : IDisposable { public void Dispose() => t.Stop(); }
    }
}
