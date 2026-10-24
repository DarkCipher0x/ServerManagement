using System.Net.Http;

namespace DcxManager.Services;

/// <summary>
/// Vault credentials whose secret lives in Bitwarden or BeyondTrust Password Safe (0.6.17, ADR-096; DarkCipher0x: "the
/// password safe and bitwarden creds to be able to be passed to all portions of the software, so hosts, hosts under cloud
/// providers, infra, portals"). Such a credential (<see cref="Credential.External"/>) stores ids and a label only; every
/// place that uses a credential (SSH, SFTP, tunnels, sudo, Remote Desktop, VNC, infrastructure providers, portal sign-in,
/// VPN keys) reads it through here, and gets an in-memory copy with the secret filled in that is never saved, logged or
/// audited (the audit trail records that it was used, not what it was).
/// <para>The app sets <see cref="Resolver"/> (DcxManager CredentialSources): this layer doesn't reference Bitwarden or
/// Password Safe. Inside <see cref="Background"/> (dashboard and scheduled checks) nothing is asked: a locked vault or a
/// Password Safe account without an open checkout fails that check instead of prompting.</para>
/// </summary>
public static class ExternalCredentials
{
    /// <summary>(stored credential, what it is for, may it ask the user, cancel) → an in-memory copy carrying the secret.
    /// Set by the app at start-up.</summary>
    public static Func<Credential, string, bool, CancellationToken, Task<Credential>>? Resolver { get; set; }

    /// <summary>Waits for a task on the UI thread while keeping it pumping (set by the app), so a synchronous use there can
    /// still show the unlock prompt. Off the UI thread the wait simply blocks.</summary>
    public static Action<Task>? WaitOnUi { get; set; }

    private static readonly AsyncLocal<bool> Quiet = new();

    /// <summary>True inside <see cref="Background"/>: resolving never asks the user.</summary>
    public static bool InBackground => Quiet.Value;

    /// <summary>Marks the work started inside the scope (and every await it continues with) as unattended.</summary>
    public static IDisposable Background() { var was = Quiet.Value; Quiet.Value = true; return new Restore(was); }

    private sealed class Restore(bool was) : IDisposable { public void Dispose() => Quiet.Value = was; }

    /// <summary>True when the credential has (or can fetch) a secret: a stored one with text, or any external one.</summary>
    public static bool HasSecret(Credential? c) => c is { IsExternal: true } || c is { Secret.Length: > 0 };

    /// <summary>A host linked straight to a Bitwarden item or a Password Safe account (0.5.0 / 0.6.14), as an external
    /// credential (never saved), so uses other than its session tabs (Files, Manage, tunnels, sudo, checks, its portals)
    /// sign in with it too. Null when the host has no such link.</summary>
    public static Credential? LinkedTo(HostEntry? host) =>
        host is null or { IsShared: true } ? null   // (a shared host's links are never followed)
        : host.BitwardenItem.Length > 0 ? new Credential { Id = "", Name = host.Display, Kind = CredentialKind.Password, External = "bw:" + host.BitwardenItem }
        : host.PasswordSafeAccount.Length > 0 ? new Credential { Id = "", Name = host.Display, Kind = CredentialKind.Password, External = "ps:" + host.PasswordSafeAccount }
        : null;

    /// <summary>The vault credential with this id, its secret read in when it is external. Null when there is none.</summary>
    public static Credential? Use(string? id, string purpose, CancellationToken ct = default) => Resolve(AppData.Current.FindCredential(id), purpose, ct);

    public static Task<Credential?> UseAsync(string? id, string purpose, CancellationToken ct = default) => ResolveAsync(AppData.Current.FindCredential(id), purpose, ct);

    /// <summary>A stored credential as it is; an external one read in (synchronously: pumps the UI thread when called on it).</summary>
    public static Credential? Resolve(Credential? c, string purpose, CancellationToken ct = default)
    {
        if (c is not { IsExternal: true }) return c;
        var quiet = InBackground;
        // Off the caller's context: the resolver's awaits must not need the thread that is waiting for them.
        var t = Task.Run(() => { using (quiet ? Background() : null) return ResolveAsync(c, purpose, ct); }, ct);
        if (!t.IsCompleted && Ui.Thread.HasUi && Ui.Thread.CheckAccess() && WaitOnUi is { } pump) pump(t);
        return t.GetAwaiter().GetResult();
    }

    public static async Task<Credential?> ResolveAsync(Credential? c, string purpose, CancellationToken ct = default)
    {
        if (c is not { IsExternal: true }) return c;
        // A shared library item never points into this PC's vaults (the library drops such links when it loads; this is the backstop).
        if (c.IsShared) throw new InvalidOperationException($"“{c.Name}” comes from a shared library and can't read from this PC's {c.ExternalText}.");
        var r = Resolver ?? throw new InvalidOperationException($"“{c.Name}” is kept in {c.ExternalText} and can't be read here.");
        var got = await r(c, purpose, !InBackground, ct).ConfigureAwait(false);
        if (got.Secret.Length == 0) throw new InvalidOperationException($"{c.ExternalText} returned no secret for “{c.Name}”.");
        return got;
    }
}

/// <summary>
/// Signs each request of an integration's HTTP client from its vault credential, read when the request is sent rather
/// than when the client is built (0.6.17, ADR-096): building a provider (to draw a screen) never asks for Bitwarden's
/// master password, a credential edited since is picked up, and a dashboard check stays unattended
/// (<see cref="ExternalCredentials.Background"/> flows into the send).
/// </summary>
public sealed class CredentialAuthHandler(Func<string?> credentialId, string purpose, Action<HttpRequestMessage, Credential> apply, HttpMessageHandler inner)
    : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (await ExternalCredentials.UseAsync(credentialId(), purpose, ct).ConfigureAwait(false) is { } c) apply(request, c);
        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }
}
