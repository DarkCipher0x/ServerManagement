using System.IO;
using System.Text.Json.Nodes;
using CipherChat.Api;
using CipherChat.Engine;
using DcxManager.Services;

namespace DcxManager.Chat;

/// <summary>
/// One signed-in CipherChat account (ADR-117): ServerManagement's counterpart of CipherChat's own <c>Session</c>, with the
/// same steps and rules, and ServerManagement's storage. Steps: sign in (a saved pairing, a new pairing code, or password +
/// authenticator code) → <see cref="Stage"/> says what's next (create the identity, unlock it with the chat passphrase,
/// confirm the owner's safety number) → the engine runs. The passphrase is only ever a parameter of the call that needs
/// it, never stored or sent (CipherChat's rule). <see cref="Lock"/> wipes the keys; the PC stays paired.
/// </summary>
public sealed class ChatSession
{
    public const string UserAgent = "CipherChat/0.2.0 (ServerManagement; Windows)";

    public enum Stages { SignedOut, NeedsIdentity, NeedsUnlock, NeedsOwnerCheck, Ready }

    public ChatAccount? Account { get; private set; }
    public ChatApi? Api { get; private set; }
    public JsonObject? State { get; private set; }
    public ChatEngine? Engine { get; private set; }
    /// <summary>The unlocked identity while the owner's safety number is being compared (before the engine starts).</summary>
    private Me? _pendingMe;

    /// <summary>Asks for the authenticator code when the server wants it again (set by the screen).</summary>
    public AskCode Ask { get; set; } = _ => Task.FromResult<string?>(null);

    public event Action? StageChanged;

    /// <summary>Why the last automatic step failed (a saved pairing that couldn't sign in), shown on the account; null = fine.</summary>
    public string? LastError { get; internal set; }

    public Stages Stage =>
        Engine != null ? Stages.Ready
        : _pendingMe != null ? Stages.NeedsOwnerCheck
        : State?["me"] is not JsonObject me ? Stages.SignedOut
        : me["identity_fp"] is null || me["identity_fp"]!.GetValueKind() == System.Text.Json.JsonValueKind.Null ? Stages.NeedsIdentity
        : Stages.NeedsUnlock;

    public string User => State?["me"]?["user"]?.GetValue<string>() ?? Account?.User ?? "";
    public string Origin => Api?.Origin.GetLeftPart(UriPartial.Authority) ?? Account?.Origin ?? "";
    public string Display => User + "@" + (Uri.TryCreate(Origin, UriKind.Absolute, out var u) ? u.Host : Origin);

    private ChatApi NewApi(Uri origin)
    {
        var api = new ChatApi(ChatServers.TransportFor(origin), UserAgent);
        // A paired PC signs itself back in when its session ends (sessions idle out; the device token lasts longer).
        api.Resume = async () =>
        {
            if (Account is null) return false;
            try { await api.DeviceSignInAsync(Account.DeviceToken).ConfigureAwait(false); return true; }
            catch (ApiException) { return false; }
        };
        return api;
    }

    /// <summary>Signs in with a saved pairing.</summary>
    public async Task ResumeAsync(ChatAccount account)
    {
        Account = account;
        Api = NewApi(new Uri(account.Origin));
        await Api.DeviceSignInAsync(account.DeviceToken).ConfigureAwait(false);
        LastError = null;
        await RefreshStateAsync().ConfigureAwait(false);
        AuditLog.Write("you", "chat", "signed in (paired device)", account.Display);
    }

    /// <summary>A pairing code from another CipherChat device (in the web chat: /pair): sign in with it and remember this PC.</summary>
    public async Task PairAsync(string code)
    {
        var parsed = Pairing.Parse(code.Trim()) ?? throw new ArgumentException("That isn't a CipherChat pairing code. In CipherChat, type /pair (web) or use Settings → Pair a device to make one.");
        if (ChatServers.Check(parsed.Server.ToString()) is { } why) throw new ArgumentException(why);
        Api = NewApi(parsed.Server);
        var (user, device) = await Api.DeviceSignInAsync(parsed.Token).ConfigureAwait(false);
        Account = new ChatAccount(parsed.Server.GetLeftPart(UriPartial.Authority), user, device, parsed.Token, parsed.Server.Host, DateTime.UtcNow);
        ChatAccounts.Save(Account);
        await RefreshStateAsync().ConfigureAwait(false);
        AuditLog.Write("you", "chat", "this PC paired as a CipherChat device", Account.Display);
    }

    /// <summary>Password + authenticator code (nothing is remembered on this PC; pair it to stay signed in).</summary>
    public async Task SignInAsync(string server, string user, string password, string code)
    {
        if (ChatServers.Check(server) is { } why) throw new ArgumentException(why);
        Account = null;
        Api = NewApi(new Uri(ChatServers.Origin(server)!));
        await Api.LoginAsync(user, password, code).ConfigureAwait(false);
        await RefreshStateAsync().ConfigureAwait(false);
        AuditLog.Write("you", "chat", "signed in (password and code)", Display);
    }

    public async Task RefreshStateAsync()
    {
        State = await Api!.CallAsync("state").ConfigureAwait(false);
        StageChanged?.Invoke();
    }

    /// <summary>First time on any device: makes the identity, sealed with the passphrase, and starts.</summary>
    public async Task CreateIdentityAsync(string passphrase, IProgress<string>? progress = null)
    {
        var me = await Gate.SetupAsync(Api!, State!, passphrase, reset: false, Ask, progress).ConfigureAwait(false);
        await RefreshStateAsync().ConfigureAwait(false);
        await AfterUnlockAsync(me).ConfigureAwait(false);
        AuditLog.Write("you", "chat", "chat identity created", Display);
    }

    /// <summary>Unlocks the identity with the chat passphrase. Next: the owner check (first time) or the chat.</summary>
    public async Task UnlockAsync(string passphrase, IProgress<string>? progress = null)
    {
        var me = await Gate.UnlockAsync(Api!, State!, passphrase, progress).ConfigureAwait(false);
        await AfterUnlockAsync(me).ConfigureAwait(false);
    }

    private async Task AfterUnlockAsync(Me me)
    {
        // A member compares the owner's safety number once and pins it (the owner is the trust anchor).
        if (me.AnchorFp is null && Gate.OwnerIdentity(State!) is not null && State!["me"]?["owner"]?.GetValue<bool>() != true)
        {
            _pendingMe = me;
            StageChanged?.Invoke();
            return;
        }
        await StartAsync(me).ConfigureAwait(false);
    }

    /// <summary>The 60 digits to compare with the owner (in person, or over a channel you trust).</summary>
    public string? OwnerSafetyNumber => _pendingMe is { } me ? Gate.OwnerSafetyNumber(State!, me) : null;

    /// <summary>The safety number matched: pin the owner (needs the passphrase again, to reseal the backup) and start.</summary>
    public async Task ConfirmOwnerAsync(string passphrase)
    {
        var me = _pendingMe ?? throw new InvalidOperationException("Nothing to confirm.");
        await Gate.PinOwnerAsync(Api!, State!, me, passphrase, Ask).ConfigureAwait(false);
        _pendingMe = null;
        AuditLog.Write("you", "chat", "owner's safety number confirmed", Display);
        await StartAsync(me).ConfigureAwait(false);
    }

    private async Task StartAsync(Me me)
    {
        Api!.Ask = Ask;
        var engine = new ChatEngine(Api, me) { Ask = Ask };
        // This PC keeps the room keys it opens (so the server can delete its wrapped copies), sealed by CipherChat to this
        // device and the passphrase, and the file by the app's store key. A store that can't be opened keeps nothing.
        try { engine.UseStore(me.OpenStore(SecureStoreGuard.PathFor(Origin, me.User), new SecureStoreGuard())); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { Log.Write("Chat key store", ex); engine.UseStore(null); }
        Engine = engine;
        Calls.ChatCalls.Attach(this, engine);   // 0.6.38: its calls ring here
        ChatNotifier.Attach(this, engine);      // 0.6.39: its new messages notify (ChatNotify)
        await engine.StartAsync().ConfigureAwait(false);
        AuditLog.Write("you", "chat", "chat unlocked", Display);
        StageChanged?.Invoke();
    }

    /// <summary>Wipes the keys from memory (app lock, chat lock, quitting). The PC stays paired.</summary>
    public void Lock()
    {
        _pendingMe = null;
        if (Engine is not null) Calls.ChatCalls.Detach(this);   // 0.6.38: its ring and call end with it
        if (Engine is { } e) { Engine = null; try { e.Lock(); } catch (Exception ex) { Log.Write("Chat lock", ex); } }
        StageChanged?.Invoke();
    }

    /// <summary>Signs out; <paramref name="forgetPc"/> also removes the pairing from this PC.</summary>
    public async Task SignOutAsync(bool forgetPc)
    {
        Lock();
        if (Api is not null) { try { await Api.LogoutAsync().ConfigureAwait(false); } catch (ApiException) { } }
        if (forgetPc && Account is not null) { ChatAccounts.Remove(Account); AuditLog.Write("you", "chat", "pairing removed from this PC", Account.Display); }
        Api = null; State = null; Account = null;
        StageChanged?.Invoke();
    }
}
