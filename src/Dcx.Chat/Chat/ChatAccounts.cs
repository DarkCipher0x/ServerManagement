using System.IO;
using CipherChat.Engine;
using DcxManager.Services;

namespace DcxManager.Chat;

/// <summary>A CipherChat server this PC is paired with (ADR-117). The device token signs this PC in to the chat without
/// a password (as in CipherChat's own apps); it is kept only in the encrypted store. The chat passphrase is never stored.</summary>
public sealed record ChatAccount(string Origin, string User, string DeviceId, string DeviceToken, string Name, DateTime AddedUtc)
{
    /// <summary>"user@host" as shown.</summary>
    public string Display => User + "@" + (Uri.TryCreate(Origin, UriKind.Absolute, out var u) ? u.Host : Origin);
    public string Key => Origin + "|" + User;
}

/// <summary>The paired accounts, in <c>chat-accounts.dcx</c> (AES-256-GCM under the store key, like every store file).</summary>
public static class ChatAccounts
{
    public const string FileName = "chat-accounts.dcx";
    private static readonly object Gate = new();
    private static List<ChatAccount>? _all;

    public static event Action? Changed;

    public static IReadOnlyList<ChatAccount> All { get { lock (Gate) return (_all ??= Load()).ToList(); } }

    private static List<ChatAccount> Load()
    {
        try { return SecureStore.Read<List<ChatAccount>>(FileName).Where(Valid).ToList(); }
        catch (Exception ex) { Log.Write("Chat accounts", ex); return new(); }
    }

    /// <summary>What a stored account must look like to be used (a damaged or tampered file can't point the chat elsewhere).</summary>
    public static bool Valid(ChatAccount a) =>
        ChatServers.Check(a.Origin) == null && a.User.Length is > 0 and <= 64 && a.DeviceToken.Length is > 0 and <= 512 && a.DeviceId.Length <= 128 && a.Name.Length <= 120;

    /// <summary>Adds or replaces (same server and person) an account, newest first.</summary>
    public static void Save(ChatAccount account)
    {
        if (!Valid(account)) throw new ArgumentException("That chat account isn't valid.");
        lock (Gate)
        {
            _all ??= Load();
            _all.RemoveAll(x => x.Key == account.Key);
            _all.Insert(0, account);
            SecureStore.Write(FileName, _all);
        }
        Changed?.Invoke();
    }

    public static void Remove(ChatAccount account)
    {
        lock (Gate)
        {
            _all ??= Load();
            if (_all.RemoveAll(x => x.Key == account.Key) == 0) return;
            SecureStore.Write(FileName, _all);
        }
        Changed?.Invoke();
    }

    /// <summary>Forgets the cached list (after a restore or for the self-test).</summary>
    internal static void Reset() { lock (Gate) _all = null; }
}

/// <summary>Seals CipherChat's per-PC key store (room keys this PC has opened) with ServerManagement's store key instead of
/// DPAPI, so it is encrypted the same way as everything else the app keeps and opens only with the app's unlock.</summary>
internal sealed class SecureStoreGuard : IStoreGuard
{
    private const string Context = "cipherchat-keystore";
    public byte[] Protect(byte[] plain) => SecureStore.Encrypt(plain, Context);
    public byte[] Unprotect(byte[] sealed_) => SecureStore.Decrypt(sealed_, Context);

    /// <summary>Where this person's key store for this server lives: inside the app's data folder.</summary>
    public static string PathFor(string origin, string user) => KeyStore.PathFor(Path.Combine(SecureStore.DataDir, "chat", "keys"), origin, user);
}
