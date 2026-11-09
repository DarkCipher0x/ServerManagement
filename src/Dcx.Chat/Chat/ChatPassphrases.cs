using DcxManager.Services;

namespace DcxManager.Chat;

/// <summary>
/// The saved chat passphrase (0.6.42; DarkCipher0x: "i also want a way to save the chat passphrase. Since the app is already
/// encrypted with windows hello, it would be good to have the same option for the chat passphrase").
/// <para>Opt-in per account (ticked at the unlock), kept with the app's other secrets (<see cref="AppSettings.ObserveSecrets"/>:
/// AES-256-GCM in the app's store, never in the registry, a backup or a policy) and only while that store is itself
/// protected: Windows Hello sign-in (the Windows-account copy of its key is then removed) or a master password required at
/// start. Without that anyone signed in to Windows could open it, so it isn't offered, and one saved before is forgotten.</para>
/// <list type="bullet">
/// <item>The app starts or is unlocked (you just verified with Windows Hello or the master password): every chat with a
/// saved passphrase unlocks by itself.</item>
/// <item>A chat locked on its own (its lock, the idle auto-lock): <b>Unlock with Windows Hello</b> asks Hello first, then
/// uses the saved passphrase.</item>
/// <item>A passphrase the chat refuses (changed on another device) is forgotten and asked for again. Signing out,
/// removing the account, the policy (<see cref="AppSettings.ChatAllowRemember"/>) or "Forget" deletes it.</item>
/// </list>
/// It is never sent anywhere: it opens the identity on this PC exactly as typing it would.
/// </summary>
public static class ChatPassphrases
{
    private const string Prefix = "chat:pass:";

    /// <summary>Self-test: the app's store counts as protected (the self-test store has no Hello or master password).</summary>
    internal static bool ProtectedForTest { get; set; }

    /// <summary>The app's store opens only with Windows Hello or the master password, so a secret in it is as safe as the app.</summary>
    public static bool StoreProtected => ProtectedForTest || SecureStore.HasHello || SecureStore.PasswordRequiredOnStart;

    /// <summary>Saving may be offered: the policy allows it and the store is protected.</summary>
    public static bool CanSave => AppSettings.Current.ChatAllowRemember && StoreProtected;

    private static string Key(string accountKey) => Prefix + accountKey;

    /// <summary>The account a session is for (server and user, as <see cref="ChatAccount.Key"/>), paired or not.</summary>
    public static string KeyOf(ChatSession s) => s.Origin is { Length: > 0 } o && s.User is { Length: > 0 } u ? o + "|" + u : "";

    /// <summary>A passphrase is saved for this account (and may be used).</summary>
    public static bool IsSaved(string accountKey) => Saved(accountKey) is not null;

    private static string? Saved(string accountKey)
    {
        if (accountKey.Length == 0) return null;
        return CanSave && AppSettings.Current.ObserveSecrets.GetValueOrDefault(Key(accountKey)) is { Length: > 0 } p ? p : null;
    }

    /// <summary>Saves the passphrase that just unlocked this account (only when allowed; audited, never the value).</summary>
    public static void Save(string accountKey, string passphrase, string display)
    {
        if (!CanSave || accountKey.Length == 0 || passphrase.Length == 0) return;
        AppSettings.Current.ObserveSecrets[Key(accountKey)] = passphrase;
        AppSettings.Save();
        AuditLog.Write("you", "chat", "chat passphrase saved on this PC (encrypted; unlocks with Windows Hello)", display);
    }

    /// <summary>Deletes a saved passphrase (sign-out, removal, a refusal, the policy, "Forget").</summary>
    public static void Forget(string accountKey, string display, string why)
    {
        if (accountKey.Length == 0 || !AppSettings.Current.ObserveSecrets.Remove(Key(accountKey))) return;
        try { AppSettings.Save(); } catch (Exception ex) { Log.Write("Chat: forget the saved passphrase", ex); }
        AuditLog.Write("you", "chat", "saved chat passphrase forgotten", display + " — " + why);
    }

    /// <summary>Deletes every saved passphrase when saving is no longer allowed (policy off, or the store lost its protection).</summary>
    public static void ForgetAllIfNotAllowed()
    {
        if (CanSave) return;
        var keys = AppSettings.Current.ObserveSecrets.Keys.Where(k => k.StartsWith(Prefix, StringComparison.Ordinal)).ToList();
        if (keys.Count == 0) return;
        foreach (var k in keys) AppSettings.Current.ObserveSecrets.Remove(k);
        try { AppSettings.Save(); } catch (Exception ex) { Log.Write("Chat: forget the saved passphrases", ex); }
        AuditLog.Write("app", "chat", "saved chat passphrases forgotten", AppSettings.Current.ChatAllowRemember
            ? "the app's store is no longer protected by Windows Hello or a master password" : "saving them was switched off (policy)");
    }

    public enum Result { Unlocked, NotSaved, Cancelled, Refused }

    /// <summary>Unlocks a locked chat with its saved passphrase. <paramref name="askHello"/>: verify with Windows Hello first
    /// (a chat locked on its own); false right after the app itself was unlocked. A refused passphrase is forgotten.</summary>
    public static async Task<Result> UnlockAsync(ChatSession s, bool askHello, string why, IProgress<string>? progress = null)
    {
        ForgetAllIfNotAllowed();
        var key = KeyOf(s);
        if (s.Stage != ChatSession.Stages.NeedsUnlock || Saved(key) is not { } pass) return Result.NotSaved;
        if (askHello)
        {
            var hello = await WindowsHello.ConfirmAsync("Unlock the chat " + s.Display);
            if (hello != WindowsHello.Outcome.Verified)
            {
                if (hello == WindowsHello.Outcome.NotAvailable) return Result.NotSaved;   // the passphrase field is still there
                return Result.Cancelled;
            }
        }
        try
        {
            await s.UnlockAsync(pass, progress);
            AuditLog.Write("you", "chat", "chat unlocked with the saved passphrase", s.Display + " — " + why);
            return Result.Unlocked;
        }
        catch (UnauthorizedAccessException)
        {
            Forget(key, s.Display, "the chat refused it (changed on another device?)");
            return Result.Refused;
        }
    }

    /// <summary>The app started or was unlocked: every locked chat with a saved passphrase unlocks (no prompt: you just
    /// verified). Failures leave that chat locked with its passphrase field.</summary>
    public static async Task AutoUnlockAllAsync(string why)
    {
        ForgetAllIfNotAllowed();
        foreach (var s in ChatHub.Sessions.Where(x => x.Stage == ChatSession.Stages.NeedsUnlock && IsSaved(KeyOf(x))))
        {
            try { await UnlockAsync(s, askHello: false, why).ConfigureAwait(false); }
            catch (Exception ex) when (ex is CipherChat.Api.ApiException or InvalidOperationException or System.IO.IOException or System.Net.Http.HttpRequestException)
            { Log.Write("Chat: unlock with the saved passphrase " + s.Display, ex); }
        }
    }
}
