using CipherChat.Api;
using DcxManager.Chat.Admin;
using DcxManager.Services;

namespace DcxManager.Chat;

/// <summary>
/// The one place that holds the chat sessions of this run (ADR-117): one <see cref="ChatSession"/> per paired account,
/// plus the in-memory administration sign-in. The screens, the side panel and the dashboard cards all read from here, so
/// a room opened in a tab and in the panel is the same room on the same engine.
/// Locking the app (or quitting) wipes every chat's keys and ends the admin sign-in; the PC stays paired.
/// </summary>
public static class ChatHub
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ChatSession> ByKey = new();

    /// <summary>The administration sign-in (memory only; one at a time).</summary>
    public static ChatAdminClient Admin { get; } = new();

    /// <summary>A session was added, removed, or changed stage (sign-in, unlock, lock).</summary>
    public static event Action? Changed;

    public static IReadOnlyList<ChatSession> Sessions { get { lock (Gate) return ByKey.Values.ToList(); } }

    /// <summary>The session of a paired account, made (signed out) on first use.</summary>
    public static ChatSession For(ChatAccount account)
    {
        lock (Gate)
        {
            if (ByKey.TryGetValue(account.Key, out var s)) return s;
            s = new ChatSession();
            s.StageChanged += () => Changed?.Invoke();
            ByKey[account.Key] = s;
            return s;
        }
    }

    /// <summary>Adds a session that signed in without a saved pairing (password + code, or a new pairing code).</summary>
    public static void Adopt(ChatSession session)
    {
        var key = session.Account?.Key ?? (session.Origin + "|" + session.User);
        lock (Gate)
        {
            if (ByKey.TryGetValue(key, out var old) && !ReferenceEquals(old, session)) old.Lock();
            ByKey[key] = session;
        }
        session.StageChanged -= Raise;
        session.StageChanged += Raise;
        Raise();
    }

    public static void Forget(ChatSession session)
    {
        lock (Gate) foreach (var k in ByKey.Where(kv => ReferenceEquals(kv.Value, session)).Select(kv => kv.Key).ToList()) ByKey.Remove(k);
        Raise();
    }

    private static void Raise() => Changed?.Invoke();

    /// <summary>Signs the saved pairings back in (no passphrase: the chat stays locked until you unlock it). Failures
    /// leave that account signed out with its reason; nothing is retried in a loop.</summary>
    public static async Task ResumeSavedAsync()
    {
        foreach (var a in ChatAccounts.All)
        {
            var s = For(a);
            if (s.Api is not null) continue;
            try { await s.ResumeAsync(a).ConfigureAwait(false); }
            catch (Exception ex) when (ex is ApiException or InvalidOperationException or ArgumentException) { s.LastError = ex.Message; Log.Write("Chat resume " + a.Display, ex); }
        }
        Raise();
        await ChatPassphrases.AutoUnlockAllAsync("the app started").ConfigureAwait(false);   // 0.6.42: saved passphrases only
    }

    /// <summary>Unread messages over every unlocked chat (the dashboard card and the sidebar badge).</summary>
    public static int Unread => Sessions.Sum(s => s.Engine?.Rooms.Sum(r => r.Unread) ?? 0);

    /// <summary>App lock and quit: every chat's keys are wiped, the admin sign-in ends; pairings stay.</summary>
    public static void LockAll(string why)
    {
        foreach (var s in Sessions) s.Lock();
        Admin.SignOutQuietly();
        if (Sessions.Count > 0) AuditLog.Write("app", "chat", "chats locked", why);
        Raise();
    }
}
