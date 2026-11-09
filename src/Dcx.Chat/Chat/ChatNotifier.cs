using System.Windows;
using CipherChat.Engine;
using DcxManager.Services;

namespace DcxManager.Chat;

/// <summary>
/// Windows notifications for new chat messages (0.6.39, ADR-117; setting/policy <c>ChatNotify</c>). A message raises one
/// when it is someone else's, new (not history being loaded), and its room isn't the one in front of you. The text is in
/// it only when <c>ChatToastPreviews</c> allows (<see cref="ChatPolicy.ToastText"/>); a burn message's text never is.
/// At most one notification per room every few seconds, so a busy room doesn't flood the screen. Nothing leaves the PC.
/// </summary>
internal static class ChatNotifier
{
    private const long FreshSeconds = 120;
    private static readonly TimeSpan PerRoom = TimeSpan.FromSeconds(8);
    private static readonly Dictionary<string, DateTime> Last = new();

    /// <summary>Self-test: the notifications raised (title, text), newest last.</summary>
    internal static readonly List<(string Title, string Text)> RaisedForTest = new();

    internal static void ResetForTest() { lock (Last) Last.Clear(); lock (RaisedForTest) RaisedForTest.Clear(); }

    /// <summary>A chat was unlocked: its new messages notify.</summary>
    public static void Attach(ChatSession s, ChatEngine e) =>
        e.EntryAdded += (r, en) => OnUi(() => { if (ReferenceEquals(s.Engine, e)) Consider(e, r, en); });

    private static void Consider(ChatEngine e, Room r, Entry en)
    {
        if (en.Message is not { Mine: false } m || !AppSettings.Current.ChatNotify) return;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - en.At > FreshSeconds) return;
        if (ReferenceEquals(e.Viewing, r) && AppInFront()) return;
        var key = r.Id;
        lock (Last)
        {
            if (Last.TryGetValue(key, out var t) && DateTime.UtcNow - t < PerRoom) return;
            Last[key] = DateTime.UtcNow;
        }
        string what = m.Burn > 0 ? "(a message that burns after reading)"
            : m.Text.Length > 0 ? Views.ChatRoomView.Clean(m.Text).ReplaceLineEndings(" ")
            : m.Files.Count > 0 ? (m.Files.Count == 1 ? "(a file)" : $"({m.Files.Count} files)") : "(a message)";
        var text = ChatPolicy.ToastText(Views.ChatRoomView.Clean(m.From), Views.ChatRoomView.Clean(r.Label(e.MyUser)), what);
        lock (RaisedForTest) { RaisedForTest.Add(("CipherChat", text)); if (RaisedForTest.Count > 100) RaisedForTest.RemoveAt(0); }
        DesktopToast.Show("CipherChat", text);
    }

    private static bool AppInFront() => Application.Current?.Windows.OfType<Window>().Any(w => w.IsActive) == true;

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) a(); else d.BeginInvoke(a);
    }
}
